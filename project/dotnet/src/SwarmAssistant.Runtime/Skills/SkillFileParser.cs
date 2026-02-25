using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Contracts.Messaging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SwarmAssistant.Runtime.Skills;

public sealed class SkillFileParser
{
    private sealed class SkillFrontmatter
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public object? Tags { get; init; }
        public object? Roles { get; init; }
        public string Scope { get; init; } = string.Empty;
    }

    private static readonly Regex FrontmatterRegex = new(
        @"\A\uFEFF?^---\s*$(.+?)^---\s*$",
        RegexOptions.Multiline | RegexOptions.Singleline,
        TimeSpan.FromSeconds(5)
    );

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly ILogger<SkillFileParser> _logger;

    public SkillFileParser(ILogger<SkillFileParser>? logger = null)
    {
        _logger = logger ?? NullLogger<SkillFileParser>.Instance;
    }

    public SkillDefinition? Parse(string content, string sourcePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Empty content for skill file: {SourcePath}", sourcePath);
                return null;
            }

            var match = FrontmatterRegex.Match(content);
            if (!match.Success)
            {
                _logger.LogWarning("No frontmatter found in skill file: {SourcePath}", sourcePath);
                return null;
            }

            var frontmatterText = match.Groups[1].Value;
            var body = content.Substring(match.Index + match.Length).TrimStart();

            var frontmatter = YamlDeserializer.Deserialize<SkillFrontmatter>(frontmatterText);

            if (string.IsNullOrWhiteSpace(frontmatter.Name))
            {
                _logger.LogWarning("Missing or empty 'name' in skill file: {SourcePath}", sourcePath);
                return null;
            }

            if (string.IsNullOrWhiteSpace(frontmatter.Description))
            {
                _logger.LogWarning("Missing or empty 'description' in skill file: {SourcePath}", sourcePath);
                return null;
            }

            var tags = ParseTags(frontmatter.Tags);
            if (tags.Count == 0)
            {
                _logger.LogWarning("No tags found in skill file: {SourcePath}", sourcePath);
                return null;
            }

            var roles = ParseRoles(frontmatter.Roles, sourcePath);
            var scope = string.IsNullOrWhiteSpace(frontmatter.Scope) ? "global" : frontmatter.Scope;

            return new SkillDefinition(
                name: frontmatter.Name,
                description: frontmatter.Description,
                tags: tags,
                roles: roles,
                scope: scope,
                body: body,
                sourcePath: sourcePath
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse skill file: {SourcePath}", sourcePath);
            return null;
        }
    }

    private IReadOnlyList<string> ParseTags(object? tagsObj)
    {
        if (tagsObj is null)
        {
            return Array.Empty<string>();
        }

        if (tagsObj is string tagsString)
        {
            return tagsString
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
        }

        if (tagsObj is IEnumerable<object> tagsList)
        {
            return tagsList
                .Select(t => t?.ToString() ?? string.Empty)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToArray();
        }

        return Array.Empty<string>();
    }

    private IReadOnlyList<SwarmRole> ParseRoles(object? rolesObj, string sourcePath)
    {
        var roles = new List<SwarmRole>();

        if (rolesObj is null)
        {
            return roles;
        }

        IEnumerable<string> roleStrings;
        if (rolesObj is string rolesString)
        {
            roleStrings = rolesString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else if (rolesObj is IEnumerable<object> rolesList)
        {
            roleStrings = rolesList.Select(r => r?.ToString() ?? string.Empty);
        }
        else
        {
            return roles;
        }

        foreach (var roleStr in roleStrings)
        {
            if (string.IsNullOrWhiteSpace(roleStr))
            {
                continue;
            }

            if (Enum.TryParse<SwarmRole>(roleStr, ignoreCase: true, out var role))
            {
                roles.Add(role);
            }
            else
            {
                _logger.LogWarning("Unknown role '{Role}' in skill file: {SourcePath}", roleStr, sourcePath);
            }
        }

        return roles;
    }
}
