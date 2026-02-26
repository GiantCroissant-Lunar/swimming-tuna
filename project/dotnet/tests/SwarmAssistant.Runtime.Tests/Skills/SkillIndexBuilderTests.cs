using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Skills;

namespace SwarmAssistant.Runtime.Tests.Skills;

public sealed class SkillIndexBuilderTests : IDisposable
{
    private readonly string _tempDirectory;

    public SkillIndexBuilderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"skill-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void BuildIndex_DiscoverNestedSkills_SuccessfullyIndexesAll()
    {
        // Arrange
        var skill1Path = Path.Combine(_tempDirectory, "category-a", "SKILL.md");
        var skill2Path = Path.Combine(_tempDirectory, "category-b", "sub", "SKILL.md");

        Directory.CreateDirectory(Path.GetDirectoryName(skill1Path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(skill2Path)!);

        File.WriteAllText(skill1Path, @"---
name: skill-one
description: First skill
tags: [test, api]
roles: [Planner]
scope: global
---

# Skill One
Body content here.");

        File.WriteAllText(skill2Path, @"---
name: skill-two
description: Second skill
tags: [test]
roles: [Builder]
scope: global
---

# Skill Two
Different body.");

        var builder = new SkillIndexBuilder(NullLogger<SkillIndexBuilder>.Instance, NullLoggerFactory.Instance);

        // Act
        builder.BuildIndex(_tempDirectory);
        var allSkills = builder.GetAllSkills();

        // Assert
        Assert.Equal(2, allSkills.Count);
        Assert.True(allSkills.ContainsKey("skill-one"));
        Assert.True(allSkills.ContainsKey("skill-two"));
        Assert.Equal("First skill", allSkills["skill-one"].Description);
        Assert.Equal("Second skill", allSkills["skill-two"].Description);
    }

    [Fact]
    public void BuildIndex_DuplicateSkillNames_KeepsFirstAndLogsWarning()
    {
        // Arrange
        var skill1Path = Path.Combine(_tempDirectory, "first", "SKILL.md");
        var skill2Path = Path.Combine(_tempDirectory, "second", "SKILL.md");

        Directory.CreateDirectory(Path.GetDirectoryName(skill1Path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(skill2Path)!);

        File.WriteAllText(skill1Path, @"---
name: duplicate
description: First duplicate
tags: [test]
roles: [Builder]
scope: global
---

First content.");

        File.WriteAllText(skill2Path, @"---
name: duplicate
description: Second duplicate
tags: [test]
roles: [Builder]
scope: global
---

Second content.");

        var builder = new SkillIndexBuilder(NullLogger<SkillIndexBuilder>.Instance, NullLoggerFactory.Instance);

        // Act
        builder.BuildIndex(_tempDirectory);
        var allSkills = builder.GetAllSkills();

        // Assert
        Assert.Single(allSkills);
        Assert.Equal("First duplicate", allSkills["duplicate"].Description);
        Assert.Contains("first", allSkills["duplicate"].SourcePath);
    }

    [Fact]
    public void BuildIndex_InvalidSkillFile_SkipsAndContinues()
    {
        // Arrange
        var validPath = Path.Combine(_tempDirectory, "valid", "SKILL.md");
        var invalidPath = Path.Combine(_tempDirectory, "invalid", "SKILL.md");

        Directory.CreateDirectory(Path.GetDirectoryName(validPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(invalidPath)!);

        File.WriteAllText(validPath, @"---
name: valid-skill
description: Valid skill
tags: [test]
roles: [Builder]
scope: global
---

Valid content.");

        File.WriteAllText(invalidPath, @"This is not valid YAML frontmatter
No frontmatter here at all.");

        var builder = new SkillIndexBuilder(NullLogger<SkillIndexBuilder>.Instance, NullLoggerFactory.Instance);

        // Act
        builder.BuildIndex(_tempDirectory);
        var allSkills = builder.GetAllSkills();

        // Assert
        Assert.Single(allSkills);
        Assert.True(allSkills.ContainsKey("valid-skill"));
    }

    [Fact]
    public void GetSkillsByTag_MultipleSkillsWithSameTag_ReturnsAll()
    {
        // Arrange
        var skill1Path = Path.Combine(_tempDirectory, "skill1", "SKILL.md");
        var skill2Path = Path.Combine(_tempDirectory, "skill2", "SKILL.md");
        var skill3Path = Path.Combine(_tempDirectory, "skill3", "SKILL.md");

        Directory.CreateDirectory(Path.GetDirectoryName(skill1Path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(skill2Path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(skill3Path)!);

        File.WriteAllText(skill1Path, @"---
name: skill-a
description: Skill A
tags: [api, test]
roles: [Builder]
scope: global
---

Body A.");

        File.WriteAllText(skill2Path, @"---
name: skill-b
description: Skill B
tags: [api, database]
roles: [Builder]
scope: global
---

Body B.");

        File.WriteAllText(skill3Path, @"---
name: skill-c
description: Skill C
tags: [test]
roles: [Builder]
scope: global
---

Body C.");

        var builder = new SkillIndexBuilder(NullLogger<SkillIndexBuilder>.Instance, NullLoggerFactory.Instance);
        builder.BuildIndex(_tempDirectory);

        // Act
        var apiSkills = builder.GetSkillsByTag("api");
        var testSkills = builder.GetSkillsByTag("test");
        var dbSkills = builder.GetSkillsByTag("database");

        // Assert
        Assert.Equal(2, apiSkills.Count);
        Assert.Contains(apiSkills, s => s.Name == "skill-a");
        Assert.Contains(apiSkills, s => s.Name == "skill-b");

        Assert.Equal(2, testSkills.Count);
        Assert.Contains(testSkills, s => s.Name == "skill-a");
        Assert.Contains(testSkills, s => s.Name == "skill-c");

        Assert.Single(dbSkills);
        Assert.Equal("skill-b", dbSkills[0].Name);
    }

    [Fact]
    public void GetSkillsByTag_NonExistentTag_ReturnsEmpty()
    {
        // Arrange
        var skillPath = Path.Combine(_tempDirectory, "skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);

        File.WriteAllText(skillPath, @"---
name: test-skill
description: Test
tags: [existing]
roles: [Builder]
scope: global
---

Body.");

        var builder = new SkillIndexBuilder(NullLogger<SkillIndexBuilder>.Instance, NullLoggerFactory.Instance);
        builder.BuildIndex(_tempDirectory);

        // Act
        var result = builder.GetSkillsByTag("nonexistent");

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void GetSkillsByRole_FiltersByRole_ReturnsMatching()
    {
        // Arrange
        var skill1Path = Path.Combine(_tempDirectory, "skill1", "SKILL.md");
        var skill2Path = Path.Combine(_tempDirectory, "skill2", "SKILL.md");
        var skill3Path = Path.Combine(_tempDirectory, "skill3", "SKILL.md");

        Directory.CreateDirectory(Path.GetDirectoryName(skill1Path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(skill2Path)!);
        Directory.CreateDirectory(Path.GetDirectoryName(skill3Path)!);

        File.WriteAllText(skill1Path, @"---
name: planner-skill
description: For planner
tags: [test]
roles: [Planner]
scope: global
---

Planner body.");

        File.WriteAllText(skill2Path, @"---
name: builder-skill
description: For builder
tags: [test]
roles: [Builder, Planner]
scope: global
---

Builder body.");

        File.WriteAllText(skill3Path, @"---
name: researcher-skill
description: For researcher
tags: [test]
roles: [Researcher]
scope: global
---

Researcher body.");

        var builder = new SkillIndexBuilder(NullLogger<SkillIndexBuilder>.Instance, NullLoggerFactory.Instance);
        builder.BuildIndex(_tempDirectory);

        // Act
        var plannerSkills = builder.GetSkillsByRole(SwarmRole.Planner);
        var builderSkills = builder.GetSkillsByRole(SwarmRole.Builder);
        var researcherSkills = builder.GetSkillsByRole(SwarmRole.Researcher);

        // Assert
        Assert.Equal(2, plannerSkills.Count);
        Assert.Contains(plannerSkills, s => s.Name == "planner-skill");
        Assert.Contains(plannerSkills, s => s.Name == "builder-skill");

        Assert.Single(builderSkills);
        Assert.Equal("builder-skill", builderSkills[0].Name);

        Assert.Single(researcherSkills);
        Assert.Equal("researcher-skill", researcherSkills[0].Name);
    }

    [Fact]
    public void BuildIndex_EmptyDirectory_ReturnsEmptyCollections()
    {
        // Arrange
        var emptyDir = Path.Combine(_tempDirectory, "empty");
        Directory.CreateDirectory(emptyDir);

        var builder = new SkillIndexBuilder(NullLogger<SkillIndexBuilder>.Instance, NullLoggerFactory.Instance);

        // Act
        builder.BuildIndex(emptyDir);
        var allSkills = builder.GetAllSkills();

        // Assert
        Assert.Empty(allSkills);
    }

    [Fact]
    public void BuildIndex_NonExistentPath_ThrowsDirectoryNotFoundException()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDirectory, "does-not-exist");
        var builder = new SkillIndexBuilder(NullLogger<SkillIndexBuilder>.Instance, NullLoggerFactory.Instance);

        // Act & Assert
        Assert.Throws<DirectoryNotFoundException>(() => builder.BuildIndex(nonExistentPath));
    }

    [Fact]
    public void GetAllSkills_ReturnsReadOnlyDictionary_ContainsAllIndexedSkills()
    {
        // Arrange
        var skillPath = Path.Combine(_tempDirectory, "SKILL.md");
        File.WriteAllText(skillPath, @"---
name: readonly-test
description: Test readonly
tags: [test]
roles: [Builder]
scope: global
---

Body.");

        var builder = new SkillIndexBuilder(NullLogger<SkillIndexBuilder>.Instance, NullLoggerFactory.Instance);
        builder.BuildIndex(_tempDirectory);

        // Act
        var skills = builder.GetAllSkills();

        // Assert
        Assert.Single(skills);
        Assert.IsAssignableFrom<IReadOnlyDictionary<string, SkillDefinition>>(skills);
    }
}
