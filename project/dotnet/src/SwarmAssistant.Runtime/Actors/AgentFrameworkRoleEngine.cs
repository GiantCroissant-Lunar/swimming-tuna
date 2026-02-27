using System.Diagnostics;
using Microsoft.Agents.AI.Workflows;
using SwarmAssistant.Contracts.Hierarchy;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;
using ExecutionTokenUsage = SwarmAssistant.Runtime.Execution.TokenUsage;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Hierarchy;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Actors;

internal sealed record RoleExecutionResult(
    CliRoleExecutionResult Result,
    Contracts.Hierarchy.TokenUsage? Usage,
    decimal? CostUsd,
    string? AdapterId);

public sealed class AgentFrameworkRoleEngine
{
    private readonly RuntimeOptions _options;
    private readonly SubscriptionCliRoleExecutor _subscriptionCliRoleExecutor;
    private readonly RoleModelMapping _roleModelMapping;
    private readonly IReadOnlyDictionary<string, IModelProvider> _modelProviders;
    private readonly RuntimeTelemetry _telemetry;
    private readonly AgentSpanCollector _spanCollector;
    private readonly ILogger _logger;

    public AgentFrameworkRoleEngine(
        RuntimeOptions options,
        ILoggerFactory loggerFactory,
        RuntimeTelemetry telemetry,
        IReadOnlyList<IModelProvider>? modelProviders = null)
    {
        _options = options;
        _subscriptionCliRoleExecutor = new SubscriptionCliRoleExecutor(options, loggerFactory);
        _roleModelMapping = RoleModelMapping.FromOptions(options);
        _modelProviders = (modelProviders ?? BuildDefaultModelProviders(options, loggerFactory))
            .ToDictionary(
                provider => provider.ProviderId,
                provider => provider,
                StringComparer.OrdinalIgnoreCase);
        _telemetry = telemetry;
        _spanCollector = new AgentSpanCollector();
        _logger = loggerFactory.CreateLogger<AgentFrameworkRoleEngine>();
    }

    internal async Task<CliRoleExecutionResult> ExecuteAsync(ExecuteRoleTask command, CancellationToken cancellationToken = default)
    {
        var mode = (_options.AgentFrameworkExecutionMode ?? "in-process-workflow").Trim().ToLowerInvariant();

        using var activity = _telemetry.StartActivity(
            "agent-framework.role.execute",
            taskId: command.TaskId,
            role: command.Role.ToString().ToLowerInvariant(),
            tags: new Dictionary<string, object?>
            {
                ["agent.framework.mode"] = mode,
            });

        var spanKind = ResolveSpanKindForExecution(mode, command.Role);
        var span = _spanCollector.StartSpan(
            command.TaskId,
            command.RunId,
            command.Role,
            spanKind,
            null,
            null);

        CliRoleExecutionResult? result = null;
        Contracts.Hierarchy.TokenUsage? usage = null;
        decimal? costUsd = null;
        string? adapterId = null;

        try
        {
            var executionResult = mode switch
            {
                "in-process-workflow" => await ExecuteInProcessWorkflowAsync(command, activity, cancellationToken),
                "subscription-cli-fallback" => await ExecuteSubscriptionCliAsync(command, activity, cancellationToken),
                "api-direct" => await ExecuteApiDirectAsync(command, activity, cancellationToken),
                "hybrid" => await ExecuteHybridAsync(command, activity, cancellationToken),
                _ => throw new InvalidOperationException(
                    $"Unsupported AgentFrameworkExecutionMode '{_options.AgentFrameworkExecutionMode}'.")
            };

            result = executionResult.Result;
            usage = executionResult.Usage;
            costUsd = executionResult.CostUsd;
            adapterId = executionResult.AdapterId;

            return result;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            _spanCollector.CompleteSpan(span.SpanId, AgentSpanStatus.Failed);
            throw;
        }
        finally
        {
            if (result is not null)
            {
                _spanCollector.CompleteSpan(span.SpanId, AgentSpanStatus.Completed, usage, costUsd, adapterId);
            }
        }
    }

