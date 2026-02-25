using Microsoft.Extensions.Logging;
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

        var skillFiles = Directory.EnumerateFiles(
            basePath,
            "SKILL.md",
            SearchOption.AllDirectories
        );

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

                if (_skillsByName.ContainsKey(skill.Name))
                {
                    _logger.LogWarning(
                        "Duplicate skill name '{SkillName}' found. Existing: {ExistingPath}, Duplicate: {DuplicatePath}. Keeping first.",
                        skill.Name,
                        _skillsByName[skill.Name].SourcePath,
                        filePath
                    );
                    continue;
                }

                _skillsByName[skill.Name] = skill;

                foreach (var tag in skill.Tags)
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
        return _skillsByName;
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
