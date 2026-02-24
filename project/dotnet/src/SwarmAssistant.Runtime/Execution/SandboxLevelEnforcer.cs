using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Execution;

internal sealed class SandboxLevelEnforcer
{
    private readonly bool _containerAvailable;

    public SandboxLevelEnforcer(bool containerAvailable = true)
    {
        _containerAvailable = containerAvailable;
    }

    public bool CanEnforce(SandboxLevel level) => level switch
    {
        SandboxLevel.BareCli => true,
        SandboxLevel.OsSandboxed => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(),
        SandboxLevel.Container => _containerAvailable,
        _ => false
    };

    public SandboxLevel GetEffectiveLevel(SandboxLevel declared)
    {
        if (CanEnforce(declared))
            return declared;

        // Fall back: Container -> OsSandboxed -> BareCli
        for (var fallback = declared - 1; fallback >= 0; fallback--)
        {
            if (CanEnforce(fallback))
                return fallback;
        }

        return SandboxLevel.BareCli;
    }
}
