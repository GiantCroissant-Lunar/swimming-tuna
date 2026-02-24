namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Execution;

public sealed class LinuxSandboxWrapperTests
{
    [Fact]
    public void WrapCommand_UsesUnshare()
    {
        var result = LinuxSandboxWrapper.WrapCommand(
            command: "copilot",
            args: ["--prompt", "hello"],
            workspacePath: Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            allowedHosts: []);

        Assert.Equal("unshare", result.Command);
        Assert.Contains("--net", result.Args);
        Assert.Contains("--mount", result.Args);
    }

    [Fact]
    public void WrapCommand_UsesShellForCommandChaining()
    {
        var result = LinuxSandboxWrapper.WrapCommand(
            command: "copilot",
            args: ["--prompt", "hello"],
            workspacePath: Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            allowedHosts: []);

        Assert.Contains("sh", result.Args);
        Assert.Contains("-c", result.Args);
        var shellCmdArg = result.Args.FirstOrDefault(a => a.Contains("&&"));
        Assert.NotNull(shellCmdArg);
        Assert.Contains("mount --bind", shellCmdArg);
        Assert.Contains("copilot", shellCmdArg);
    }

    [Fact]
    public void BuildNamespaceArgs_IncludesNetAndMount()
    {
        var args = LinuxSandboxWrapper.BuildNamespaceArgs();

        Assert.Contains("--net", args);
        Assert.Contains("--mount", args);
    }

    [Fact]
    public void WrapCommand_IncludesWorkspacePathInMountArgs()
    {
        var tmpPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var result = LinuxSandboxWrapper.WrapCommand(
            command: "copilot",
            args: ["--prompt", "hello"],
            workspacePath: tmpPath,
            allowedHosts: []);

        Assert.Contains("--mount-proc", result.Args);
        var shellCmdArg = result.Args.FirstOrDefault(a => a.Contains("mount --bind"));
        Assert.NotNull(shellCmdArg);
        Assert.Contains(Path.GetFullPath(tmpPath), shellCmdArg);
    }

    [Fact]
    public void WrapCommand_ThrowsOnNullWorkspacePath()
    {
        Assert.Throws<ArgumentException>(() =>
            LinuxSandboxWrapper.WrapCommand("copilot", [], null!, []));
    }

    [Fact]
    public void WrapCommand_ThrowsOnEmptyWorkspacePath()
    {
        Assert.Throws<ArgumentException>(() =>
            LinuxSandboxWrapper.WrapCommand("copilot", [], "", []));
    }

    [Fact]
    public void WrapCommand_ThrowsOnNonExistentWorkspacePath()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            LinuxSandboxWrapper.WrapCommand("copilot", [], "/non/existent/path", []));
    }

    [Fact]
    public void WrapCommand_EscapesSpecialCharactersInArguments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "test-workspace");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = LinuxSandboxWrapper.WrapCommand(
                command: "echo",
                args: ["hello'world", "test\"value"],
                workspacePath: tempDir,
                allowedHosts: []);

            var shellCmdArg = result.Args.FirstOrDefault(a => a.Contains("&&"));
            Assert.NotNull(shellCmdArg);
            Assert.Contains("hello'\\''world", shellCmdArg);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
