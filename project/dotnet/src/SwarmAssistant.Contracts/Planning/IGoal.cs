namespace SwarmAssistant.Contracts.Planning;

public interface IGoal
{
    string Name { get; }
    IReadOnlyDictionary<WorldKey, bool> TargetState { get; }
}
