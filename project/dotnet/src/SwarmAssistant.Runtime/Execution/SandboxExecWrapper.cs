namespace SwarmAssistant.Runtime.Execution;

internal static class SandboxExecWrapper
{
    private static string EscapeSbpl(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static string BuildProfile(string workspacePath, string[] allowedHosts)
    {
        var sbplSafePath = EscapeSbpl(workspacePath);

        var profile = "(version 1)\n";
        profile += "(deny default)\n";
        profile += "(allow process*)\n";
        profile += "(allow process-exec)\n";
        profile += "(allow sysctl-read)\n";
        profile += "(allow mach-lookup)\n";

        // File access: read everywhere, write only to workspace
        profile += "(allow file-read*)\n";
        profile += "(deny file-write*)\n";
        profile += $"(allow file-write* (subpath \"{sbplSafePath}\"))\n";
        profile += "(allow file-write* (subpath \"/tmp\"))\n";
        profile += "(allow file-write* (subpath \"/private/tmp\"))\n";

        // Network: deny by default, allow specific hosts if requested.
        // SBPL limitation: Apple Sandbox Profile Language only supports all-or-nothing TCP
        // filtering. It cannot restrict outbound connections to specific hostnames or IPs.
        // When allowedHosts is non-empty we open all outbound TCP (the best SBPL can do)
        // and list the intended hosts as comments. Per-host filtering requires an external
        // proxy (e.g. squid, mitmproxy) or a Network Extension at a higher layer.
        profile += "(deny network*)\n";
        if (allowedHosts.Length > 0)
        {
            profile += "(allow network-outbound (remote tcp))\n";
            foreach (var host in allowedHosts)
            {
                profile += $";; allowed host: {EscapeSbpl(host)}\n";
            }
        }

        return profile;
    }

    public static SandboxCommand WrapCommand(
        string command,
        IReadOnlyList<string> args,
        string workspacePath,
        string[] allowedHosts)
    {
        var profile = BuildProfile(workspacePath, allowedHosts);
        var sandboxArgs = new List<string> { "-p", profile, command };
        sandboxArgs.AddRange(args);
        return new SandboxCommand("sandbox-exec", sandboxArgs.ToArray());
    }
}
