using System.Collections.Frozen;
using SwarmAssistant.Contracts.Planning;

namespace SwarmAssistant.Runtime.Planning;

public sealed class WorldState : IWorldState, IEquatable<WorldState>
{
    private readonly FrozenDictionary<WorldKey, bool> _state;

    public WorldState()
        : this(FrozenDictionary<WorldKey, bool>.Empty)
    {
    }

    private WorldState(FrozenDictionary<WorldKey, bool> state)
    {
        _state = state;
    }

    public bool Get(WorldKey key) => _state.TryGetValue(key, out var value) && value;

    public IWorldState With(WorldKey key, bool value)
    {
        var dict = _state.ToDictionary();
        dict[key] = value;
        return new WorldState(dict.ToFrozenDictionary());
    }

    public IReadOnlyDictionary<WorldKey, bool> GetAll() => _state;

    public bool Satisfies(IReadOnlyDictionary<WorldKey, bool> conditions)
    {
        foreach (var (key, required) in conditions)
        {
            if (Get(key) != required)
            {
                return false;
            }
        }

        return true;
    }

    public IReadOnlyList<WorldKey> Unsatisfied(IReadOnlyDictionary<WorldKey, bool> conditions)
    {
        var result = new List<WorldKey>();
        foreach (var (key, required) in conditions)
        {
            if (Get(key) != required)
            {
                result.Add(key);
            }
        }

        return result;
    }

    public WorldState Apply(IGoapAction action)
    {
        var dict = _state.ToDictionary();
        foreach (var (key, value) in action.Effects)
        {
            dict[key] = value;
        }

        return new WorldState(dict.ToFrozenDictionary());
    }

    public bool Equals(WorldState? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_state.Count != other._state.Count) return false;

        foreach (var (key, value) in _state)
        {
            if (!other._state.TryGetValue(key, out var otherValue) || value != otherValue)
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as WorldState);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (key, value) in _state.OrderBy(kv => kv.Key))
        {
            hash.Add(key);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var trueKeys = _state.Where(kv => kv.Value).Select(kv => kv.Key.ToString());
        return $"{{ {string.Join(", ", trueKeys)} }}";
    }
}
