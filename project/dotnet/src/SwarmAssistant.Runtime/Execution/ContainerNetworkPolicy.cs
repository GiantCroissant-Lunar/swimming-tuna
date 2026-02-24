namespace SwarmAssistant.Runtime.Execution;

public static class ContainerNetworkPolicy
{
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
