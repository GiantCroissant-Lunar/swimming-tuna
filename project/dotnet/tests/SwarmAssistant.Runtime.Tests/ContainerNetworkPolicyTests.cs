namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Execution;

public sealed class ContainerNetworkPolicyTests
{
    [Fact]
    public void BuildNetworkArgs_NoHosts_DisablesNetwork()
    {
        var result = ContainerNetworkPolicy.BuildNetworkArgs(null, allowA2A: false);

        Assert.Single(result);
        Assert.Equal("--network=none", result[0]);
    }

    [Fact]
    public void BuildNetworkArgs_WithHosts_UsesBridgeNetwork()
    {
        var allowedHosts = new List<string> { "example.com", "api.github.com" };

        var result = ContainerNetworkPolicy.BuildNetworkArgs(allowedHosts, allowA2A: false);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildNetworkArgs_A2AAllowed_IncludesHostGateway()
    {
        var result = ContainerNetworkPolicy.BuildNetworkArgs(null, allowA2A: true);

        Assert.Contains("--add-host=host.docker.internal:host-gateway", result);
    }
}
