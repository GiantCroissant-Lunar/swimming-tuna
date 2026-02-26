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
    private readonly string _defaultProvider;

    private RoleModelMapping(
        IReadOnlyDictionary<SwarmRole, RoleModelPreference> preferences,
        string defaultProvider)
    {
        _preferences = preferences;
        _defaultProvider = defaultProvider;
    }

    public static RoleModelMapping FromOptions(RuntimeOptions options)
    {
        var defaultProvider = NormalizeProviderId(
            options.ApiProviderOrder.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)));
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

        return new RoleModelMapping(preferences, defaultProvider);
    }

    public ResolvedRoleModel? Resolve(SwarmRole role, string? adapterId = null)
    {
        if (!_preferences.TryGetValue(role, out var preference) ||
            string.IsNullOrWhiteSpace(preference.Model))
        {
            return null;
        }

        var model = ParseModelSpec(preference.Model, adapterId, _defaultProvider);
        return new ResolvedRoleModel(model, preference.Reasoning);
    }

    internal static ModelSpec ParseModelSpec(string modelValue, string? adapterId, string defaultProvider = "openai")
    {
        var trimmed = modelValue.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Model value cannot be empty.", nameof(modelValue));
        }

        var slash = trimmed.IndexOf('/');
        if (slash >= 0)
        {
            var provider = slash == 0 ? string.Empty : trimmed[..slash].Trim();
            var modelId = slash == trimmed.Length - 1 ? string.Empty : trimmed[(slash + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(modelId))
            {
                throw new ArgumentException($"Invalid model format '{modelValue}'. Expected 'provider/model' or 'model'.", nameof(modelValue));
            }

            var resolvedProvider = string.IsNullOrWhiteSpace(provider)
                ? ResolveProviderFallback(adapterId, defaultProvider)
                : NormalizeProviderId(provider, defaultProvider);

            return new ModelSpec
            {
                Id = modelId,
                Provider = resolvedProvider,
                DisplayName = modelId
            };
        }

        return new ModelSpec
        {
            Id = trimmed,
            Provider = ResolveProviderFallback(adapterId, defaultProvider),
            DisplayName = trimmed
        };
    }

    private static string ResolveProviderFallback(string? adapterId, string defaultProvider)
    {
        var normalizedDefaultProvider = NormalizeProviderId(defaultProvider);
        if (string.IsNullOrWhiteSpace(adapterId))
        {
            return normalizedDefaultProvider;
        }

        var normalizedAdapterId = adapterId.Trim();
        if (AdapterProviderFallbacks.TryGetValue(normalizedAdapterId, out var provider))
        {
            return NormalizeProviderId(provider, normalizedDefaultProvider);
        }

        return NormalizeProviderId(normalizedAdapterId, normalizedDefaultProvider);
    }

    private static string NormalizeProviderId(string? providerId, string fallback = "openai")
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return fallback;
        }

        var normalized = providerId.Trim();
        if (normalized.StartsWith("api-", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["api-".Length..];
        }

        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}

internal sealed record ResolvedRoleModel(ModelSpec Model, string? Reasoning);
