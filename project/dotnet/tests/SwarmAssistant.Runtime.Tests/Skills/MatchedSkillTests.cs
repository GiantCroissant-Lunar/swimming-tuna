using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Skills;

namespace SwarmAssistant.Runtime.Tests.Skills;

public sealed class MatchedSkillTests
{
    private static SkillDefinition CreateTestDefinition() =>
        new SkillDefinition(
            name: "TestSkill",
            description: "A test skill",
            tags: new[] { "test", "example", "demo" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Skill body content",
            sourcePath: "/path/to/skill.md"
        );

    [Fact]
    public void Constructor_WithValidParameters_CreatesInstance()
    {
        var definition = CreateTestDefinition();
        var matchedSkill = new MatchedSkill(
            Definition: definition,
            RelevanceScore: 0.85,
            MatchedTags: new[] { "test", "example" }
        );

        Assert.Equal(definition, matchedSkill.Definition);
        Assert.Equal(0.85, matchedSkill.RelevanceScore);
        Assert.Equal(2, matchedSkill.MatchedTags.Count);
        Assert.Contains("test", matchedSkill.MatchedTags);
        Assert.Contains("example", matchedSkill.MatchedTags);
    }

    [Fact]
    public void Constructor_WithZeroScore_CreatesInstance()
    {
        var definition = CreateTestDefinition();
        var matchedSkill = new MatchedSkill(
            Definition: definition,
            RelevanceScore: 0.0,
            MatchedTags: Array.Empty<string>()
        );

        Assert.Equal(0.0, matchedSkill.RelevanceScore);
        Assert.Empty(matchedSkill.MatchedTags);
    }

    [Fact]
    public void Constructor_WithMaxScore_CreatesInstance()
    {
        var definition = CreateTestDefinition();
        var matchedSkill = new MatchedSkill(
            Definition: definition,
            RelevanceScore: 1.0,
            MatchedTags: new[] { "test", "example", "demo" }
        );

        Assert.Equal(1.0, matchedSkill.RelevanceScore);
        Assert.Equal(3, matchedSkill.MatchedTags.Count);
    }

    [Fact]
    public void SameReference_ReturnsEqual()
    {
        var definition = CreateTestDefinition();
        var matched = new MatchedSkill(
            Definition: definition,
            RelevanceScore: 0.75,
            MatchedTags: new[] { "test" }
        );

        Assert.Equal(matched, matched);
    }

    [Fact]
    public void Equality_WithDifferentScores_ReturnsFalse()
    {
        var definition = CreateTestDefinition();
        var matched1 = new MatchedSkill(
            Definition: definition,
            RelevanceScore: 0.75,
            MatchedTags: new[] { "test" }
        );

        var matched2 = new MatchedSkill(
            Definition: definition,
            RelevanceScore: 0.80,
            MatchedTags: new[] { "test" }
        );

        Assert.NotEqual(matched1, matched2);
    }

    [Fact]
    public void With_ModifiesRelevanceScore()
    {
        var definition = CreateTestDefinition();
        var original = new MatchedSkill(
            Definition: definition,
            RelevanceScore: 0.5,
            MatchedTags: new[] { "test" }
        );

        var modified = original with { RelevanceScore = 0.9 };

        Assert.Equal(0.9, modified.RelevanceScore);
        Assert.Equal(definition, modified.Definition);
        Assert.Single(modified.MatchedTags);
    }

    [Fact]
    public void Constructor_WithNullDefinition_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MatchedSkill(
            Definition: null!,
            RelevanceScore: 0.5,
            MatchedTags: new[] { "test" }
        ));
    }

    [Fact]
    public void Constructor_WithNullMatchedTags_ThrowsArgumentNullException()
    {
        var definition = CreateTestDefinition();
        Assert.Throws<ArgumentNullException>(() => new MatchedSkill(
            Definition: definition,
            RelevanceScore: 0.5,
            MatchedTags: null!
        ));
    }

    [Fact]
    public void Constructor_WithNegativeScore_ThrowsArgumentOutOfRangeException()
    {
        var definition = CreateTestDefinition();
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchedSkill(
            Definition: definition,
            RelevanceScore: -0.1,
            MatchedTags: new[] { "test" }
        ));
    }

    [Fact]
    public void Constructor_WithScoreGreaterThanOne_ThrowsArgumentOutOfRangeException()
    {
        var definition = CreateTestDefinition();
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchedSkill(
            Definition: definition,
            RelevanceScore: 1.1,
            MatchedTags: new[] { "test" }
        ));
    }

    [Fact]
    public void Constructor_WithScoreGreaterThanTwo_ThrowsArgumentOutOfRangeException()
    {
        var definition = CreateTestDefinition();
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchedSkill(
            Definition: definition,
            RelevanceScore: 2.0,
            MatchedTags: new[] { "test" }
        ));
    }

    [Fact]
    public void Constructor_WithEmptyMatchedTags_CreatesInstance()
    {
        var definition = CreateTestDefinition();
        var matchedSkill = new MatchedSkill(
            Definition: definition,
            RelevanceScore: 0.3,
            MatchedTags: Array.Empty<string>()
        );

        Assert.Empty(matchedSkill.MatchedTags);
    }
}
