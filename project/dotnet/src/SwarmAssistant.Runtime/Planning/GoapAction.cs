using System.Collections.Frozen;
using SwarmAssistant.Contracts.Planning;

namespace SwarmAssistant.Runtime.Planning;

public sealed class GoapAction : IGoapAction
{
    public string Name { get; }
    public IReadOnlyDictionary<WorldKey, bool> Preconditions { get; }
    public IReadOnlyDictionary<WorldKey, bool> Effects { get; }
    public int Cost { get; }

    public GoapAction(
        string name,
        IReadOnlyDictionary<WorldKey, bool> preconditions,
        IReadOnlyDictionary<WorldKey, bool> effects,
        int cost)
    {
        Name = name;
        Preconditions = preconditions.ToFrozenDictionary();
        Effects = effects.ToFrozenDictionary();
        Cost = cost;
    }

    public override string ToString() => $"{Name} (cost={Cost})";
}
