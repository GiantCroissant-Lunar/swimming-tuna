using System.Text.RegularExpressions;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Planning;

namespace SwarmAssistant.Runtime.Tests;

public sealed class OrchestratorFlowTests
{
    private static readonly Regex ActionRegex = new(
        @"ACTION:\s*(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // --- Orchestrator Prompt Tests ---

    [Fact]
    public void BuildOrchestratorPrompt_IncludesGoapContext()
    {
        var prompt = RolePromptFactory.BuildOrchestratorPrompt(
            "task-1", "Test Task", "Do testing",
            "GOAP Analysis:\n  Recommended plan: Plan → Build (total cost: 4)",
            null);

        Assert.Contains("orchestrator agent for task 'Test Task'", prompt);
        Assert.Contains("GOAP Analysis:", prompt);
        Assert.Contains("Recommended plan: Plan → Build", prompt);
        Assert.Contains("ACTION:", prompt);
        Assert.Contains("REASON:", prompt);
    }

    [Fact]
    public void BuildOrchestratorPrompt_IncludesBlackboardEntries()
    {
        var blackboard = new Dictionary<string, string>
        {
            ["planner_output"] = "Step 1: analyze. Step 2: build.",
            ["builder_output"] = "Implemented core logic."
        };

        var prompt = RolePromptFactory.BuildOrchestratorPrompt(
            "task-1", "Test Task", "Do testing",
            "GOAP Analysis:\n  Dead end: false",
            blackboard);

        Assert.Contains("Task history:", prompt);
        Assert.Contains("planner_output:", prompt);
        Assert.Contains("builder_output:", prompt);
    }

    [Fact]
    public void BuildOrchestratorPrompt_EmptyBlackboard_SkipsHistory()
    {
        var prompt = RolePromptFactory.BuildOrchestratorPrompt(
            "task-1", "Test Task", "Do testing",
            "GOAP Analysis:\n  Dead end: false",
            new Dictionary<string, string>());

        Assert.DoesNotContain("Task history:", prompt);
    }

    // --- RolePromptFactory Orchestrator Support ---

    [Fact]
    public void BuildPrompt_Orchestrator_UsesOrchestratorPrompt()
    {
        var command = new ExecuteRoleTask(
            "task-1", SwarmRole.Orchestrator, "Title", "Desc", null, null,
            "Custom orchestrator prompt for testing.");

        var prompt = RolePromptFactory.BuildPrompt(command);

        Assert.Equal("Custom orchestrator prompt for testing.", prompt);
    }

    [Fact]
    public void BuildPrompt_Orchestrator_NullPrompt_ReturnsDefault()
    {
        var command = new ExecuteRoleTask(
            "task-1", SwarmRole.Orchestrator, "Title", "Desc", null, null, null);

        var prompt = RolePromptFactory.BuildPrompt(command);

        Assert.Contains("orchestrator agent", prompt);
        Assert.Contains("ACTION:", prompt);
    }

    // --- ACTION Parsing Tests ---

    [Theory]
    [InlineData("ACTION: Plan\nREASON: Starting fresh.", "Plan")]
    [InlineData("ACTION: Build\nREASON: Plan is ready.", "Build")]
    [InlineData("ACTION: Review\nREASON: Build complete.", "Review")]
    [InlineData("ACTION: Rework\nREASON: Reviewer found issues.", "Rework")]
    [InlineData("ACTION: Finalize\nREASON: All good.", "Finalize")]
    [InlineData("ACTION: Escalate\nREASON: Too many retries.", "Escalate")]
    public void ActionRegex_ParsesValidActions(string input, string expectedAction)
    {
        var match = ActionRegex.Match(input);

        Assert.True(match.Success);
        Assert.Equal(expectedAction, match.Groups[1].Value);
    }

    [Theory]
    [InlineData("I think we should plan next.")]
    [InlineData("No action specified")]
    [InlineData("")]
    public void ActionRegex_RejectsInvalidOutput(string input)
    {
        var match = ActionRegex.Match(input);

        Assert.False(match.Success);
    }

    [Fact]
    public void ActionRegex_CaseInsensitive()
    {
        var match = ActionRegex.Match("action: build\nreason: ready to go");

        Assert.True(match.Success);
        Assert.Equal("build", match.Groups[1].Value);
    }

    // --- GOAP Context Serializer Integration ---

    [Fact]
    public void GoapContext_SerializedIntoOrchestratorPrompt()
    {
        var worldState = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.AdapterAvailable, true);

        var planner = new GoapPlanner(SwarmActions.All);
        var result = planner.Plan(worldState, SwarmActions.CompleteTask);
        var context = GoapContextSerializer.Serialize(worldState, result);

        var prompt = RolePromptFactory.BuildOrchestratorPrompt(
            "task-1", "Test", "Test desc", context, null);

        Assert.Contains("GOAP Analysis:", prompt);
        Assert.Contains("Build", prompt);
        Assert.Contains("TaskExists=true", prompt);
        Assert.Contains("PlanExists=true", prompt);
    }

    // --- ExecuteRoleTask Record Tests ---

    [Fact]
    public void ExecuteRoleTask_OrchestratorPrompt_DefaultsToNull()
    {
        var command = new ExecuteRoleTask("t-1", SwarmRole.Planner, "T", "D", null, null);

        Assert.Null(command.OrchestratorPrompt);
    }

    [Fact]
    public void ExecuteRoleTask_OrchestratorPrompt_CanBeSet()
    {
        var command = new ExecuteRoleTask(
            "t-1", SwarmRole.Orchestrator, "T", "D", null, null, "custom prompt");

        Assert.Equal("custom prompt", command.OrchestratorPrompt);
    }

