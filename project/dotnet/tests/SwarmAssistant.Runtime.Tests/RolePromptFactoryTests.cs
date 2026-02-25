using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Execution;
using SwarmAssistant.Runtime.Skills;

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

    [Fact]
    public void BuildPrompt_WithMatchedSkills_IncludesSkillContext()
    {
        var task = CreateTask(SwarmRole.Builder);
        var skills = new List<MatchedSkill>
        {
            new MatchedSkill(
                Definition: new SkillDefinition(
                    name: "TestSkill1",
                    description: "A test skill",
                    tags: new[] { "testing" },
                    roles: new[] { SwarmRole.Builder },
                    scope: "global",
                    body: "Use TDD approach for all changes.",
                    sourcePath: "/skills/test1.md"),
                RelevanceScore: 0.95,
                MatchedTags: new[] { "testing" }),
            new MatchedSkill(
                Definition: new SkillDefinition(
                    name: "TestSkill2",
                    description: "Another test skill",
                    tags: new[] { "quality" },
                    roles: new[] { SwarmRole.Builder },
                    scope: "global",
                    body: "Always add logging for debugging.",
                    sourcePath: "/skills/test2.md"),
                RelevanceScore: 0.85,
                MatchedTags: new[] { "quality" })
        };

        var prompt = RolePromptFactory.BuildPrompt(task, strategyAdvice: null, codeContext: null, projectContext: null, matchedSkills: skills);

        Assert.Contains("--- Agent Skills ---", prompt);
        Assert.Contains("### TestSkill1", prompt);
        Assert.Contains("Use TDD approach for all changes.", prompt);
        Assert.Contains("### TestSkill2", prompt);
        Assert.Contains("Always add logging for debugging.", prompt);
        Assert.Contains("--- End Agent Skills ---", prompt);
        Assert.Contains("Apply these skills to your review/implementation.", prompt);
    }

    [Fact]
    public void BuildPrompt_WithNullSkills_DoesNotIncludeSkillContext()
    {
        var task = CreateTask(SwarmRole.Builder);

        var prompt = RolePromptFactory.BuildPrompt(task, strategyAdvice: null, codeContext: null, projectContext: null, matchedSkills: null);

        Assert.DoesNotContain("--- Agent Skills ---", prompt);
        Assert.DoesNotContain("--- End Agent Skills ---", prompt);
    }

    [Fact]
    public void BuildPrompt_WithEmptySkills_DoesNotIncludeSkillContext()
    {
        var task = CreateTask(SwarmRole.Builder);
        var skills = new List<MatchedSkill>();

        var prompt = RolePromptFactory.BuildPrompt(task, strategyAdvice: null, codeContext: null, projectContext: null, matchedSkills: skills);

        Assert.DoesNotContain("--- Agent Skills ---", prompt);
        Assert.DoesNotContain("--- End Agent Skills ---", prompt);
    }

    [Fact]
    public void BuildPrompt_SkillContextEnforcesBudget()
    {
        var task = CreateTask(SwarmRole.Builder);
        var largeBody = new string('X', 5000); // 5000 chars, exceeds 4000 budget
        var skills = new List<MatchedSkill>
        {
            new MatchedSkill(
                Definition: new SkillDefinition(
                    name: "LargeSkill",
                    description: "A large skill",
                    tags: new[] { "testing" },
                    roles: new[] { SwarmRole.Builder },
                    scope: "global",
                    body: largeBody,
                    sourcePath: "/skills/large.md"),
                RelevanceScore: 0.95,
                MatchedTags: new[] { "testing" })
        };

        var prompt = RolePromptFactory.BuildPrompt(task, strategyAdvice: null, codeContext: null, projectContext: null, matchedSkills: skills);

        Assert.Contains("--- Agent Skills ---", prompt);
        Assert.Contains("### LargeSkill", prompt);

        // Extract skill context section
        var startIdx = prompt.IndexOf("--- Agent Skills ---");
        var endIdx = prompt.IndexOf("--- End Agent Skills ---");
        var skillSection = prompt.Substring(startIdx, endIdx - startIdx + "--- End Agent Skills ---".Length);

        // Verify total is under budget (4000 + some overhead for headers)
        Assert.True(skillSection.Length < 4500, $"Skill section should be under budget, but was {skillSection.Length} chars");
    }

    [Fact]
    public void BuildPrompt_ExistingOverloadsStillWork()
    {
        var task = CreateTask(SwarmRole.Planner);

        // Test all existing overloads
        var prompt1 = RolePromptFactory.BuildPrompt(task);
        var prompt2 = RolePromptFactory.BuildPrompt(task, strategyAdvice: null);
        var prompt3 = RolePromptFactory.BuildPrompt(task, strategyAdvice: null, codeContext: null);
        var prompt4 = RolePromptFactory.BuildPrompt(task, strategyAdvice: null, codeContext: null, projectContext: null);

        Assert.Contains("planner agent", prompt1);
        Assert.Contains("planner agent", prompt2);
        Assert.Contains("planner agent", prompt3);
        Assert.Contains("planner agent", prompt4);
    }

    [Fact]
    public void BuildPrompt_SkillsOnlyAppliedToRelevantRoles()
    {
        var skills = new List<MatchedSkill>
        {
            new MatchedSkill(
                Definition: new SkillDefinition(
                    name: "TestSkill",
                    description: "A test skill",
                    tags: new[] { "testing" },
                    roles: new[] { SwarmRole.Builder },
                    scope: "global",
                    body: "Test skill body",
                    sourcePath: "/skills/test.md"),
                RelevanceScore: 0.95,
                MatchedTags: new[] { "testing" })
        };

        // Should include skills for Builder
        var builderTask = CreateTask(SwarmRole.Builder);
        var builderPrompt = RolePromptFactory.BuildPrompt(builderTask, strategyAdvice: null, codeContext: null, projectContext: null, matchedSkills: skills);
        Assert.Contains("--- Agent Skills ---", builderPrompt);

        // Should include skills for Reviewer
        var reviewerTask = CreateTask(SwarmRole.Reviewer);
        var reviewerPrompt = RolePromptFactory.BuildPrompt(reviewerTask, strategyAdvice: null, codeContext: null, projectContext: null, matchedSkills: skills);
        Assert.Contains("--- Agent Skills ---", reviewerPrompt);

        // Should NOT include skills for Orchestrator
        var orchestratorTask = CreateTask(SwarmRole.Orchestrator);
        var orchestratorPrompt = RolePromptFactory.BuildPrompt(orchestratorTask, strategyAdvice: null, codeContext: null, projectContext: null, matchedSkills: skills);
        Assert.DoesNotContain("--- Agent Skills ---", orchestratorPrompt);
    }
}
