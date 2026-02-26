using System.Diagnostics;
using Microsoft.Agents.AI.Workflows;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Actors;

public sealed class AgentFrameworkRoleEngine
{
    private readonly RuntimeOptions _options;
    private readonly SubscriptionCliRoleExecutor _subscriptionCliRoleExecutor;
    private readonly RuntimeTelemetry _telemetry;
    private readonly ILogger _logger;

    public AgentFrameworkRoleEngine(RuntimeOptions options, ILoggerFactory loggerFactory, RuntimeTelemetry telemetry)
    {
        _options = options;
        _subscriptionCliRoleExecutor = new SubscriptionCliRoleExecutor(options, loggerFactory);
        _telemetry = telemetry;
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

        try
        {
            return mode switch
            {
                "in-process-workflow" => await ExecuteInProcessWorkflowAsync(command, activity, cancellationToken),
                "subscription-cli-fallback" => await ExecuteSubscriptionCliAsync(command, activity, cancellationToken),
                _ => throw new InvalidOperationException(
                    $"Unsupported AgentFrameworkExecutionMode '{_options.AgentFrameworkExecutionMode}'.")
            };
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
    }

    private async Task<CliRoleExecutionResult> ExecuteInProcessWorkflowAsync(
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

        return new CliRoleExecutionResult(output, executor.LastAdapterId ?? string.Empty);
    }

    private async Task<CliRoleExecutionResult> ExecuteSubscriptionCliAsync(
        ExecuteRoleTask command,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionCliRoleExecutor.ExecuteAsync(command, cancellationToken);
        activity?.SetTag("agent.framework.cli.adapter", result.AdapterId);
        activity?.SetTag("output.length", result.Output.Length);
        activity?.SetStatus(ActivityStatusCode.Ok);

        _logger.LogInformation(
            "Role completed through subscription CLI adapter={AdapterId} role={Role} taskId={TaskId}",
            result.AdapterId,
            command.Role,
            command.TaskId);

        return result;
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
