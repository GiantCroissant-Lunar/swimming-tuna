using SwarmAssistant.Contracts.Planning;
using SwarmAssistant.Runtime.Planning;

namespace SwarmAssistant.Runtime.Tests.Planning;

public sealed class WorldStateTests
{
    [Fact]
    public void Get_UnsetKey_ReturnsFalse()
    {
        var state = new WorldState();

        Assert.False(state.Get(WorldKey.TaskExists));
    }

    [Fact]
    public void With_SetsKey_GetReturnsTrue()
    {
        var state = (WorldState)new WorldState().With(WorldKey.TaskExists, true);

        Assert.True(state.Get(WorldKey.TaskExists));
    }

    [Fact]
    public void With_IsImmutable_OriginalUnchanged()
    {
        var original = new WorldState();
        _ = original.With(WorldKey.TaskExists, true);

        Assert.False(original.Get(WorldKey.TaskExists));
    }

    [Fact]
    public void Satisfies_AllConditionsMet_ReturnsTrue()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true);

        var conditions = new Dictionary<WorldKey, bool>
        {
            [WorldKey.TaskExists] = true,
            [WorldKey.PlanExists] = true,
        };

        Assert.True(state.Satisfies(conditions));
    }

    [Fact]
    public void Satisfies_ConditionNotMet_ReturnsFalse()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true);

        var conditions = new Dictionary<WorldKey, bool>
        {
            [WorldKey.TaskExists] = true,
            [WorldKey.PlanExists] = true,
        };

        Assert.False(state.Satisfies(conditions));
    }

    [Fact]
    public void Satisfies_RequiresFalse_UnsetKeyMatchesFalse()
    {
        var state = new WorldState();

        var conditions = new Dictionary<WorldKey, bool>
        {
            [WorldKey.RetryLimitReached] = false,
        };

        Assert.True(((WorldState)state).Satisfies(conditions));
    }

    [Fact]
    public void Unsatisfied_ReturnsOnlyMissingKeys()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true);

        var conditions = new Dictionary<WorldKey, bool>
        {
            [WorldKey.TaskExists] = true,
            [WorldKey.PlanExists] = true,
            [WorldKey.BuildExists] = true,
        };

        var unsatisfied = state.Unsatisfied(conditions);

        Assert.Equal(2, unsatisfied.Count);
        Assert.Contains(WorldKey.PlanExists, unsatisfied);
        Assert.Contains(WorldKey.BuildExists, unsatisfied);
    }

    [Fact]
    public void Apply_AppliesActionEffects()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true);

        var action = new GoapAction(
            "TestAction",
            new Dictionary<WorldKey, bool> { [WorldKey.TaskExists] = true },
            new Dictionary<WorldKey, bool> { [WorldKey.PlanExists] = true },
            cost: 1);

        var result = state.Apply(action);

        Assert.True(result.Get(WorldKey.TaskExists));
        Assert.True(result.Get(WorldKey.PlanExists));
    }

    [Fact]
    public void Apply_IsImmutable_OriginalUnchanged()
    {
        var state = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true);

        var action = new GoapAction(
            "TestAction",
            new Dictionary<WorldKey, bool>(),
            new Dictionary<WorldKey, bool> { [WorldKey.PlanExists] = true },
            cost: 1);

        _ = state.Apply(action);

        Assert.False(state.Get(WorldKey.PlanExists));
    }

    [Fact]
    public void Equals_SameState_ReturnsTrue()
    {
        var a = (WorldState)new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, true);

        var b = (WorldState)new WorldState()
            .With(WorldKey.PlanExists, true)
            .With(WorldKey.TaskExists, true);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentState_ReturnsFalse()
    {
        var a = (WorldState)new WorldState().With(WorldKey.TaskExists, true);
        var b = (WorldState)new WorldState().With(WorldKey.PlanExists, true);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GetAll_ReturnsAllSetKeys()
    {
        var state = new WorldState()
            .With(WorldKey.TaskExists, true)
            .With(WorldKey.PlanExists, false);

        var all = state.GetAll();

        Assert.Equal(2, all.Count);
    }
}
