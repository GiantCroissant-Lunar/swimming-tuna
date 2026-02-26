using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Skills;

namespace SwarmAssistant.Runtime.Tests.Skills;

public sealed class SkillMatcherTests
{
    [Fact]
    public void ExactTagMatch_ReturnsFullScore()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "database-query",
                description: "Query database",
                tags: new[] { "database", "query", "sql" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: "SELECT * FROM users",
                sourcePath: "/skills/db.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "Database query",
            "Run SQL query on database",
            SwarmRole.Builder
        );

        Assert.Single(results);
        Assert.Equal(1.0, results[0].RelevanceScore);
        Assert.Equal(3, results[0].MatchedTags.Count);
    }

    [Fact]
    public void PartialMatch_ReturnsProportionalScore()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "web-api",
                description: "Web API skill",
                tags: new[] { "api", "rest", "http", "json" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: "REST API implementation",
                sourcePath: "/skills/api.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "API testing",
            "Test REST endpoints",
            SwarmRole.Builder
        );

        Assert.Single(results);
        Assert.Equal(0.5, results[0].RelevanceScore);
        Assert.Equal(2, results[0].MatchedTags.Count);
        Assert.Contains("api", results[0].MatchedTags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("rest", results[0].MatchedTags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void RoleFiltering_ExcludesIrrelevantSkills()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "build-skill",
                description: "Build skill",
                tags: new[] { "build", "compile" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: "Build code",
                sourcePath: "/skills/build.md"
            ),
            new(
                name: "plan-skill",
                description: "Plan skill",
                tags: new[] { "plan", "strategy" },
                roles: new[] { SwarmRole.Planner },
                scope: "global",
                body: "Create plan",
                sourcePath: "/skills/plan.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "Build and plan",
            "Build code and create plan",
            SwarmRole.Builder
        );

        Assert.Single(results);
        Assert.Equal("build-skill", results[0].Definition.Name);
    }

    [Fact]
    public void BudgetConstraint_TruncatesResults()
    {
        var largeBody = new string('x', 2500);
        var skills = new List<SkillDefinition>
        {
            new(
                name: "skill1",
                description: "Skill 1",
                tags: new[] { "test" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: largeBody,
                sourcePath: "/skills/s1.md"
            ),
            new(
                name: "skill2",
                description: "Skill 2",
                tags: new[] { "test" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: largeBody,
                sourcePath: "/skills/s2.md"
            ),
            new(
                name: "skill3",
                description: "Skill 3",
                tags: new[] { "test" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: largeBody,
                sourcePath: "/skills/s3.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "Test",
            "",
            SwarmRole.Builder,
            maxResults: 5,
            budgetChars: 4000
        );

        Assert.Single(results);
        var totalLength = results.Sum(r => r.Definition.Body?.Length ?? 0);
        Assert.True(totalLength <= 4000);
    }

    [Fact]
    public void BudgetConstraint_SkipsOversizedAndPacksSmallerSkills()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "large-skill",
                description: "Large skill",
                tags: new[] { "test" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: new string('x', 3000),
                sourcePath: "/skills/large.md"
            ),
            new(
                name: "medium-skill",
                description: "Medium skill",
                tags: new[] { "test" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: new string('y', 2000),
                sourcePath: "/skills/medium.md"
            ),
            new(
                name: "small-skill",
                description: "Small skill",
                tags: new[] { "test" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: new string('z', 500),
                sourcePath: "/skills/small.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "Test",
            "",
            SwarmRole.Builder,
            maxResults: 5,
            budgetChars: 4000
        );

        // large-skill (3000) fits, medium-skill (2000) doesn't, small-skill (500) does
        Assert.Equal(2, results.Count);
        Assert.Equal("large-skill", results[0].Definition.Name);
        Assert.Equal("small-skill", results[1].Definition.Name);
    }

    [Fact]
    public void EmptyDescription_UsesOnlyTitle()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "test-skill",
                description: "Test skill",
                tags: new[] { "testing", "unit" },
                roles: new[] { SwarmRole.Tester },
                scope: "global",
                body: "Test code",
                sourcePath: "/skills/test.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "Unit testing",
            "",
            SwarmRole.Tester
        );

        Assert.Single(results);
        Assert.Equal("test-skill", results[0].Definition.Name);
    }

    [Fact]
    public void NoMatchingSkills_ReturnsEmpty()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "database-skill",
                description: "Database skill",
                tags: new[] { "database", "sql" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: "Database operations",
                sourcePath: "/skills/db.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "Frontend styling",
            "CSS and HTML work",
            SwarmRole.Builder
        );

        Assert.Empty(results);
    }

    [Fact]
    public void StopWordsFiltered()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "api-skill",
                description: "API skill",
                tags: new[] { "api" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: "API implementation",
                sourcePath: "/skills/api.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "Work with the API",
            "This should work with the API system",
            SwarmRole.Builder
        );

        Assert.Single(results);
        Assert.Single(results[0].MatchedTags);
        Assert.Contains("api", results[0].MatchedTags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaxResultsRespected()
    {
        var skills = Enumerable.Range(1, 10)
            .Select(i => new SkillDefinition(
                name: $"skill{i}",
                description: $"Skill {i}",
                tags: new[] { "common", $"tag{i}" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: "Short body",
                sourcePath: $"/skills/s{i}.md"
            ))
            .ToList();

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "Common task",
            "",
            SwarmRole.Builder,
            maxResults: 3
        );

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void EmptyTitle_ReturnsEmpty()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "test-skill",
                description: "Test skill",
                tags: new[] { "test" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: "Test",
                sourcePath: "/skills/test.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match("", "test related description", SwarmRole.Builder);

        Assert.Empty(results);
    }

    [Fact]
    public void SkillWithNoTags_IsSkipped()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "no-tags-skill",
                description: "Skill without tags",
                tags: Array.Empty<string>(),
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: "No tags",
                sourcePath: "/skills/notags.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match("Any task", "", SwarmRole.Builder);

        Assert.Empty(results);
    }

    [Fact]
    public void SkillWithNonMatchingRole_IsSkipped()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "planner-only-skill",
                description: "Skill for planner only",
                tags: new[] { "test" },
                roles: new[] { SwarmRole.Planner },
                scope: "global",
                body: "Planner only",
                sourcePath: "/skills/planner.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match("Test task", "", SwarmRole.Builder);

        Assert.Empty(results);
    }

    [Fact]
    public void CaseInsensitiveMatching()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "case-skill",
                description: "Case test",
                tags: new[] { "DATABASE", "Query" },
                roles: new[] { SwarmRole.Builder },
                scope: "global",
                body: "Case test",
                sourcePath: "/skills/case.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "database QUERY",
            "",
            SwarmRole.Builder
        );

        Assert.Single(results);
        Assert.Equal(1.0, results[0].RelevanceScore);
    }

    [Fact]
    public void SortingByScoreDescending()
    {
        var skills = new List<SkillDefinition>
        {
            new(
                name: "partial-match",
                description: "Partial",
                tags: new[] { "test", "unit", "integration", "e2e" },
                roles: new[] { SwarmRole.Tester },
                scope: "global",
                body: "Partial match",
                sourcePath: "/skills/partial.md"
            ),
            new(
                name: "full-match",
                description: "Full",
                tags: new[] { "test", "unit" },
                roles: new[] { SwarmRole.Tester },
                scope: "global",
                body: "Full match",
                sourcePath: "/skills/full.md"
            )
        };

        var matcher = new SkillMatcher(skills);
        var results = matcher.Match(
            "Test unit",
            "",
            SwarmRole.Tester
        );

        Assert.Equal(2, results.Count);
        Assert.Equal("full-match", results[0].Definition.Name);
        Assert.Equal(1.0, results[0].RelevanceScore);
        Assert.Equal("partial-match", results[1].Definition.Name);
        Assert.Equal(0.5, results[1].RelevanceScore);
    }
}
