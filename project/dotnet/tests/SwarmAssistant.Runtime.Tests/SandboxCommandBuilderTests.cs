using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class SandboxCommandBuilderTests
{
    [Fact]
    public void Build_HostMode_ReturnsOriginalCommand()
    {
        var options = new RuntimeOptions
        {
            SandboxMode = "host"
        };

        var command = SandboxCommandBuilder.Build(options, "copilot", ["--help"]);

        Assert.Equal("copilot", command.Command);
        Assert.Equal(["--help"], command.Args);
    }

    [Fact]
    public void Build_DockerModeWithoutWrapper_Throws()
    {
        var options = new RuntimeOptions
        {
            SandboxMode = "docker"
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SandboxCommandBuilder.Build(options, "copilot", ["--help"]));

        Assert.Contains("requires wrapper command configuration", exception.Message);
    }

    [Fact]
    public void Build_DockerModeWithWrapper_RendersPlaceholders()
    {
        var options = new RuntimeOptions
        {
            SandboxMode = "docker",
            DockerSandboxWrapper = new SandboxWrapperOptions
            {
                Command = "docker",
                Args = ["run", "--rm", "tools-image", "sh", "-lc", "{{command}} {{args_joined}}"]
            }
        };

        var command = SandboxCommandBuilder.Build(options, "copilot", ["--prompt", "hello world"]);

        Assert.Equal("docker", command.Command);
        Assert.Equal(["run", "--rm", "tools-image", "sh", "-lc", "copilot '--prompt' 'hello world'"], command.Args);
    }

    [Theory]
    [InlineData("host", SandboxLevel.BareCli)]
    [InlineData("HOST", SandboxLevel.BareCli)]
    [InlineData("docker", SandboxLevel.Container)]
    [InlineData("apple-container", SandboxLevel.Container)]
    public void ParseLevel_MapsStringToEnum(string mode, SandboxLevel expected)
    {
        Assert.Equal(expected, SandboxCommandBuilder.ParseLevel(mode));
    }

    [Fact]
    public void ParseLevel_UnknownMode_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SandboxCommandBuilder.ParseLevel("unknown-mode"));
    }
}
