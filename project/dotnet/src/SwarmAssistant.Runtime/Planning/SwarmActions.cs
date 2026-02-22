using SwarmAssistant.Contracts.Planning;

namespace SwarmAssistant.Runtime.Planning;

public static class SwarmActions
{
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
        cost: 1);

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
        cost: 3);

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
        cost: 2);

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
        cost: 4);

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
        cost: 10);

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
        cost: 1);

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
        cost: 2);

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
