using System.Collections.ObjectModel;
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Skills;

public sealed class SkillIndexBuilder
{
    private readonly ILogger<SkillIndexBuilder> _logger;
    private readonly SkillFileParser _parser;
    private readonly Dictionary<string, SkillDefinition> _skillsByName = new();
    private readonly Dictionary<string, List<string>> _tagIndex = new();

    public SkillIndexBuilder(ILogger<SkillIndexBuilder> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _parser = new SkillFileParser(loggerFactory.CreateLogger<SkillFileParser>());
    }

    public void BuildIndex(string basePath)
    {
        if (!Directory.Exists(basePath))
        {
            _logger.LogError("Skill base path does not exist: {BasePath}", basePath);
            throw new DirectoryNotFoundException($"Skill base path does not exist: {basePath}");
        }

        _skillsByName.Clear();
        _tagIndex.Clear();

        IEnumerable<string> skillFiles;
        try
        {
            skillFiles = Directory.EnumerateFiles(
                basePath,
                "SKILL.md",
                SearchOption.AllDirectories
            ).OrderBy(path => path, StringComparer.Ordinal);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Access denied enumerating skill files under {BasePath}", basePath);
            return;
        }

        foreach (var filePath in skillFiles)
        {
            try
            {
                var content = File.ReadAllText(filePath);
                var skill = _parser.Parse(content, filePath);

                if (skill is null)
                {
                    _logger.LogWarning("Failed to parse skill file: {FilePath}", filePath);
                    continue;
                }

                if (_skillsByName.TryGetValue(skill.Name, out SkillDefinition? value))
                {
                    _logger.LogWarning(
                        "Duplicate skill name '{SkillName}' found. Existing: {ExistingPath}, Duplicate: {DuplicatePath}. Keeping first.",
                        skill.Name,
                        value.SourcePath,
                        filePath
                    );
                    continue;
                }

                _skillsByName[skill.Name] = skill;

                foreach (var tag in skill.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!_tagIndex.TryGetValue(tag, out var skillNames))
                    {
                        skillNames = [];
                        _tagIndex[tag] = skillNames;
                    }
                    skillNames.Add(skill.Name);
                }

                _logger.LogDebug("Indexed skill: {SkillName} from {FilePath}", skill.Name, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error processing skill file: {FilePath}", filePath);
            }
        }

        _logger.LogInformation("Skill index built: {SkillCount} skills, {TagCount} tags",
            _skillsByName.Count,
            _tagIndex.Count
        );
    }

    public IReadOnlyDictionary<string, SkillDefinition> GetAllSkills()
    {
        return new ReadOnlyDictionary<string, SkillDefinition>(_skillsByName);
    }

    public IReadOnlyList<SkillDefinition> GetSkillsByTag(string tag)
    {
        if (!_tagIndex.TryGetValue(tag, out var skillNames))
        {
            return [];
        }

        return skillNames
            .Select(name => _skillsByName[name])
            .ToList();
    }

    public IReadOnlyList<SkillDefinition> GetSkillsByRole(SwarmRole role)
    {
        return _skillsByName.Values
            .Where(skill => skill.Roles.Contains(role))
            .ToList();
    }
}
