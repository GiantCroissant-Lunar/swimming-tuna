using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class RolePromptFactoryTests
{
    private static ExecuteRoleTask CreateTask(SwarmRole role) =>
        new(
            TaskId: "test-1",
            Role: role,
            Title: "Implement feature X",
            Description: "Build a new feature for the system",
            PlanningOutput: "Step 1: do A, Step 2: do B",
            BuildOutput: "Built A and B successfully");

    [Fact]
    public void Planner_WithProjectContext_IncludesContent()
    {
        var task = CreateTask(SwarmRole.Planner);
        const string projectContext = "# AGENTS.md\nUse file-scoped namespaces.\nFollow TDD.";

        var prompt = RolePromptFactory.BuildPrompt(task, strategyAdvice: null, codeContext: null, projectContext: projectContext);

        Assert.Contains("## Project Context", prompt);
        Assert.Contains("# AGENTS.md", prompt);
        Assert.Contains("Follow TDD.", prompt);
    }

    [Fact]
    public void Builder_WithProjectContext_IncludesContent()
    {
        var task = CreateTask(SwarmRole.Builder);
        const string projectContext = "# AGENTS.md\nPrefer sealed records.\nRun tests before committing.";

        var prompt = RolePromptFactory.BuildPrompt(task, strategyAdvice: null, codeContext: null, projectContext: projectContext);

        Assert.Contains("## Project Context", prompt);
        Assert.Contains("Prefer sealed records.", prompt);
        Assert.Contains("Run tests before committing.", prompt);
    }

    [Fact]
    public void Planner_WithoutProjectContext_StillProducesValidPrompt()
    {
        var task = CreateTask(SwarmRole.Planner);

        var prompt = RolePromptFactory.BuildPrompt(task, strategyAdvice: null, codeContext: null, projectContext: null);

        Assert.Contains("planner agent", prompt);
        Assert.Contains("Implement feature X", prompt);
        Assert.DoesNotContain("## Project Context", prompt);
    }
}
