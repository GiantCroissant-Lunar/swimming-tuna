using SwarmAssistant.Contracts.Planning;

namespace SwarmAssistant.Runtime.Planning;

public sealed class GoapPlanner : IGoapPlanner
{
    private readonly IReadOnlyList<IGoapAction> _actions;

    public GoapPlanner(IReadOnlyList<IGoapAction> actions)
    {
        _actions = actions;
    }

    public GoapPlanResult Plan(IWorldState current, IGoal goal)
    {
        var currentState = ToWorldState(current);

        if (currentState.Satisfies(goal.TargetState))
        {
            return new GoapPlanResult(
                RecommendedPlan: Array.Empty<IGoapAction>(),
                AlternativePlan: null,
                DeadEnd: false);
        }

        var plans = FindAllPlans(currentState, goal, limit: 2);

        return plans.Count switch
        {
            0 => new GoapPlanResult(null, null, DeadEnd: true),
            1 => new GoapPlanResult(plans[0], null, DeadEnd: false),
            _ => new GoapPlanResult(plans[0], plans[1], DeadEnd: false),
        };
    }

    private List<IReadOnlyList<IGoapAction>> FindAllPlans(WorldState start, IGoal goal, int limit)
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

            foreach (var action in _actions)
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
