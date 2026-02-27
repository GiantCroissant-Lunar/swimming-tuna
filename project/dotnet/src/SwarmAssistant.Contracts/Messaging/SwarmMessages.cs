using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Contracts.Messaging;

public enum SwarmRole
{
    Planner = 0,
    Builder = 1,
    Reviewer = 2,
    Orchestrator = 3,
    Researcher = 4,
    Debugger = 5,
    Tester = 6,
    Decomposer = 7
}

public sealed record TaskAssigned(
    string TaskId,
    string Title,
    string Description,
    DateTimeOffset AssignedAt
);

public sealed record TaskStarted(
    string TaskId,
    TaskState Status,
    DateTimeOffset StartedAt,
    string ActorName
);

public sealed record TaskResult(
    string TaskId,
    TaskState Status,
    string Output,
    DateTimeOffset CompletedAt,
    string ActorName
);

public sealed record TaskFailed(
    string TaskId,
    TaskState Status,
    string Error,
    DateTimeOffset FailedAt,
    string ActorName
);

public sealed record EscalationRaised(
    string TaskId,
    string Reason,
    int Level,
    DateTimeOffset RaisedAt,
    string FromActor
);
