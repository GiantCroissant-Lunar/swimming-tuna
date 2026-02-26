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
}