    private AgentSpanKind ResolveSpanKindForExecution(string mode, SwarmRole role)
    {
        return mode switch
        {
            "in-process-workflow" => AgentSpanKind.CliAgent,
            "subscription-cli-fallback" => AgentSpanKind.CliAgent,
            "api-direct" => AgentSpanKind.ApiAgent,
            "hybrid" => WillHybridUseApiDirect(role) ? AgentSpanKind.ApiAgent : AgentSpanKind.CliAgent,
            _ => AgentSpanKind.CliAgent
        };
    }

    private bool WillHybridUseApiDirect(SwarmRole role)
    {
        var resolvedRoleModel = _roleModelMapping.Resolve(role);
        return resolvedRoleModel is not null &&
            _modelProviders.ContainsKey(resolvedRoleModel.Model.Provider);
    }

    private async Task<RoleExecutionResult> ExecuteInProcessWorkflowAsync(
        ExecuteRoleTask command,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Executing role with Agent Framework CLI workflow role={Role} taskId={TaskId}",
            command.Role,
            command.TaskId);

        var executor = new CliWorkflowExecutor(
            "agent-framework-cli-executor",
            _subscriptionCliRoleExecutor,
            _logger);
        var workflow = new WorkflowBuilder(executor)
            .WithOutputFrom(executor)
            .Build();

        await using StreamingRun run = await InProcessExecution.StreamAsync(
            workflow,
            command,
            cancellationToken: cancellationToken);

