using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Skills;
using Xunit;

namespace SwarmAssistant.Runtime.Tests.Skills;

public sealed class SkillFileParserTests
{
    private readonly SkillFileParser _parser = new(NullLogger<SkillFileParser>.Instance);

    [Fact]
    public void Parse_ValidSkillWithAllFields_ReturnsDefinition()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags:
              - tag1
              - tag2
            roles:
              - Planner
              - Builder
            scope: global
            ---
            # Test Body
            This is the body content.
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.NotNull(result);
        Assert.Equal("test-skill", result.Name);
        Assert.Equal("A test skill", result.Description);
        Assert.Equal(2, result.Tags.Count);
        Assert.Contains("tag1", result.Tags);
        Assert.Contains("tag2", result.Tags);
        Assert.Equal(2, result.Roles.Count);
        Assert.Contains(SwarmRole.Planner, result.Roles);
        Assert.Contains(SwarmRole.Builder, result.Roles);
        Assert.Equal("global", result.Scope);
        Assert.Contains("# Test Body", result.Body);
        Assert.Equal("test.md", result.SourcePath);
    }

    [Fact]
    public void Parse_MissingName_ReturnsNull()
    {
        var content = """
            ---
            description: A test skill
            tags:
              - tag1
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_EmptyName_ReturnsNull()
    {
        var content = """
            ---
            name: ""
            description: A test skill
            tags:
              - tag1
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_MissingDescription_ReturnsNull()
    {
        var content = """
            ---
            name: test-skill
            tags:
              - tag1
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_EmptyDescription_ReturnsNull()
    {
        var content = """
            ---
            name: test-skill
            description: ""
            tags:
              - tag1
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_EmptyTags_ReturnsNull()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags: []
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_MissingTags_ReturnsNull()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_NoFrontmatter_ReturnsNull()
    {
        var content = """
            # Just a regular markdown file
            No frontmatter here.
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_BodyOnly_ReturnsNull()
    {
        var content = """
            This is just body content without frontmatter.
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_UnknownRole_SkipsRole()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags:
              - tag1
            roles:
              - Planner
              - UnknownRole
              - Builder
            scope: global
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.NotNull(result);
        Assert.Equal(2, result.Roles.Count);
        Assert.Contains(SwarmRole.Planner, result.Roles);
        Assert.Contains(SwarmRole.Builder, result.Roles);
        Assert.DoesNotContain(result.Roles, r => r.ToString() == "UnknownRole");
    }

    [Fact]
    public void Parse_CommaSeparatedTags_ParsesCorrectly()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags: tag1, tag2, tag3
            roles:
              - Builder
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.NotNull(result);
        Assert.Equal(3, result.Tags.Count);
        Assert.Contains("tag1", result.Tags);
        Assert.Contains("tag2", result.Tags);
        Assert.Contains("tag3", result.Tags);
    }

    [Fact]
    public void Parse_YamlListTags_ParsesCorrectly()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags:
              - yaml-tag1
              - yaml-tag2
            roles:
              - Builder
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.NotNull(result);
        Assert.Equal(2, result.Tags.Count);
        Assert.Contains("yaml-tag1", result.Tags);
        Assert.Contains("yaml-tag2", result.Tags);
    }

    [Fact]
    public void Parse_DefaultScope_ReturnsGlobal()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags:
              - tag1
            roles:
              - Builder
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.NotNull(result);
        Assert.Equal("global", result.Scope);
    }

    [Fact]
    public void Parse_MultipleRoles_ParsesAll()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags:
              - tag1
            roles:
              - Planner
              - Builder
              - Reviewer
              - Orchestrator
            scope: global
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.NotNull(result);
        Assert.Equal(4, result.Roles.Count);
        Assert.Contains(SwarmRole.Planner, result.Roles);
        Assert.Contains(SwarmRole.Builder, result.Roles);
        Assert.Contains(SwarmRole.Reviewer, result.Roles);
        Assert.Contains(SwarmRole.Orchestrator, result.Roles);
    }

    [Fact]
    public void Parse_CaseInsensitiveRoles_ParsesCorrectly()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags:
              - tag1
            roles:
              - planner
              - BUILDER
              - ReViEwEr
            scope: global
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.NotNull(result);
        Assert.Equal(3, result.Roles.Count);
        Assert.Contains(SwarmRole.Planner, result.Roles);
        Assert.Contains(SwarmRole.Builder, result.Roles);
        Assert.Contains(SwarmRole.Reviewer, result.Roles);
    }

    [Fact]
    public void Parse_CommaSeparatedRoles_ParsesCorrectly()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags:
              - tag1
            roles: Planner, Builder
            scope: global
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.NotNull(result);
        Assert.Equal(2, result.Roles.Count);
        Assert.Contains(SwarmRole.Planner, result.Roles);
        Assert.Contains(SwarmRole.Builder, result.Roles);
    }

    [Fact]
    public void Parse_EmptyContent_ReturnsNull()
    {
        var result = _parser.Parse("", "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_WhitespaceContent_ReturnsNull()
    {
        var result = _parser.Parse("   \n\n   ", "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_BodyWithMultipleDashes_ParsesCorrectly()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags:
              - tag1
            roles:
              - Builder
            ---
            # Body with --- in content
            This is valid content with --- dashes.
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.NotNull(result);
        Assert.Contains("---", result.Body);
    }

    [Fact]
    public void Parse_WithBOM_ReturnsNull()
    {
        // BOM immediately before '---' prevents the frontmatter regex from matching
        // because the '^' assertion fails after consuming the BOM character.
        var content = "\uFEFF---\nname: test-skill\ndescription: A test skill\ntags:\n  - tag1\nroles:\n  - Builder\n---\nBody content";

        var result = _parser.Parse(content, "test.md");

        Assert.Null(result);
    }

    [Fact]
    public void Parse_AllSwarmRoles_ParsesCorrectly()
    {
        var content = """
            ---
            name: test-skill
            description: A test skill
            tags:
              - tag1
            roles:
              - Planner
              - Builder
              - Reviewer
              - Orchestrator
              - Researcher
              - Debugger
              - Tester
            scope: global
            ---
            Body content
            """;

        var result = _parser.Parse(content, "test.md");

        Assert.NotNull(result);
        Assert.Equal(7, result.Roles.Count);
        Assert.Contains(SwarmRole.Planner, result.Roles);
        Assert.Contains(SwarmRole.Builder, result.Roles);
        Assert.Contains(SwarmRole.Reviewer, result.Roles);
        Assert.Contains(SwarmRole.Orchestrator, result.Roles);
        Assert.Contains(SwarmRole.Researcher, result.Roles);
        Assert.Contains(SwarmRole.Debugger, result.Roles);
        Assert.Contains(SwarmRole.Tester, result.Roles);
    }
}
