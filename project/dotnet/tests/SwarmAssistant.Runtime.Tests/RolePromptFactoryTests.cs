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

    [Fact]
    public void Builder_WithPlanningOutput_IncludesImplementationPlanSection()
    {
        var task = new ExecuteRoleTask(
            TaskId: "test-2",
            Role: SwarmRole.Builder,
            Title: "Implement feature X",
            Description: "Build a new feature for the system",
            PlanningOutput: "1. Create Foo.cs\n2. Update Bar.cs",
            BuildOutput: null);

        var prompt = RolePromptFactory.BuildPrompt(task);

        Assert.Contains("## Implementation Plan", prompt);
        Assert.Contains("1. Create Foo.cs", prompt);
        Assert.Contains("2. Update Bar.cs", prompt);
        Assert.Contains("## Task", prompt);
        Assert.Contains("Implement feature X", prompt);
    }

    [Fact]
    public void Builder_WithoutPlanningOutput_HasFallbackInstruction()
    {
        var task = new ExecuteRoleTask(
            TaskId: "test-3",
            Role: SwarmRole.Builder,
            Title: "Implement feature X",
            Description: "Build a new feature for the system",
            PlanningOutput: null,
            BuildOutput: null);

        var prompt = RolePromptFactory.BuildPrompt(task);

        Assert.Contains("## Implementation Plan", prompt);
        Assert.Contains("No plan provided", prompt);
    }
}