        string? output = null;
        var eventCount = 0;

        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cancellationToken))
        {
            eventCount += 1;

            if (evt is WorkflowOutputEvent outputEvent && outputEvent.As<string>() is { } value)
            {
                output = value;
            }
        }

        activity?.SetTag("agent.framework.workflow.event_count", eventCount);
        activity?.SetTag("agent.framework.cli.adapter", executor.LastAdapterId);

        if (string.IsNullOrWhiteSpace(output))
        {
            const string error = "Agent Framework workflow returned no output.";
            throw new InvalidOperationException($"{error} role={command.Role}");
        }

        activity?.SetTag("output.length", output.Length);
        activity?.SetStatus(ActivityStatusCode.Ok);
        _logger.LogInformation(
            "Agent Framework CLI workflow completed role={Role} taskId={TaskId}",
            command.Role,
            command.TaskId);

        return new RoleExecutionResult(
            new CliRoleExecutionResult(output, executor.LastAdapterId ?? string.Empty),
            null,
            null,
            executor.LastAdapterId);
    }

    private async Task<RoleExecutionResult> ExecuteSubscriptionCliAsync(
        ExecuteRoleTask command,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionCliRoleExecutor.ExecuteAsync(command, cancellationToken);
        activity?.SetTag("agent.framework.cli.adapter", result.AdapterId);
        activity?.SetTag("output.length", result.Output.Length);
        if (result.Model is not null)
        {
            activity?.SetTag("gen_ai.request.model", result.Model.Id);
            activity?.SetTag("gen_ai.request.provider", result.Model.Provider);
        }
        if (!string.IsNullOrWhiteSpace(result.Reasoning))
        {
            activity?.SetTag("gen_ai.request.reasoning", result.Reasoning);
        }
        activity?.SetStatus(ActivityStatusCode.Ok);

        _logger.LogInformation(
            "Role completed through subscription CLI adapter={AdapterId} role={Role} taskId={TaskId} model={ModelId}",
            result.AdapterId,
            command.Role,
            command.TaskId,
            result.Model?.Id ?? "(default)");

        return new RoleExecutionResult(
            result,
            null,
            null,
            result.AdapterId);
    }

    private async Task<RoleExecutionResult> ExecuteApiDirectAsync(
        ExecuteRoleTask command,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var resolvedRoleModel = _roleModelMapping.Resolve(command.Role);
        if (resolvedRoleModel is null)
        {
            throw new InvalidOperationException(
                $"api-direct mode requires Runtime__RoleModelMapping for role {command.Role}.");
        }

        if (!_modelProviders.TryGetValue(resolvedRoleModel.Model.Provider, out var provider))
        {
            throw new InvalidOperationException(
                $"No model provider registered for '{resolvedRoleModel.Model.Provider}'.");
        }

        var prompt = command.Prompt ?? RolePromptFactory.BuildPrompt(command);
        var modelOptions = new ModelExecutionOptions
        {
            Reasoning = resolvedRoleModel.Reasoning
        };
        var response = await provider.ExecuteAsync(resolvedRoleModel.Model, prompt, modelOptions, cancellationToken);

        activity?.SetTag("gen_ai.request.provider", provider.ProviderId);
        activity?.SetTag("gen_ai.request.model", resolvedRoleModel.Model.Id);
        if (!string.IsNullOrWhiteSpace(resolvedRoleModel.Reasoning))
        {
            activity?.SetTag("gen_ai.request.reasoning", resolvedRoleModel.Reasoning);
        }
        var cost = CalculateCostUsd(resolvedRoleModel.Model, response.Usage);

        activity?.SetTag("gen_ai.usage.input_tokens", response.Usage.InputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", response.Usage.OutputTokens);
        activity?.SetTag("gen_ai.usage.cache_read_tokens", response.Usage.CacheReadTokens);
        activity?.SetTag("gen_ai.usage.cache_write_tokens", response.Usage.CacheWriteTokens);
        activity?.SetTag("gen_ai.usage.cost_usd", cost);
        activity?.SetTag("gen_ai.request.messages",
            prompt.Length > 10000 ? prompt[..10000] + "...[truncated]" : prompt);
        activity?.SetTag("gen_ai.response.content",
            response.Output.Length > 10000 ? response.Output[..10000] + "...[truncated]" : response.Output);
        activity?.SetTag("output.length", response.Output.Length);
        activity?.SetStatus(ActivityStatusCode.Ok);

        _logger.LogInformation(
            "Role completed through api-direct provider={Provider} role={Role} taskId={TaskId} model={Model}",
            provider.ProviderId,
            command.Role,
            command.TaskId,
            resolvedRoleModel.Model.Id);

        var usage = ConvertTokenUsage(response.Usage);

        return new RoleExecutionResult(
            new CliRoleExecutionResult(
                response.Output,
                AdapterId: $"api-{provider.ProviderId}",
                Model: resolvedRoleModel.Model,
                Reasoning: resolvedRoleModel.Reasoning),
            usage,
            cost,
            $"api-{provider.ProviderId}");
    }

    private static Contracts.Hierarchy.TokenUsage ConvertTokenUsage(ExecutionTokenUsage usage)
    {
        return new Contracts.Hierarchy.TokenUsage
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadTokens = usage.CacheReadTokens > 0 ? usage.CacheReadTokens : null,
            CacheWriteTokens = usage.CacheWriteTokens > 0 ? usage.CacheWriteTokens : null
        };
    }

    private async Task<RoleExecutionResult> ExecuteHybridAsync(
        ExecuteRoleTask command,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var resolvedRoleModel = _roleModelMapping.Resolve(command.Role);
        if (resolvedRoleModel is not null &&
            _modelProviders.ContainsKey(resolvedRoleModel.Model.Provider))
        {
            return await ExecuteApiDirectAsync(command, activity, cancellationToken);
        }

        return await ExecuteSubscriptionCliAsync(command, activity, cancellationToken);
    }

    private static IReadOnlyList<IModelProvider> BuildDefaultModelProviders(RuntimeOptions options, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger<AgentFrameworkRoleEngine>();
        var providers = new List<IModelProvider>();
        foreach (var configuredProvider in options.ApiProviderOrder.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(configuredProvider))
            {
                continue;
            }

            var providerId = configuredProvider.Trim();
            var normalizedProviderId = providerId.StartsWith("api-", StringComparison.OrdinalIgnoreCase)
                ? providerId["api-".Length..]
                : providerId;

            if (normalizedProviderId.Equals("openai", StringComparison.OrdinalIgnoreCase))
            {
                var apiKey = Environment.GetEnvironmentVariable(options.OpenAiApiKeyEnvVar);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    logger.LogWarning(
                        "Model provider '{ProviderId}' skipped because env var '{ApiKeyEnvVar}' is not set.",
                        providerId,
                        options.OpenAiApiKeyEnvVar);
                    continue;
                }

                providers.Add(new OpenAiModelProvider(
                    apiKey,
                    options.OpenAiBaseUrl,
                    options.OpenAiRequestTimeoutSeconds));
                continue;
            }

            if (normalizedProviderId.Equals("anthropic", StringComparison.OrdinalIgnoreCase))
            {
                var apiKey = Environment.GetEnvironmentVariable(options.AnthropicApiKeyEnvVar);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    logger.LogWarning(
                        "Model provider '{ProviderId}' skipped because env var '{ApiKeyEnvVar}' is not set.",
                        providerId,
                        options.AnthropicApiKeyEnvVar);
                    continue;
                }

                providers.Add(new AnthropicModelProvider(
                    apiKey,
                    options.AnthropicBaseUrl,
                    options.AnthropicRequestTimeoutSeconds));
                continue;
            }

            logger.LogWarning("Unrecognized model provider configured: {ProviderId}", providerId);
        }

        logger.LogInformation("Model providers registered: {Providers}", string.Join(",", providers.Select(p => p.ProviderId)));
        return providers;
    }

    private decimal CalculateCostUsd(ModelSpec model, ExecutionTokenUsage usage)
    {
        var cost = model.Cost;
        if (cost.InputPerMillionTokens == 0m &&
            cost.OutputPerMillionTokens == 0m &&
            cost.CacheReadPerMillionTokens == 0m)
        {
            _logger.LogWarning(
                "Cost data unavailable for model={ModelId}; reporting unknown cost sentinel.",
                model.Id);
            return -1m;
        }

        var inputCost = (usage.InputTokens / 1_000_000m) * cost.InputPerMillionTokens;
        var outputCost = (usage.OutputTokens / 1_000_000m) * cost.OutputPerMillionTokens;
        var cacheReadCost = (usage.CacheReadTokens / 1_000_000m) * cost.CacheReadPerMillionTokens;
        return inputCost + outputCost + cacheReadCost;
    }

    /// <summary>
    /// CLI-backed workflow executor that delegates role execution to <see cref="SubscriptionCliRoleExecutor"/>.
    /// Supports multi-turn retry: if the CLI returns empty or invalid output, retries up to <c>MaxAttempts</c>.
    /// </summary>
    private sealed class CliWorkflowExecutor : Executor<ExecuteRoleTask>
    {
        private const int MaxAttempts = 2;

        private readonly SubscriptionCliRoleExecutor _cliExecutor;
        private readonly ILogger _logger;

        /// <summary>The adapter ID that produced the accepted output (set after a successful execution).</summary>
        internal string? LastAdapterId { get; private set; }

        public CliWorkflowExecutor(
            string id,
            SubscriptionCliRoleExecutor cliExecutor,
            ILogger logger) : base(id)
        {
            _cliExecutor = cliExecutor;
            _logger = logger;
        }

        public override async ValueTask HandleAsync(
            ExecuteRoleTask message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            Exception? lastException = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    var result = await _cliExecutor.ExecuteAsync(message, cancellationToken);

                    if (!string.IsNullOrWhiteSpace(result.Output))
                    {
                        LastAdapterId = result.AdapterId;
                        await context.YieldOutputAsync(result.Output, cancellationToken);
                        return;
                    }

                    _logger.LogWarning(
                        "CLI returned empty output, attempt {Attempt}/{Max} role={Role} taskId={TaskId}",
                        attempt,
                        MaxAttempts,
                        message.Role,
                        message.TaskId);
                }
                catch (Exception exception) when (attempt < MaxAttempts)
                {
                    lastException = exception;
                    _logger.LogWarning(
                        exception,
                        "CLI execution failed, retrying attempt {Attempt}/{Max} role={Role} taskId={TaskId}",
                        attempt,
                        MaxAttempts,
                        message.Role,
                        message.TaskId);
                }
            }

            throw lastException ?? new InvalidOperationException(
                $"CLI workflow returned no valid output after {MaxAttempts} attempts for role {message.Role}");
        }
    }
}
