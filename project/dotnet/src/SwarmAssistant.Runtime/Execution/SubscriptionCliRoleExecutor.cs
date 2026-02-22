using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Execution;

internal sealed class SubscriptionCliRoleExecutor
{
    private static readonly string[] DefaultAdapterOrder = ["copilot", "cline", "kimi", "local-echo"];

    private static readonly IReadOnlyDictionary<string, AdapterDefinition> AdapterDefinitions =
        new Dictionary<string, AdapterDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["copilot"] = new(
                "copilot",
                "copilot",
                ["--help"],
                "copilot",
                ["--prompt", "{{prompt}}", "--allow-all-tools", "--allow-all-paths", "--stream", "off", "--no-color"],
                ["deprecated in favor", "No commands will be executed."]),
            ["cline"] = new(
                "cline",
                "cline",
                ["--help"],
                "cline",
                ["{{prompt}}", "--oneshot", "--no-interactive", "--output-format", "plain"],
                []),
            ["kimi"] = new(
                "kimi",
                "kimi",
                ["--help"],
                "kimi",
                ["--prompt", "{{prompt}}"],
                []),
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

    public SubscriptionCliRoleExecutor(RuntimeOptions options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _concurrencyGate = new SemaphoreSlim(Math.Clamp(options.MaxCliConcurrency, 1, 32));
        _logger = loggerFactory.CreateLogger<SubscriptionCliRoleExecutor>();
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

    private async Task<CliRoleExecutionResult> ExecuteCoreAsync(ExecuteRoleTask command, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_options.RoleExecutionTimeoutSeconds, 5, 900));
        var errors = new List<string>();
        var prompt = RolePromptFactory.BuildPrompt(command);

        var adapterOrder = _options.CliAdapterOrder.Length > 0 ? _options.CliAdapterOrder : DefaultAdapterOrder;
        foreach (var adapterId in adapterOrder)
        {
            if (!AdapterDefinitions.TryGetValue(adapterId, out var adapter))
            {
                errors.Add($"{adapterId}: adapter not configured");
                continue;
            }

            if (adapter.IsInternal)
            {
                return new CliRoleExecutionResult(BuildInternalEcho(command), adapter.Id);
            }

            SandboxCommand probeCommand;
            try
            {
                probeCommand = SandboxCommandBuilder.Build(_options, adapter.ProbeCommand, adapter.ProbeArgs);
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
                ["role"] = command.Role.ToString().ToLowerInvariant()
            };
            var executeArgs = RenderArgs(adapter.ExecuteArgs, vars);
            SandboxCommand executeCommand;
            try
            {
                executeCommand = SandboxCommandBuilder.Build(_options, adapter.ExecuteCommand, executeArgs);
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

            var execution = await RunProcessAsync(executeCommand, timeout, cancellationToken);
            if (!execution.Ok)
            {
                errors.Add($"{adapter.Id}: {execution.ErrorSummary}");
                continue;
            }

            var output = execution.Output.Trim();
            if (output.Length == 0)
            {
                errors.Add($"{adapter.Id}: empty output");
                continue;
            }

            var rejection = adapter.RejectOutputSubstrings.FirstOrDefault(snippet =>
                output.Contains(snippet, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(rejection))
            {
                errors.Add($"{adapter.Id}: rejected output match ({rejection})");
                continue;
            }

            return new CliRoleExecutionResult(output, adapter.Id);
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
                "3. Validate failure handling.",
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
            _ => $"[LocalEcho] Unsupported role {command.Role}"
        };
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

    private async Task<ProcessResult> RunProcessAsync(
        SandboxCommand sandboxCommand,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = sandboxCommand.Command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var arg in sandboxCommand.Args)
        {
            process.StartInfo.ArgumentList.Add(arg);
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
        bool IsInternal = false);

    private sealed record ProcessResult(bool Ok, string Output, string ErrorSummary);
}

internal sealed record CliRoleExecutionResult(string Output, string AdapterId);
