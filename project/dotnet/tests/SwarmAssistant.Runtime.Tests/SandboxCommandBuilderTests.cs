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

    [Fact]
    public void BuildForLevel_BareCli_ReturnsOriginalCommand()
    {
        var command = SandboxCommandBuilder.BuildForLevel(
            SandboxLevel.BareCli,
            "copilot",
            ["--help"],
            "/workspace",
            []);

        Assert.Equal("copilot", command.Command);
        Assert.Equal(["--help"], command.Args);
    }

    [Fact]
    public void BuildForLevel_OsSandboxed_WrapsMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var command = SandboxCommandBuilder.BuildForLevel(
            SandboxLevel.OsSandboxed,
            "copilot",
            ["--help"],
            "/workspace",
            ["api.github.com"]);

        Assert.Equal("sandbox-exec", command.Command);
        Assert.Contains("-p", command.Args);
        Assert.Contains("copilot", command.Args);
        Assert.Contains("--help", command.Args);
    }

    [Fact]
    public void BuildForLevel_Container_ThrowsWhenImageMissing()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SandboxCommandBuilder.BuildForLevel(
                SandboxLevel.Container,
                "copilot",
                ["--help"],
                "/workspace",
                [],
                containerImage: null));

        Assert.Contains("Container lifecycle handles command wrapping separately", exception.Message);
    }

    [Fact]
    public void BuildForLevel_Container_ReturnsDockerRunCommand()
    {
        var result = SandboxCommandBuilder.BuildForLevel(
            SandboxLevel.Container,
            "bash",
            ["-c", "echo test"],
            "/workspace",
            [],
            containerImage: "ubuntu:22.04",
            cpuLimit: 1.0,
            memoryLimit: "512m",
            timeoutSeconds: 30,
            allowA2A: false);

        Assert.Equal("docker", result.Command);
        Assert.Contains("run", result.Args);
        Assert.Contains("--rm", result.Args);
        Assert.Contains("ubuntu:22.04", result.Args);
        Assert.Contains("bash", result.Args);
        Assert.Contains("-c", result.Args);
        Assert.Contains("echo test", result.Args);

        // Verify network isolation
        Assert.Contains("--network=none", result.Args);

        // Verify resource limits
        Assert.Contains("--cpus=1", result.Args);
        Assert.Contains("--memory=512m", result.Args);
        Assert.Contains("--stop-timeout=30", result.Args);
    }

    [Fact]
    public void BuildForLevel_OsSandboxed_IncludesWorkspacePath()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var options = new RuntimeOptions
        {
            SandboxMode = "os-sandboxed"
        };

        var result = SandboxCommandBuilder.BuildForLevel(
            SandboxLevel.OsSandboxed,
            "echo",
            ["test"],
            "/workspace/path",
            ["api.example.com"]);

        Assert.Contains(result.Args, arg => arg.Contains("/workspace/path"));
    }
}
