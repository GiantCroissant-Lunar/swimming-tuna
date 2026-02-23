using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime.Execution;

internal static class RolePromptFactory
{
    public static string BuildPrompt(ExecuteRoleTask command)
    {
        return BuildPrompt(command, strategyAdvice: null);
    }

    public static string BuildPrompt(ExecuteRoleTask command, StrategyAdvice? strategyAdvice)
    {
        var basePrompt = command.Role switch
        {
            SwarmRole.Planner => string.Join(
                Environment.NewLine,
                "You are the planner agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Task description: {command.Description}",
                "Return a concise implementation plan with risks and validation steps."),
            SwarmRole.Builder => string.Join(
                Environment.NewLine,
                "You are the builder agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Task description: {command.Description}",
                $"Planner output: {command.PlanningOutput ?? "(none)"}",
                "Return concrete implementation notes and execution details."),
            SwarmRole.Reviewer => string.Join(
                Environment.NewLine,
                "You are the reviewer agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Planner output: {command.PlanningOutput ?? "(none)"}",
                $"Builder output: {command.BuildOutput ?? "(none)"}",
                "Find defects, risks, and missing tests. Keep it specific."),
            SwarmRole.Orchestrator => command.OrchestratorPrompt
                ?? "You are the orchestrator agent. Decide the next action. Respond with ACTION: <action> and REASON: <reason>.",
            SwarmRole.Researcher => string.Join(
                Environment.NewLine,
                "You are the researcher agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Task description: {command.Description}",
                "Collect relevant facts and references that de-risk implementation decisions."),
            SwarmRole.Debugger => string.Join(
                Environment.NewLine,
                "You are the debugger agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Task description: {command.Description}",
                $"Builder output: {command.BuildOutput ?? "(none)"}",
                "Identify likely failure points, root causes, and minimal fixes."),
            SwarmRole.Tester => string.Join(
                Environment.NewLine,
                "You are the tester agent in a swarm runtime.",
                $"Task title: {command.Title}",
                $"Task description: {command.Description}",
                $"Planner output: {command.PlanningOutput ?? "(none)"}",
                $"Builder output: {command.BuildOutput ?? "(none)"}",
                "Propose focused test cases that validate functionality and regressions."),
            _ => $"Unsupported role {command.Role}"
        };

        // Append historical context if available for relevant roles
        if (strategyAdvice is not null &&
            strategyAdvice.SimilarTaskCount > 0 &&
            command.Role is SwarmRole.Planner or SwarmRole.Builder or SwarmRole.Reviewer)
        {
            var historicalContext = BuildHistoricalContext(strategyAdvice);
            return string.Join(Environment.NewLine, basePrompt, string.Empty, historicalContext);
        }

        return basePrompt;
    }

    /// <summary>
    /// Builds a historical context section from strategy advice.
    /// </summary>
    private static string BuildHistoricalContext(StrategyAdvice advice)
    {
        var lines = new List<string>
        {
            "--- Historical Insights ---",
            $"Based on {advice.SimilarTaskCount} similar past tasks:",
            $"  Success rate: {advice.SimilarTaskSuccessRate:P0}",
        };

        if (advice.AverageRetryCount > 0)
        {
            lines.Add($"  Average retries: {advice.AverageRetryCount:F1}");
        }

        if (advice.ReviewRejectionRate > 0.1)
        {
            lines.Add($"  Review rejection rate: {advice.ReviewRejectionRate:P0}");
        }

        // Add insights
        foreach (var insight in advice.Insights)
        {
            lines.Add($"  • {insight}");
        }

        // Add adapter recommendations if available
        if (advice.AdapterSuccessRates is { Count: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add("Adapter performance for similar tasks:");
            foreach (var (adapter, rate) in advice.AdapterSuccessRates.OrderByDescending(kv => kv.Value))
            {
                lines.Add($"  {adapter}: {rate:P0} success rate");
            }
        }

        // Add common failure patterns if available
        if (advice.CommonFailurePatterns is { Count: > 0 })
        {
            lines.Add(string.Empty);
            lines.Add("Common failure patterns to avoid:");
            foreach (var pattern in advice.CommonFailurePatterns)
            {
                lines.Add($"  • {pattern}");
            }
        }

        lines.Add("--- End Historical Insights ---");

        return string.Join(Environment.NewLine, lines);
    }

    public static string BuildOrchestratorPrompt(
        string taskId,
        string title,
        string description,
        string goapContext,
        IReadOnlyDictionary<string, string>? blackboardEntries,
        IReadOnlyDictionary<string, string>? globalBlackboardEntries = null)
    {
        var lines = new List<string>
        {
            $"You are the orchestrator agent for task '{title}'.",
            $"Task ID: {taskId}",
            $"Task description: {description}",
            string.Empty,
            goapContext,
            string.Empty,
        };

        if (blackboardEntries is { Count: > 0 })
        {
            lines.Add("Task history:");
            foreach (var (key, value) in blackboardEntries)
            {
                lines.Add($"  {key}: {value}");
            }

            lines.Add(string.Empty);
        }

        // Include global blackboard context for stigmergy (cross-task coordination)
        if (globalBlackboardEntries is { Count: > 0 })
        {
            lines.Add("Swarm intelligence signals:");
            foreach (var (key, value) in globalBlackboardEntries)
            {
                lines.Add($"  {key}: {value}");
            }

            lines.Add(string.Empty);
        }

        lines.Add("What should happen next? Choose ONE action from the GOAP plan and explain why.");
        lines.Add("Respond in this format:");
        lines.Add("ACTION: <action name>");
        lines.Add("REASON: <brief explanation>");

        return string.Join(Environment.NewLine, lines);
    }
}
