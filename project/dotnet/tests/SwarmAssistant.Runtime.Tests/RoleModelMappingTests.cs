using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class RoleModelMappingTests
{
    [Fact]
    public void Resolve_ModelWithExplicitProvider_UsesProvidedProvider()
    {
        var options = new RuntimeOptions
        {
            RoleModelMapping = new Dictionary<string, RoleModelPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["Planner"] = new() { Model = "anthropic/claude-sonnet-4-6" }
            }
        };

        var mapping = RoleModelMapping.FromOptions(options);
        var resolved = mapping.Resolve(SwarmRole.Planner, "kilo");

        Assert.NotNull(resolved);
        Assert.Equal("anthropic", resolved!.Model.Provider);
        Assert.Equal("claude-sonnet-4-6", resolved.Model.Id);
    }

    [Fact]
    public void Resolve_ModelWithoutProvider_UsesAdapterProviderFallback()
    {
        var options = new RuntimeOptions
        {
            RoleModelMapping = new Dictionary<string, RoleModelPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["Builder"] = new() { Model = "giga-potato" }
            }
        };

        var mapping = RoleModelMapping.FromOptions(options);
        var resolved = mapping.Resolve(SwarmRole.Builder, "kilo");

        Assert.NotNull(resolved);
        Assert.Equal("kilo", resolved!.Model.Provider);
        Assert.Equal("giga-potato", resolved.Model.Id);
    }

    [Fact]
    public void Resolve_UnknownRoleMappingKey_IsIgnored()
    {
        var options = new RuntimeOptions
        {
            RoleModelMapping = new Dictionary<string, RoleModelPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["NotARole"] = new() { Model = "something" }
            }
        };

        var mapping = RoleModelMapping.FromOptions(options);
        var resolved = mapping.Resolve(SwarmRole.Planner, "kimi");

        Assert.Null(resolved);
    }

    [Fact]
    public void ParseModelSpec_WhitespaceModel_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            RoleModelMapping.ParseModelSpec("   ", adapterId: "kilo"));

        Assert.Contains("Model value cannot be empty", exception.Message);
    }

    [Fact]
    public void ParseModelSpec_MissingProvider_UsesFallbackProvider()
    {
        var spec = RoleModelMapping.ParseModelSpec("/gpt-4o-mini", adapterId: "api-openai");

        Assert.Equal("openai", spec.Provider);
        Assert.Equal("gpt-4o-mini", spec.Id);
        Assert.Equal("gpt-4o-mini", spec.DisplayName);
    }

    [Fact]
    public void ParseModelSpec_MissingModelId_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            RoleModelMapping.ParseModelSpec("openai/ ", adapterId: "kilo"));

        Assert.Contains("Invalid model format", exception.Message);
    }

    [Fact]
    public void ParseModelSpec_WithPiAdapter_ResolvesToPiAgentProvider()
    {
        var spec = RoleModelMapping.ParseModelSpec("pi-model", adapterId: "pi");

        Assert.Equal("pi-agent", spec.Provider);
        Assert.Equal("pi-model", spec.Id);
        Assert.Equal("pi-model", spec.DisplayName);
    }

    [Fact]
    public void Resolve_WithPiAdapter_UsesPiAgentProviderFallback()
    {
        var options = new RuntimeOptions
        {
            RoleModelMapping = new Dictionary<string, RoleModelPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["Builder"] = new() { Model = "pi-model-v1" }
            }
        };

        var mapping = RoleModelMapping.FromOptions(options);
        var resolved = mapping.Resolve(SwarmRole.Builder, "pi");

        Assert.NotNull(resolved);
        Assert.Equal("pi-agent", resolved!.Model.Provider);
        Assert.Equal("pi-model-v1", resolved.Model.Id);
    }
}
