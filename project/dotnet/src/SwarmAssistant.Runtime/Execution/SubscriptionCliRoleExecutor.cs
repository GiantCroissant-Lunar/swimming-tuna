using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Execution;

internal sealed class SubscriptionCliRoleExecutor : IDisposable
{
    private static readonly string[] DefaultAdapterOrder = ["copilot", "cline", "kimi", "kilo", "local-echo"];

    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled);

    private static readonly string[] CommonRejectedOutputSubstrings =
    [
        "authorization failed",
        "check your login status",
        "authentication required",
        "not authenticated",
        "not logged in",
        "please log in",
        "please login",
        "unauthorized"
    ];

    private static readonly Regex RecommendedPlanRegex = new(
        @"Recommended plan:\s*(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, AdapterDefinition> AdapterDefinitions =
        new Dictionary<string, AdapterDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["copilot"] = new(
                "copilot",
                "copilot",
                ["--help"],
                "copilot",
                ["--prompt", "{{prompt}}", "--allow-all-tools", "--allow-all-paths", "--stream", "off", "--no-color"],
                ["deprecated in favor", "No commands will be executed."],
                ModelEnvVar: "COPILOT_MODEL",
                ReasoningEnvVar: "COPILOT_REASONING_EFFORT"),
            ["cline"] = new(
                "cline",
                "cline",
                ["--help"],
                "cline",
                ["{{prompt}}", "--oneshot", "--no-interactive", "--output-format", "plain"],
                [],
                ModelFlag: "--model"),
            ["kimi"] = new(
                "kimi",
                "kimi",
                ["--help"],
                "kimi",
                ["--print", "--prompt", "{{prompt}}"],
                ["token expired", "session expired"],
                ModelFlag: "--model"),
            ["kilo"] = new(
                "kilo",
                "kilo",
                ["run", "--help"],
                "kilo",
                ["run", "{{prompt}}", "--auto"],
                [],
                ModelFlag: "--model",
                ReasoningFlag: "--variant"),
            ["local-echo"] = new(
                "local-echo",
                string.Empty,
                [],
                string.Empty,
                [],
                [],
                IsInternal: true)
        };

    private readonly RuntimeOptions _options;
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly ILogger _logger;
    private readonly SandboxLevelEnforcer _sandboxLevelEnforcer;
    private readonly string _workspacePath;
    private readonly RoleModelMapping _roleModelMapping;

    public SubscriptionCliRoleExecutor(RuntimeOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _concurrencyGate = new SemaphoreSlim(Math.Clamp(options.MaxCliConcurrency, 1, 32));
        _logger = loggerFactory.CreateLogger<SubscriptionCliRoleExecutor>();
        _sandboxLevelEnforcer = new SandboxLevelEnforcer();
        _workspacePath = GetWorkspacePath(options);
        _roleModelMapping = RoleModelMapping.FromOptions(options);
    }

    private static string GetWorkspacePath(RuntimeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.WorkspacePath))
        {
            return options.WorkspacePath;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --show-toplevel",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return output;
                }
            }
        }
        catch
        {
            // Fall through to default
        }

        return Environment.CurrentDirectory;
    }

    public async Task<CliRoleExecutionResult> ExecuteAsync(ExecuteRoleTask command, CancellationToken cancellationToken)
    {
        await _concurrencyGate.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteCoreAsync(command, cancellationToken);
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private string[] BuildAdapterOrder(string? preferredAdapter)
    {
        var baseOrder = _options.CliAdapterOrder.Length > 0
            ? _options.CliAdapterOrder.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToArray()
            : DefaultAdapterOrder;

        // If a preferred adapter is specified and exists, prioritize it
        if (!string.IsNullOrWhiteSpace(preferredAdapter) &&
            AdapterDefinitions.ContainsKey(preferredAdapter))
        {
            // Return preferred adapter first, then the rest (excluding preferred to avoid duplication)
            return [preferredAdapter, .. baseOrder.Where(a => !a.Equals(preferredAdapter, StringComparison.OrdinalIgnoreCase))];
        }

        return baseOrder;
    }

    private async Task<CliRoleExecutionResult> ExecuteCoreAsync(ExecuteRoleTask command, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.RoleExecutionTimeoutSeconds, 5, 900));
        var errors = new List<string>();
        // Use pre-built prompt if provided (with code context), otherwise build from scratch
        var prompt = command.Prompt ?? RolePromptFactory.BuildPrompt(command);
        // Per-task worktree path takes priority over global workspace
        var effectiveWorkspace = string.IsNullOrWhiteSpace(command.WorkspacePath)
            ? _workspacePath
            : command.WorkspacePath;

        var adapterOrder = BuildAdapterOrder(command.PreferredAdapter);

        // Log if preferred adapter is being honored (only when the adapter is actually available)
        if (!string.IsNullOrWhiteSpace(command.PreferredAdapter) &&
            AdapterDefinitions.ContainsKey(command.PreferredAdapter))
        {
            _logger.LogInformation(
                "Using preferred adapter={PreferredAdapter} for role={Role} taskId={TaskId}",
                command.PreferredAdapter,
                command.Role,
                command.TaskId);
        }
        else if (!string.IsNullOrWhiteSpace(command.PreferredAdapter))
        {
            _logger.LogWarning(
                "Preferred adapter={PreferredAdapter} not found, using default order for role={Role} taskId={TaskId}",
                command.PreferredAdapter,
                command.Role,
                command.TaskId);
        }

        foreach (var adapterId in adapterOrder)
        {
            if (!AdapterDefinitions.TryGetValue(adapterId, out var adapter))
            {
                errors.Add($"{adapterId}: adapter not configured");
                continue;
            }

            var resolvedRoleModel = _roleModelMapping.Resolve(command.Role, adapter.Id);

            if (adapter.IsInternal)
            {
                return new CliRoleExecutionResult(
                    BuildInternalEcho(command),
                    adapter.Id,
                    resolvedRoleModel?.Model,
                    resolvedRoleModel?.Reasoning);
            }

            SandboxCommand probeCommand;
            try
            {
                // TODO: Container level currently falls through to the legacy wrapper-based
                // execution path (SandboxCommandBuilder.Build). A future iteration will use
                // ContainerLifecycleManager/ContainerNetworkPolicy for Container-level
                // sandboxing directly from this executor.
                if (_options.SandboxLevel == SandboxLevel.OsSandboxed &&
                    _sandboxLevelEnforcer.CanEnforce(SandboxLevel.OsSandboxed))
                {
                    probeCommand = SandboxCommandBuilder.BuildForLevel(
                        SandboxLevel.OsSandboxed,
                        adapter.ProbeCommand,
                        adapter.ProbeArgs,
                        effectiveWorkspace,
                        _options.SandboxAllowedHosts);
                }
                else
                {
                    probeCommand = SandboxCommandBuilder.Build(_options, adapter.ProbeCommand, adapter.ProbeArgs);
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{adapter.Id}: {exception.Message}");
                continue;
            }

            var probe = await RunProcessAsync(probeCommand, TimeSpan.FromSeconds(10), cancellationToken);
            if (!probe.Ok)
            {
                errors.Add($"{adapter.Id}: unavailable ({probe.ErrorSummary})");
                continue;
            }

            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["prompt"] = prompt,
                ["task_id"] = command.TaskId,
                ["task_title"] = command.Title,
                ["task_description"] = command.Description,
                ["role"] = command.Role.ToString().ToLowerInvariant(),
                ["workspace"] = effectiveWorkspace
            };
            var executeArgs = RenderArgs(adapter.ExecuteArgs, vars).ToList();
            var executionEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ApplyModelExecutionHints(adapter, command.Role, resolvedRoleModel, executeArgs, executionEnvironment);
            SandboxCommand executeCommand;
            try
            {
                if (_options.SandboxLevel == SandboxLevel.OsSandboxed &&
                    _sandboxLevelEnforcer.CanEnforce(SandboxLevel.OsSandboxed))
                {
                    executeCommand = SandboxCommandBuilder.BuildForLevel(
                        SandboxLevel.OsSandboxed,
                        adapter.ExecuteCommand,
                        executeArgs.ToArray(),
                        effectiveWorkspace,
                        _options.SandboxAllowedHosts);
                }
                else
                {
                    executeCommand = SandboxCommandBuilder.Build(_options, adapter.ExecuteCommand, executeArgs.ToArray());
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{adapter.Id}: {exception.Message}");
                continue;
            }

            _logger.LogInformation(
                "Executing role via subscription CLI adapter={AdapterId} role={Role} taskId={TaskId} sandbox={SandboxMode}",
                adapter.Id,
                command.Role,
                command.TaskId,
                _options.SandboxMode);

            var execution = await RunProcessAsync(
                executeCommand,
                timeout,
                cancellationToken,
                effectiveWorkspace,
                executionEnvironment);
            if (!execution.Ok)
            {
                errors.Add($"{adapter.Id}: {execution.ErrorSummary}");
                continue;
            }

            var output = NormalizeOutput(execution.Output);
            if (output.Length == 0)
            {
                errors.Add($"{adapter.Id}: empty output");
                continue;
            }

            var rejection = FindRejectedOutputMatch(output, adapter.RejectOutputSubstrings);
            if (!string.IsNullOrWhiteSpace(rejection))
            {
                errors.Add($"{adapter.Id}: rejected output match ({rejection})");
                continue;
            }

            return new CliRoleExecutionResult(
                output,
                adapter.Id,
                resolvedRoleModel?.Model,
                resolvedRoleModel?.Reasoning);
        }

        throw new InvalidOperationException(
            $"No CLI adapter succeeded for role {command.Role}. {string.Join(" | ", errors)}");
    }

    private static string BuildInternalEcho(ExecuteRoleTask command)
    {
        return command.Role switch
        {
            Contracts.Messaging.SwarmRole.Planner => string.Join(
                Environment.NewLine,
                $"[LocalEcho/Planner] Task: {command.Title}",
                "1. Clarify scope and constraints.",
                "2. Deliver smallest testable slice.",
                "3. Validate edge-case handling.",
                "4. Capture next actions."),
            Contracts.Messaging.SwarmRole.Builder => string.Join(
                Environment.NewLine,
                $"[LocalEcho/Builder] Task: {command.Title}",
                $"Planner context: {command.PlanningOutput ?? "(none)"}",
                "- Implemented core flow with typed message handoff.",
                "- Kept execution deterministic for reproducibility."),
            Contracts.Messaging.SwarmRole.Reviewer => string.Join(
                Environment.NewLine,
                $"[LocalEcho/Reviewer] Task: {command.Title}",
                $"Build context: {command.BuildOutput ?? "(none)"}",
                "- Checked lifecycle transitions and error propagation.",
                "- Suggested coverage for success and escalation paths."),
            Contracts.Messaging.SwarmRole.Orchestrator => BuildOrchestratorEcho(command),
            Contracts.Messaging.SwarmRole.Researcher => string.Join(
                Environment.NewLine,
                $"[LocalEcho/Researcher] Task: {command.Title}",
                "- Gathered contextual constraints and relevant references.",
                "- Highlighted unknowns requiring validation."),
            Contracts.Messaging.SwarmRole.Debugger => string.Join(
                Environment.NewLine,
                $"[LocalEcho/Debugger] Task: {command.Title}",
                "- Isolated likely fault boundaries from recent changes.",
                "- Proposed smallest safe fix candidates."),
            Contracts.Messaging.SwarmRole.Tester => string.Join(
                Environment.NewLine,
                $"[LocalEcho/Tester] Task: {command.Title}",
                "- Added focused positive and negative test scenarios.",
                "- Covered regression checks for impacted paths."),
            _ => $"[LocalEcho] Unsupported role {command.Role}"
        };
    }

    private static string BuildOrchestratorEcho(ExecuteRoleTask command)
    {
        var action = "Plan";
        var reason = "Starting with planning phase as determined by local echo fallback.";

        if (!string.IsNullOrWhiteSpace(command.OrchestratorPrompt))
        {
            var match = RecommendedPlanRegex.Match(command.OrchestratorPrompt);
            if (match.Success)
            {
                action = match.Groups[1].Value;
                reason = $"Following GOAP recommended action '{action}' via local echo.";
            }
        }

        return string.Join(
            Environment.NewLine,
            $"[LocalEcho/Orchestrator] Task: {command.Title}",
            $"ACTION: {action}",
            $"REASON: {reason}");
    }

    private static string[] RenderArgs(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> vars)
    {
        var rendered = new string[args.Count];
        for (var i = 0; i < args.Count; i += 1)
        {
            rendered[i] = ApplyTemplate(args[i], vars);
        }

        return rendered;
    }

    private static string ApplyTemplate(string template, IReadOnlyDictionary<string, string> vars)
    {
        var output = template;
        foreach (var pair in vars)
        {
            output = output.Replace($"{{{{{pair.Key}}}}}", pair.Value, StringComparison.Ordinal);
        }

        return output;
    }

    internal static string NormalizeOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        var withoutAnsi = AnsiEscapeRegex.Replace(output, string.Empty);
        return withoutAnsi.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    internal static string? FindRejectedOutputMatch(string output, IReadOnlyList<string> adapterRejectOutputSubstrings)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var snippet in CommonRejectedOutputSubstrings)
        {
            if (output.Contains(snippet, StringComparison.OrdinalIgnoreCase))
            {
                return snippet;
            }
        }

        foreach (var snippet in adapterRejectOutputSubstrings)
        {
            if (output.Contains(snippet, StringComparison.OrdinalIgnoreCase))
            {
                return snippet;
            }
        }

        return null;
    }

    private static void ApplyModelExecutionHints(
        AdapterDefinition adapter,
        SwarmRole role,
        ResolvedRoleModel? resolvedRoleModel,
        IList<string> executeArgs,
        IDictionary<string, string> environment)
    {
        if (resolvedRoleModel is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(adapter.ModelFlag))
        {
            executeArgs.Add(adapter.ModelFlag);
            executeArgs.Add(resolvedRoleModel.Model.Id);
        }

        if (!string.IsNullOrWhiteSpace(adapter.ModelEnvVar))
        {
            environment[adapter.ModelEnvVar] = resolvedRoleModel.Model.Id;
        }

        if (!string.IsNullOrWhiteSpace(resolvedRoleModel.Reasoning))
        {
            if (!string.IsNullOrWhiteSpace(adapter.ReasoningFlag))
            {
                executeArgs.Add(adapter.ReasoningFlag);
                executeArgs.Add(resolvedRoleModel.Reasoning);
            }

            if (!string.IsNullOrWhiteSpace(adapter.ReasoningEnvVar))
            {
                environment[adapter.ReasoningEnvVar] = resolvedRoleModel.Reasoning;
            }
        }

        var cliMode = ResolveCliMode(role, adapter);
        if (!string.IsNullOrWhiteSpace(cliMode) && !string.IsNullOrWhiteSpace(adapter.ModeFlag))
        {
            executeArgs.Add(adapter.ModeFlag);
            executeArgs.Add(cliMode);
        }
    }

    private static string? ResolveCliMode(SwarmRole role, AdapterDefinition adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter.ModeFlag))
        {
            return null;
        }

        return role switch
        {
            SwarmRole.Planner => "plan",
            SwarmRole.Reviewer => "plan",
            SwarmRole.Researcher => "plan",
            SwarmRole.Orchestrator => "plan",
            SwarmRole.Builder => "act",
            SwarmRole.Debugger => "act",
            SwarmRole.Tester => "act",
            _ => null
        };
    }

    private async Task<ProcessResult> RunProcessAsync(
        SandboxCommand sandboxCommand,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sandboxCommand.Command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? string.Empty
            }
        };

        foreach (var arg in sandboxCommand.Args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(false, string.Empty, "failed to start process");
            }
        }
        catch (Exception exception)
        {
            return new ProcessResult(false, string.Empty, exception.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            await TryGracefulKillAsync(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        var output = stdout.ToString();
        var error = stderr.ToString().Trim();
        if (timedOut)
        {
            return new ProcessResult(false, output, "execution timeout");
        }

        if (process.ExitCode != 0)
        {
            var summary = error.Length > 0 ? error : $"exit code {process.ExitCode}";
            return new ProcessResult(false, output, summary);
        }

        return new ProcessResult(true, output, string.Empty);
    }

    private async Task TryGracefulKillAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            // First attempt: kill only the main process to allow child processes to receive natural
            // termination signals and clean up before escalating to the entire process tree.
            process.Kill(entireProcessTree: false);

            // Allow a short grace period before escalating.
            using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(graceCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Exception while terminating timed-out process.");
        }
    }

    private sealed record AdapterDefinition(
        string Id,
        string ProbeCommand,
        string[] ProbeArgs,
        string ExecuteCommand,
        string[] ExecuteArgs,
        string[] RejectOutputSubstrings,
        bool IsInternal = false,
        string? ModelFlag = null,
        string? ModelEnvVar = null,
        string? ModeFlag = null,
        string? ReasoningFlag = null,
        string? ReasoningEnvVar = null);

    private sealed record ProcessResult(bool Ok, string Output, string ErrorSummary);

    public void Dispose()
    {
        _concurrencyGate.Dispose();
    }
}

internal sealed record CliRoleExecutionResult(
    string Output,
    string AdapterId,
    ModelSpec? Model = null,
    string? Reasoning = null);
