using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;

namespace SwarmAssistant.Runtime.Execution;

internal static class RolePromptFactory
{
    public static string BuildPrompt(ExecuteRoleTask command)
    {
        return command.Role switch
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
