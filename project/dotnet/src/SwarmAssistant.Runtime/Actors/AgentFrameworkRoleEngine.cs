using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Actors;

public sealed class AgentFrameworkRoleEngine
{
    private readonly ILogger _logger;

    public AgentFrameworkRoleEngine(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AgentFrameworkRoleEngine>();
    }

    internal async Task<string> ExecuteAsync(ExecuteRoleTask command, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Executing role with Agent Framework workflow role={Role} taskId={TaskId}",
            command.Role,
            command.TaskId);

        var executor = new RoleExecutionExecutor("agent-framework-role-executor");
        var workflow = new WorkflowBuilder(executor)
            .WithOutputFrom(executor)
            .Build();

        await using StreamingRun run = await InProcessExecution.StreamAsync(
            workflow,
            command,
            cancellationToken: cancellationToken);

        string? output = null;

        await foreach (WorkflowEvent evt in run.WatchStreamAsync(cancellationToken))
        {
            if (evt is WorkflowOutputEvent outputEvent && outputEvent.As<string>() is { } value)
            {
                output = value;
            }
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            _logger.LogInformation(
                "Agent Framework workflow completed role={Role} taskId={TaskId}",
                command.Role,
                command.TaskId);
            return output;
        }

        throw new InvalidOperationException($"Agent Framework workflow returned no output for role {command.Role}.");
    }

    private sealed class RoleExecutionExecutor : Executor<ExecuteRoleTask>
    {
        public RoleExecutionExecutor(string id) : base(id)
        {
        }

        public override async ValueTask HandleAsync(
            ExecuteRoleTask message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            var output = message.Role switch
            {
                SwarmRole.Planner => BuildPlannerOutput(message),
                SwarmRole.Builder => BuildBuilderOutput(message),
                SwarmRole.Reviewer => BuildReviewerOutput(message),
                _ => $"Unsupported role {message.Role}"
            };

            await context.YieldOutputAsync(output, cancellationToken);
        }

        private static string BuildPlannerOutput(ExecuteRoleTask message)
        {
            return string.Join(
                Environment.NewLine,
                $"[AgentFramework/Planner] Task: {message.Title}",
                $"Goal: {message.Description}",
                "Plan:",
                "1. Capture explicit constraints and success criteria.",
                "2. Build the smallest testable implementation slice.",
                "3. Validate against failure paths and rollback options.",
                "4. Produce concise delivery notes and next steps.");
        }

        private static string BuildBuilderOutput(ExecuteRoleTask message)
        {
            return string.Join(
                Environment.NewLine,
                $"[AgentFramework/Builder] Task: {message.Title}",
                $"Using plan context: {message.PlanningOutput ?? "(none)"}",
                "Execution:",
                "- Implemented the core workflow step with durable state transitions.",
                "- Preserved actor boundaries and typed message contracts.",
                "- Prepared logs for supervisor visibility and escalation handling.");
        }

        private static string BuildReviewerOutput(ExecuteRoleTask message)
        {
            return string.Join(
                Environment.NewLine,
                $"[AgentFramework/Reviewer] Task: {message.Title}",
                $"Build context: {message.BuildOutput ?? "(none)"}",
                "Review:",
                "- Verified lifecycle transitions and blocked-state handling.",
                "- Checked role ownership boundaries and failure propagation.",
                "- Recommended adding scenario tests for escalation paths.");
        }
    }
}
