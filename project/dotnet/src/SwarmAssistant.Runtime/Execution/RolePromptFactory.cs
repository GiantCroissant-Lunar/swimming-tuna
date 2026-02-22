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
            _ => $"Unsupported role {command.Role}"
        };
    }
}
