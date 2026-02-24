namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Configuration;

public sealed class ConfigurationTests
{
    [Fact]
    public void AgentEndpoint_DefaultsAreCorrect()
    {
        var opts = new RuntimeOptions();
        Assert.False(opts.AgentEndpointEnabled);
        Assert.Equal("8001-8032", opts.AgentEndpointPortRange);
        Assert.Equal(30, opts.AgentHeartbeatIntervalSeconds);
    }
}
