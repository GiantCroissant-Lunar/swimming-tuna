using SwarmAssistant.Contracts.Planning;

namespace SwarmAssistant.Runtime.Planning;

/// <summary>
/// GOAP planner that supports dynamic cost adjustment based on learning data.
/// </summary>
public sealed class GoapPlanner : IGoapPlanner
{
    private readonly IReadOnlyList<IGoapAction> _actions;

    public GoapPlanner(IReadOnlyList<IGoapAction> actions)
    {
        _actions = actions;
    }

    /// <summary>
    /// Plans a sequence of actions to achieve the goal from the current world state.
    /// </summary>
    /// <param name="current">The current world state.</param>
    /// <param name="goal">The goal to achieve.</param>
    /// <returns>The planning result with recommended and alternative plans.</returns>
    public GoapPlanResult Plan(IWorldState current, IGoal goal)
    {
        return Plan(current, goal, costAdjustments: null);
    }

    /// <summary>
    /// Plans a sequence of actions to achieve the goal with dynamically adjusted costs.
    /// </summary>
    /// <param name="current">The current world state.</param>
    /// <param name="goal">The goal to achieve.</param>
    /// <param name="costAdjustments">
    /// Optional dictionary mapping action names to cost multipliers.
    /// Higher values make actions less preferred. A value of 1.0 means no change.
    /// </param>
    /// <returns>The planning result with recommended and alternative plans.</returns>
    public GoapPlanResult Plan(
        IWorldState current,
        IGoal goal,
        IReadOnlyDictionary<string, double>? costAdjustments)
    {
        var currentState = ToWorldState(current);

        if (currentState.Satisfies(goal.TargetState))
        {
            return new GoapPlanResult(
                RecommendedPlan: Array.Empty<IGoapAction>(),
                AlternativePlan: null,
                DeadEnd: false);
        }

        // Use adjusted actions if cost overrides are provided
        var actions = costAdjustments is { Count: > 0 }
            ? GetAdjustedActions(costAdjustments)
            : _actions;

        var plans = FindAllPlans(currentState, goal, actions, limit: 2);

        return plans.Count switch
        {
            0 => new GoapPlanResult(null, null, DeadEnd: true),
            1 => new GoapPlanResult(plans[0], null, DeadEnd: false),
            _ => new GoapPlanResult(plans[0], plans[1], DeadEnd: false),
        };
    }

    /// <summary>
    /// Creates a set of actions with adjusted costs based on learning data.
    /// </summary>
    private IReadOnlyList<IGoapAction> GetAdjustedActions(
        IReadOnlyDictionary<string, double> costAdjustments)
    {
        var adjusted = new List<IGoapAction>();

        foreach (var action in _actions)
        {
            if (costAdjustments.TryGetValue(action.Name, out var multiplier))
            {
                var adjustedCost = Math.Max(1, (int)Math.Round(action.Cost * multiplier));
                adjusted.Add(new GoapAction(
                    name: action.Name,
                    preconditions: action.Preconditions,
                    effects: action.Effects,
                    cost: adjustedCost));
            }
            else
            {
                adjusted.Add(action);
            }
        }

        return adjusted;
    }

    private List<IReadOnlyList<IGoapAction>> FindAllPlans(
        WorldState start,
        IGoal goal,
        IReadOnlyList<IGoapAction> actions,
        int limit)
    {
        var results = new List<IReadOnlyList<IGoapAction>>();
        var open = new PriorityQueue<SearchNode, int>();
        var visited = new HashSet<WorldState>();

        open.Enqueue(new SearchNode(start, [], 0), Heuristic(start, goal));

        while (open.Count > 0 && results.Count < limit)
        {
            var node = open.Dequeue();

            if (node.State.Satisfies(goal.TargetState))
            {
                results.Add(node.Actions);

                // For the alternative plan, skip nodes that start with the same action
                continue;
            }

            if (!visited.Add(node.State))
            {
                continue;
            }

            foreach (var action in actions)
            {
                if (!node.State.Satisfies(action.Preconditions))
                {
                    continue;
                }

                var nextState = node.State.Apply(action);

                if (visited.Contains(nextState))
                {
                    continue;
                }

                var nextActions = new List<IGoapAction>(node.Actions) { action };
                var nextCost = node.Cost + action.Cost;
                var priority = nextCost + Heuristic(nextState, goal);

                open.Enqueue(new SearchNode(nextState, nextActions, nextCost), priority);
            }
        }

        return results;
    }

    private static int Heuristic(WorldState state, IGoal goal)
    {
        return state.Unsatisfied(goal.TargetState).Count;
    }

    private static WorldState ToWorldState(IWorldState state)
    {
        if (state is WorldState ws)
        {
            return ws;
        }

        IWorldState current = new WorldState();
        foreach (var (key, value) in state.GetAll())
        {
            current = current.With(key, value);
        }

        return (WorldState)current;
    }

    private sealed record SearchNode(
        WorldState State,
        IReadOnlyList<IGoapAction> Actions,
        int Cost);
}
