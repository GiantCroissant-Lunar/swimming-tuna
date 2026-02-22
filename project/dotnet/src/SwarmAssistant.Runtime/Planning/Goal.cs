using System.Collections.Frozen;
using SwarmAssistant.Contracts.Planning;

namespace SwarmAssistant.Runtime.Planning;

public sealed class Goal : IGoal
{
    public string Name { get; }
    public IReadOnlyDictionary<WorldKey, bool> TargetState { get; }

    public Goal(string name, IReadOnlyDictionary<WorldKey, bool> targetState)
    {
        Name = name;
        TargetState = targetState.ToFrozenDictionary();
    }

    public override string ToString() => Name;
}