    // --- GOAP Fallback Logic Tests ---

    [Fact]
    public void GoapPlanner_ProducesValidPlan_ForOrchestratorFallback()
    {
        var worldState = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.AdapterAvailable, true);

        var planner = new GoapPlanner(SwarmActions.All);
        var result = planner.Plan(worldState, SwarmActions.CompleteTask);

        // Should have a recommended plan starting with Plan
        Assert.NotNull(result.RecommendedPlan);
        Assert.True(result.RecommendedPlan.Count > 0);
        Assert.Equal("Plan", result.RecommendedPlan[0].Name);
        Assert.False(result.DeadEnd);
    }

    [Fact]
    public void GoapPlanner_AfterBuildExists_RecommendsBuild()
    {
        var worldState = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.AdapterAvailable, true);

        var planner = new GoapPlanner(SwarmActions.All);
        var result = planner.Plan(worldState, SwarmActions.CompleteTask);

        Assert.NotNull(result.RecommendedPlan);
        Assert.True(result.RecommendedPlan.Count > 0);
        Assert.Equal("Build", result.RecommendedPlan[0].Name);
    }

    [Fact]
    public void GoapPlanner_AfterReviewRejection_RecommendsRework()
    {
        var worldState = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.BuildExists, true)
            .With(WorldKey.ReviewRejected, true)
            .With(WorldKey.AdapterAvailable, true);

        var planner = new GoapPlanner(SwarmActions.All);
        var result = planner.Plan(worldState, SwarmActions.CompleteTask);

        Assert.NotNull(result.RecommendedPlan);
        Assert.True(result.RecommendedPlan.Count > 0);
        Assert.Equal("Rework", result.RecommendedPlan[0].Name);
    }

    // --- Blackboard Entry Tracking ---

    [Fact]
    public void BlackboardEntries_IncludedInOrchestratorPrompt()
    {
        var entries = new Dictionary<string, string>
        {
            ["planner_output"] = "Plan: do stuff",
            ["builder_output"] = "Built stuff",
            ["reviewer_output"] = "Looks good, approved",
            ["review_passed"] = "True"
        };

        var prompt = RolePromptFactory.BuildOrchestratorPrompt(
            "task-1", "Test", "Test desc",
            "GOAP Analysis:\n  Goal already satisfied",
            entries);

        Assert.Contains("planner_output: Plan: do stuff", prompt);
        Assert.Contains("builder_output: Built stuff", prompt);
        Assert.Contains("reviewer_output: Looks good, approved", prompt);
    }

    // --- GOAP-Aware Local Echo Orchestrator Tests ---

    [Theory]
    [InlineData("Recommended plan: Build → Review → Finalize (total cost: 6)", "Build")]
    [InlineData("Recommended plan: Plan (total cost: 1)", "Plan")]
    [InlineData("Recommended plan: Review → Finalize (total cost: 3)", "Review")]
    [InlineData("Recommended plan: Rework → Review → Finalize (total cost: 7)", "Rework")]
    [InlineData("Recommended plan: Finalize (total cost: 1)", "Finalize")]
    [InlineData("Recommended plan: Escalate (total cost: 10)", "Escalate")]
    public void GoapAwareEcho_ExtractsRecommendedAction(string goapLine, string expectedAction)
    {
        var prompt = $"You are the orchestrator.\n{goapLine}\nWhat next?";
        var command = new ExecuteRoleTask(
            "t-1", SwarmRole.Orchestrator, "Test", "Desc", null, null, prompt);

        var output = InvokeLocalEcho(command);

        Assert.Contains($"ACTION: {expectedAction}", output);
        Assert.Contains("REASON:", output);
    }

    [Fact]
    public void GoapAwareEcho_NoPrompt_DefaultsToPlan()
    {
        var command = new ExecuteRoleTask(
            "t-1", SwarmRole.Orchestrator, "Test", "Desc", null, null, null);

        var output = InvokeLocalEcho(command);

        Assert.Contains("ACTION: Plan", output);
    }

    [Fact]
    public void GoapAwareEcho_NoRecommendedPlan_DefaultsToPlan()
    {
        var prompt = "You are the orchestrator.\nGOAP Analysis:\n  Dead end: false\nWhat next?";
        var command = new ExecuteRoleTask(
            "t-1", SwarmRole.Orchestrator, "Test", "Desc", null, null, prompt);

        var output = InvokeLocalEcho(command);

        Assert.Contains("ACTION: Plan", output);
    }

    [Fact]
    public void GoapAwareEcho_FullGoapContext_ExtractsFirstAction()
    {
        // Simulate a real GOAP serialized context with world state
        var worldState = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.AdapterAvailable, true);

        var planner = new GoapPlanner(SwarmActions.All);
        var result = planner.Plan(worldState, SwarmActions.CompleteTask);
        var context = GoapContextSerializer.Serialize(worldState, result);

        var prompt = RolePromptFactory.BuildOrchestratorPrompt(
            "task-1", "Test", "Test desc", context, null);

        var command = new ExecuteRoleTask(
            "task-1", SwarmRole.Orchestrator, "Test", "Test desc", null, null, prompt);

        var output = InvokeLocalEcho(command);

        // After Plan exists, GOAP recommends Build
        Assert.Contains("ACTION: Build", output);
    }

    /// <summary>
    /// Uses reflection to call the private <c>BuildInternalEcho</c> method for testing.
    /// </summary>
    private static string InvokeLocalEcho(ExecuteRoleTask command)
    {
        var method = typeof(SubscriptionCliRoleExecutor)
            .GetMethod("BuildInternalEcho",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (string)method.Invoke(null, [command])!;
    }
}
