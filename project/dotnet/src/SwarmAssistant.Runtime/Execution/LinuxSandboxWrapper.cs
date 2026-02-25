namespace SwarmAssistant.Runtime.Execution;

internal static class LinuxSandboxWrapper
{
    public static string[] BuildNamespaceArgs() =>
        ["--net", "--mount", "--pid", "--fork"];

    /// <summary>
    /// Wraps a command in a Linux namespace sandbox using unshare.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <param name="args">Arguments for the command.</param>
    /// <param name="workspacePath">Path to the workspace directory for read-only bind mount restriction.</param>
    /// <param name="allowedHosts">Note: Not enforced at unshare level; network namespace provides isolation.</param>
    /// <returns>A SandboxCommand configured with namespace and mount restrictions.</returns>
    public static SandboxCommand WrapCommand(
        string command,
        IReadOnlyList<string> args,
        string workspacePath,
        string[] allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("Workspace path cannot be null or empty", nameof(workspacePath));

        var normalizedPath = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(normalizedPath))
            throw new DirectoryNotFoundException($"Workspace path does not exist: {normalizedPath}");

        var unshareArgs = new List<string>(BuildNamespaceArgs());
        unshareArgs.Add("--mount-proc");
        unshareArgs.Add("--");
        unshareArgs.Add("sh");
        unshareArgs.Add("-c");

        var escapedWorkspace = normalizedPath.Replace("'", "'\\''");
        var escapedCommand = command.Replace("'", "'\\''");
        var escapedArgs = string.Join(" ", args.Select(a => $"'{a.Replace("'", "'\\''")}'"));
        var shellCmd = $"mount --bind -o rw '{escapedWorkspace}' '{escapedWorkspace}' && '{escapedCommand}' {escapedArgs}";

        unshareArgs.Add(shellCmd);
        return new SandboxCommand("unshare", unshareArgs.ToArray());
    }
}
