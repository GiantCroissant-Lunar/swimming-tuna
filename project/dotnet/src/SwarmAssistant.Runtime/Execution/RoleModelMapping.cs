using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Execution;

internal sealed class RoleModelMapping
{
    private static readonly IReadOnlyDictionary<string, string> AdapterProviderFallbacks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["copilot"] = "github-copilot",
            ["cline"] = "cline",
            ["kimi"] = "moonshot",
            ["kilo"] = "kilo",
            ["local-echo"] = "local-echo"
        };

    private readonly IReadOnlyDictionary<SwarmRole, RoleModelPreference> _preferences;

    private RoleModelMapping(IReadOnlyDictionary<SwarmRole, RoleModelPreference> preferences)
    {
        _preferences = preferences;
    }

    public static RoleModelMapping FromOptions(RuntimeOptions options)
    {
        var preferences = new Dictionary<SwarmRole, RoleModelPreference>();
        foreach (var (key, value) in options.RoleModelMapping)
        {
            if (value is null || string.IsNullOrWhiteSpace(value.Model))
            {
                continue;
            }

            if (!Enum.TryParse<SwarmRole>(key, ignoreCase: true, out var role))
            {
                continue;
            }

            preferences[role] = value;
        }

        return new RoleModelMapping(preferences);
    }

    public ResolvedRoleModel? Resolve(SwarmRole role, string adapterId)
    {
        if (!_preferences.TryGetValue(role, out var preference) ||
            string.IsNullOrWhiteSpace(preference.Model))
        {
            return null;
        }

        var model = ParseModelSpec(preference.Model, adapterId);
        return new ResolvedRoleModel(model, preference.Reasoning);
    }

    internal static ModelSpec ParseModelSpec(string modelValue, string adapterId)
    {
        var trimmed = modelValue.Trim();
        var slash = trimmed.IndexOf('/');
        if (slash > 0 && slash < trimmed.Length - 1)
        {
            var provider = trimmed[..slash].Trim();
            var modelId = trimmed[(slash + 1)..].Trim();
            return new ModelSpec
            {
                Id = modelId,
                Provider = provider,
                DisplayName = modelId
            };
        }

        return new ModelSpec
        {
            Id = trimmed,
            Provider = ResolveProviderFallback(adapterId),
            DisplayName = trimmed
        };
    }

    private static string ResolveProviderFallback(string adapterId)
    {
        if (AdapterProviderFallbacks.TryGetValue(adapterId, out var provider))
        {
            return provider;
        }

        return adapterId;
    }
}

internal sealed record ResolvedRoleModel(ModelSpec Model, string? Reasoning);
