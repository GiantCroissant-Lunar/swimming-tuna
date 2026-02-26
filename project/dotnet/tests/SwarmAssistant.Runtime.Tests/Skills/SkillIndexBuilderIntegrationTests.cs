using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Runtime.Skills;
using Xunit.Abstractions;

namespace SwarmAssistant.Runtime.Tests.Skills;

public sealed class SkillIndexBuilderIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public SkillIndexBuilderIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BuildIndex_WithRealSkillsDirectory_LoadsKnownSkills()
    {
        // Arrange
        var repoRoot = FindRepositoryRoot();
        if (repoRoot is null)
        {
            _output.WriteLine("Repository root not found, skipping integration test");
            return;
        }

        var skillsPath = Path.Combine(repoRoot, ".agent", "skills");
        if (!Directory.Exists(skillsPath))
        {
            _output.WriteLine($"Skills directory not found at {skillsPath}, skipping integration test");
            return;
        }

        var builder = new SkillIndexBuilder(NullLogger<SkillIndexBuilder>.Instance, NullLoggerFactory.Instance);

        // Act
        builder.BuildIndex(skillsPath);
        var allSkills = builder.GetAllSkills();

        // Assert
        Assert.NotEmpty(allSkills);
        _output.WriteLine($"Loaded {allSkills.Count} skills from {skillsPath}");

        foreach (var skill in allSkills.Values)
        {
            _output.WriteLine($"  - {skill.Name}: {skill.Description}");
            _output.WriteLine($"    Tags: [{string.Join(", ", skill.Tags)}]");
            _output.WriteLine($"    Roles: [{string.Join(", ", skill.Roles)}]");
            _output.WriteLine($"    Source: {skill.SourcePath}");
        }

        Assert.True(allSkills.ContainsKey("memory"), "Expected 'memory' skill to be loaded");
    }

    private static string? FindRepositoryRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            var gitMarker = Path.Combine(current, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }
}
