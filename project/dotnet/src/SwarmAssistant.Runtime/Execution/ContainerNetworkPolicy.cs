namespace SwarmAssistant.Runtime.Execution;

public static class ContainerNetworkPolicy
{
    /// <summary>
    /// Builds Docker network arguments for container execution.
    /// TODO: Docker does not natively support per-host egress allowlists. When allowedHosts
    /// are provided we suppress --network=none so the container gets default bridge networking,
    /// but all outbound traffic is permitted (not just the listed hosts). A future iteration
    /// should introduce an egress proxy (e.g. squid) or iptables-based solution to enforce
    /// per-host filtering at the container network layer.
    /// </summary>
    public static string[] BuildNetworkArgs(List<string>? allowedHosts, bool allowA2A = false)
    {
        var args = new List<string>();

        var hasHosts = allowedHosts != null && allowedHosts.Count > 0;

        if (!hasHosts && !allowA2A)
        {
            args.Add("--network=none");
        }

        if (allowA2A)
        {
            args.Add("--add-host=host.docker.internal:host-gateway");
        }

        return args.ToArray();
    }
}
