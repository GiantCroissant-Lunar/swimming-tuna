using SwarmAssistant.Contracts.Planning;

namespace SwarmAssistant.Runtime.Planning;

/// <summary>
/// Defines GOAP actions for swarm task execution with configurable costs.
/// Base costs can be adjusted dynamically based on historical performance data.
/// </summary>
public static class SwarmActions
{
    /// <summary>
    /// Base costs for each action. These can be overridden based on learning data.
    /// </summary>
    public static class BaseCosts
    {
        public const int Plan = 1;
        public const int Build = 3;
        public const int Review = 2;
        public const int Rework = 4;
        public const int Escalate = 10;
        public const int Finalize = 1;
        public const int WaitForSubTasks = 2;
    }

    public static readonly IGoapAction Plan = new GoapAction(
        name: "Plan",
        preconditions: new Dictionary<WorldKey, bool>
        {
            [WorldKey.TaskExists] = true,
        },
        effects: new Dictionary<WorldKey, bool>
        {
            [WorldKey.PlanExists] = true,
        },
        cost: BaseCosts.Plan);

    public static readonly IGoapAction Build = new GoapAction(
        name: "Build",
        preconditions: new Dictionary<WorldKey, bool>
        {
            [WorldKey.PlanExists] = true,
        },
        effects: new Dictionary<WorldKey, bool>
        {
            [WorldKey.BuildExists] = true,
        },
        cost: BaseCosts.Build);

    public static readonly IGoapAction Review = new GoapAction(
        name: "Review",
        preconditions: new Dictionary<WorldKey, bool>
        {
            [WorldKey.BuildExists] = true,
            [WorldKey.ReviewRejected] = false,
        },
        effects: new Dictionary<WorldKey, bool>
        {
            [WorldKey.ReviewPassed] = true,
        },
        cost: BaseCosts.Review);

    public static readonly IGoapAction Rework = new GoapAction(
        name: "Rework",
        preconditions: new Dictionary<WorldKey, bool>
        {
            [WorldKey.ReviewRejected] = true,
            [WorldKey.RetryLimitReached] = false,
        },
        effects: new Dictionary<WorldKey, bool>
        {
            [WorldKey.BuildExists] = true,
            [WorldKey.ReviewRejected] = false,
            [WorldKey.ReworkAttempted] = true,
        },
        cost: BaseCosts.Rework);

    public static readonly IGoapAction Escalate = new GoapAction(
        name: "Escalate",
        preconditions: new Dictionary<WorldKey, bool>
        {
            [WorldKey.ReviewRejected] = true,
            [WorldKey.RetryLimitReached] = true,
        },
        effects: new Dictionary<WorldKey, bool>
        {
            [WorldKey.TaskBlocked] = true,
        },
        cost: BaseCosts.Escalate);

    public static readonly IGoapAction Finalize = new GoapAction(
        name: "Finalize",
        preconditions: new Dictionary<WorldKey, bool>
        {
            [WorldKey.ReviewPassed] = true,
        },
        effects: new Dictionary<WorldKey, bool>
        {
            [WorldKey.TaskCompleted] = true,
        },
        cost: BaseCosts.Finalize);

    public static readonly IGoapAction WaitForSubTasks = new GoapAction(
        name: "WaitForSubTasks",
        preconditions: new Dictionary<WorldKey, bool>
        {
            [WorldKey.SubTasksSpawned] = true,
            [WorldKey.SubTasksCompleted] = false,
        },
        effects: new Dictionary<WorldKey, bool>
        {
            [WorldKey.SubTasksCompleted] = true,
        },
        cost: BaseCosts.WaitForSubTasks);

    public static readonly IGoapAction Negotiate = new GoapAction(
        name: "Negotiate",
        preconditions: new Dictionary<WorldKey, bool>
        {
            [WorldKey.TaskExists] = true,
            [WorldKey.AgentsAvailable] = true,
        },
        effects: new Dictionary<WorldKey, bool>
        {
            [WorldKey.NegotiationComplete] = true,
        },
        cost: 1);

    public static IReadOnlyList<IGoapAction> All { get; } = [Plan, Build, Review, Rework, Escalate, Finalize, WaitForSubTasks, Negotiate];

    /// <summary>
    /// Creates a new set of actions with adjusted costs based on learning data.
    /// </summary>
    /// <param name="costAdjustments">
    /// Dictionary mapping action names to cost multipliers.
    /// A value of 1.0 means no change, higher values make actions less preferred.
    /// </param>
    /// <returns>A new list of actions with adjusted costs.</returns>
    public static IReadOnlyList<IGoapAction> WithAdjustedCosts(
        IReadOnlyDictionary<string, double>? costAdjustments)
    {
        if (costAdjustments is null or { Count: 0 })
        {
            return All;
        }

        return
        [
            CreateWithAdjustedCost(Plan, BaseCosts.Plan, costAdjustments),
            CreateWithAdjustedCost(Build, BaseCosts.Build, costAdjustments),
            CreateWithAdjustedCost(Review, BaseCosts.Review, costAdjustments),
            CreateWithAdjustedCost(Rework, BaseCosts.Rework, costAdjustments),
            CreateWithAdjustedCost(Escalate, BaseCosts.Escalate, costAdjustments),
            CreateWithAdjustedCost(Finalize, BaseCosts.Finalize, costAdjustments),
            CreateWithAdjustedCost(WaitForSubTasks, BaseCosts.WaitForSubTasks, costAdjustments)
        ];
    }

    private static IGoapAction CreateWithAdjustedCost(
        IGoapAction original,
        int baseCost,
        IReadOnlyDictionary<string, double> costAdjustments)
    {
        if (!costAdjustments.TryGetValue(original.Name, out var multiplier))
        {
            return original;
        }

        var adjustedCost = Math.Max(1, (int)Math.Round(baseCost * multiplier));
        return new GoapAction(
            name: original.Name,
            preconditions: original.Preconditions,
            effects: original.Effects,
            cost: adjustedCost);
    }

    public static IGoal CompleteTask { get; } = new Goal(
        "CompleteTask",
        new Dictionary<WorldKey, bool>
        {
            [WorldKey.TaskCompleted] = true,
        });

    public static IGoal EscalateTask { get; } = new Goal(
        "EscalateTask",
        new Dictionary<WorldKey, bool>
        {
            [WorldKey.TaskBlocked] = true,
        });
}
