using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Skills;

public sealed record SkillDefinition
{
    public string Name { get; init; }
    public string Description { get; init; }
    public IReadOnlyList<string> Tags { get; init; }
    public IReadOnlyList<SwarmRole> Roles { get; init; }
    public string Scope { get; init; }
    public string Body { get; init; }
    public string SourcePath { get; init; }

    public SkillDefinition(string name, string description, IReadOnlyList<string> tags, IReadOnlyList<SwarmRole> roles, string scope, string body, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        if (roles.Count == 0)
            throw new ArgumentException("At least one role is required.", nameof(roles));

        if (!scope.Equals("global", StringComparison.OrdinalIgnoreCase) &&
            !scope.StartsWith("task:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Scope must be 'global' or start with 'task:'.", nameof(scope));

        Name = name;
        Description = description;
        Tags = tags;
        Roles = roles;
        Scope = scope;
        Body = body;
        SourcePath = sourcePath;
    }
}
