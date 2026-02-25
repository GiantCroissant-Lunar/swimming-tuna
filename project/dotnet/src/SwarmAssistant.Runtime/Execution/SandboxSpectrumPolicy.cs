namespace SwarmAssistant.Runtime.Execution;

using SwarmAssistant.Contracts.Messaging;

public static class SandboxSpectrumPolicy
{
    public static SandboxLevel RecommendLevel(SandboxRequirements requirements)
    {
        if (requirements.NeedsOAuth || requirements.NeedsKeychain)
        {
            return SandboxLevel.BareCli;
        }

        if (requirements.NeedsNetwork.Length > 0)
        {
            return SandboxLevel.OsSandboxed;
        }

        return SandboxLevel.Container;
    }
}
