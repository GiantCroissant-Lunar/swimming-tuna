using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Skills;

namespace SwarmAssistant.Runtime.Tests.Skills;

public sealed class SkillDefinitionTests
{
    [Fact]
    public void Constructor_WithValidParameters_CreatesInstance()
    {
        var definition = new SkillDefinition(
            name: "TestSkill",
            description: "A test skill",
            tags: new[] { "test", "example" },
            roles: new[] { SwarmRole.Builder, SwarmRole.Planner },
            scope: "global",
            body: "# Skill Content\n\nThis is the body.",
            sourcePath: "/path/to/SKILL.md"
        );

        Assert.Equal("TestSkill", definition.Name);
        Assert.Equal("A test skill", definition.Description);
        Assert.Equal(2, definition.Tags.Count);
        Assert.Contains("test", definition.Tags);
        Assert.Contains("example", definition.Tags);
        Assert.Equal(2, definition.Roles.Count);
        Assert.Contains(SwarmRole.Builder, definition.Roles);
        Assert.Contains(SwarmRole.Planner, definition.Roles);
        Assert.Equal("global", definition.Scope);
        Assert.Equal("# Skill Content\n\nThis is the body.", definition.Body);
        Assert.Equal("/path/to/SKILL.md", definition.SourcePath);
    }

    [Fact]
    public void Constructor_WithTaskScope_CreatesInstance()
    {
        var definition = new SkillDefinition(
            name: "TaskSpecificSkill",
            description: "A task-specific skill",
            tags: new[] { "task" },
            roles: new[] { SwarmRole.Reviewer },
            scope: "task:task-123",
            body: "Task-specific content",
            sourcePath: "/path/to/task-skill.md"
        );

        Assert.Equal("task:task-123", definition.Scope);
    }

    [Fact]
    public void SameReference_ReturnsEqual()
    {
        var definition = new SkillDefinition(
            name: "Skill1",
            description: "Description",
            tags: new[] { "tag1" },
            roles: new[] { SwarmRole.Orchestrator },
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        );

        Assert.Equal(definition, definition);
    }

    [Fact]
    public void Equality_WithDifferentValues_ReturnsFalse()
    {
        var definition1 = new SkillDefinition(
            name: "Skill1",
            description: "Description",
            tags: new[] { "tag1" },
            roles: new[] { SwarmRole.Orchestrator },
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        );

        var definition2 = new SkillDefinition(
            name: "Skill2",
            description: "Description",
            tags: new[] { "tag1" },
            roles: new[] { SwarmRole.Orchestrator },
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        );

        Assert.NotEqual(definition1, definition2);
    }

    [Fact]
    public void With_ModifiesProperties()
    {
        var original = new SkillDefinition(
            name: "Original",
            description: "Original description",
            tags: new[] { "tag1" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Original body",
            sourcePath: "/original/path"
        );

        var modified = original with { Name = "Modified" };

        Assert.Equal("Modified", modified.Name);
        Assert.Equal("Original description", modified.Description);
    }

    [Fact]
    public void Constructor_WithNullName_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillDefinition(
            name: null!,
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithEmptyName_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillDefinition(
            name: "",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithWhitespaceName_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillDefinition(
            name: "   ",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithNullDescription_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillDefinition(
            name: "Name",
            description: null!,
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithEmptyDescription_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new SkillDefinition(
            name: "Name",
            description: "",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithNullTags_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: null!,
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithEmptyTags_CreatesInstance()
    {
        var definition = new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: Array.Empty<string>(),
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        );

        Assert.Empty(definition.Tags);
    }

    [Fact]
    public void Constructor_WithNullRoles_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: null!,
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithEmptyRoles_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: Array.Empty<SwarmRole>(),
            scope: "global",
            body: "Body",
            sourcePath: "/path"
        ));

        Assert.Contains("role", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_WithNullScope_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: null!,
            body: "Body",
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithEmptyScope_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "",
            body: "Body",
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithInvalidScope_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "invalid-scope",
            body: "Body",
            sourcePath: "/path"
        ));

        Assert.Contains("scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_WithGlobalScopeCaseInsensitive_CreatesInstance()
    {
        var definition = new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "GLOBAL",
            body: "Body",
            sourcePath: "/path"
        );

        Assert.Equal("GLOBAL", definition.Scope);
    }

    [Fact]
    public void Constructor_WithTaskScopeCaseInsensitive_CreatesInstance()
    {
        var definition = new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "TASK:123",
            body: "Body",
            sourcePath: "/path"
        );

        Assert.Equal("TASK:123", definition.Scope);
    }

    [Fact]
    public void Constructor_WithNullBody_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: null!,
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithEmptyBody_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "",
            sourcePath: "/path"
        ));
    }

    [Fact]
    public void Constructor_WithNullSourcePath_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Body",
            sourcePath: null!
        ));
    }

    [Fact]
    public void Constructor_WithEmptySourcePath_ThrowsArgumentException()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SkillDefinition(
            name: "Name",
            description: "Description",
            tags: new[] { "tag" },
            roles: new[] { SwarmRole.Builder },
            scope: "global",
            body: "Body",
            sourcePath: ""
        ));
    }
}
