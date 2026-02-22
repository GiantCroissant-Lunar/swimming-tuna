namespace SwarmAssistant.Contracts.Planning;

public interface IWorldState
{
    bool Get(WorldKey key);
    IWorldState With(WorldKey key, bool value);
    IReadOnlyDictionary<WorldKey, bool> GetAll();
}
