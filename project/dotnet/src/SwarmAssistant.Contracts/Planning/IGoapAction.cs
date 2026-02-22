namespace SwarmAssistant.Contracts.Planning;

public interface IGoapAction
{
    string Name { get; }
    IReadOnlyDictionary<WorldKey, bool> Preconditions { get; }
    IReadOnlyDictionary<WorldKey, bool> Effects { get; }
    int Cost { get; }
}
