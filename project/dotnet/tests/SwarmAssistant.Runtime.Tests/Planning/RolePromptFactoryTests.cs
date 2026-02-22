using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests.Planning;

public sealed class RolePromptFactoryTests
{
    [Fact]
    public void BuildOrchestratorPrompt_IncludesTaskContext()
    {
        var prompt = RolePromptFactory.BuildOrchestratorPrompt(
            "task-1",
            "My Task",
            "Build something",
            "GOAP Analysis:\n  Recommended plan: Plan → Build (total cost: 4)",
            null);

        Assert.Contains("orchestrator agent", prompt);
        Assert.Contains("My Task", prompt);
        Assert.Contains("Build something", prompt);
        Assert.Contains("GOAP Analysis:", prompt);
        Assert.Contains("ACTION:", prompt);
    }

    [Fact]
    public void BuildOrchestratorPrompt_IncludesBlackboardEntries()
    {
        var blackboard = new Dictionary<string, string>
        {
            ["planner_output"] = "Plan: step 1, step 2",
            ["builder_output"] = "Built the thing",
        };

        var prompt = RolePromptFactory.BuildOrchestratorPrompt(
            "task-1",
            "My Task",
            "Build something",
            "GOAP Analysis: ...",
            blackboard);

        Assert.Contains("Task history:", prompt);
        Assert.Contains("planner_output:", prompt);
        Assert.Contains("Plan: step 1, step 2", prompt);
    }

    [Fact]
    public void BuildOrchestratorPrompt_EmptyBlackboard_NoHistorySection()
    {
        var prompt = RolePromptFactory.BuildOrchestratorPrompt(
            "task-1",
            "My Task",
            "Build something",
            "GOAP Analysis: ...",
            new Dictionary<string, string>());

        Assert.DoesNotContain("Task history:", prompt);
    }
}
