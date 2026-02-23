using Akka.Actor;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Contracts.Planning;

namespace SwarmAssistant.Runtime.Actors;

internal sealed record ExecuteRoleTask(
    string TaskId,
    SwarmRole Role,
    string Title,
    string Description,
    string? PlanningOutput,
    string? BuildOutput,
    string? OrchestratorPrompt = null,
    string? PreferredAdapter = null,
    double? MaxConfidence = null
);

internal sealed record RoleTaskSucceeded(
    string TaskId,
    SwarmRole Role,
    string Output,
    DateTimeOffset CompletedAt,
    double Confidence = 1.0
);

internal sealed record RoleTaskFailed(
    string TaskId,
    SwarmRole Role,
    string Error,
    DateTimeOffset FailedAt
);

internal sealed record GetSupervisorSnapshot();

internal sealed record SupervisorSnapshot(
    int Started,
    int Completed,
    int Failed,
    int Escalations
);

// Orchestrator decision from CLI agent — reserved for future phases where the
// orchestrator publishes its structured decision to the event stream instead of
// returning plain text. Produced by TaskCoordinatorActor; consumed by telemetry/UI.
internal sealed record OrchestratorDecision(
    string TaskId,
    string ChosenAction,
    string Reasoning,
    DateTimeOffset DecidedAt
);

// Blackboard messages
internal sealed record UpdateBlackboard(
    string TaskId,
    string Key,
    string Value
);

internal sealed record RemoveBlackboard(
    string TaskId
);

internal sealed record GetBlackboardContext(
    string TaskId
);

internal sealed record BlackboardContext(
    string TaskId,
    IReadOnlyDictionary<string, string> Entries
);

// Global blackboard messages for stigmergy (cross-task coordination)
internal sealed record UpdateGlobalBlackboard(
    string Key,
    string Value
);

internal sealed record GetGlobalContext();

internal sealed record GlobalBlackboardContext(
    IReadOnlyDictionary<string, string> Entries
);

internal sealed record GlobalBlackboardChanged(
    string Key,
    string Value
);

// World state snapshot for telemetry/UI — reserved for future phases where world-state
// changes are published as discrete events (e.g., to a replay log or the AG-UI stream).
// Produced by TaskCoordinatorActor on each TransitionTo call.
internal sealed record WorldStateUpdated(
    string TaskId,
    WorldKey Key,
    bool Value
);

// Coordinator → Supervisor: detailed role failure report for active supervision
internal sealed record RoleFailureReport(
    string TaskId,
    SwarmRole FailedRole,
    string Error,
    int RetryCount,
    DateTimeOffset At
);

// Supervisor → Coordinator: retry a specific role (optionally skipping an adapter)
internal sealed record RetryRole(
    string TaskId,
    SwarmRole Role,
    string? SkipAdapter,
    string Reason
);

// Supervisor → broadcast via EventStream: adapter circuit breaker state
internal sealed record AdapterCircuitOpen(
    string AdapterId,
    DateTimeOffset Until
);

internal sealed record AdapterCircuitClosed(
    string AdapterId
);

// Monitor → Workers: health check ping/pong — reserved for future phases where the
// MonitorActor actively probes worker/reviewer actors for liveness rather than relying
// solely on supervisor snapshots. Request sent by MonitorActor; response from WorkerActor.
internal sealed record HealthCheckRequest(
    string RequestId,
    DateTimeOffset At
);

internal sealed record HealthCheckResponse(
    string RequestId,
    string ActorName,
    int ActiveTasks,
    DateTimeOffset At
);

internal sealed record GetBestAgentForRole(
    SwarmRole Role
);

internal sealed record BestAgentForRole(
    SwarmRole Role,
    IActorRef? Agent
);

internal sealed record GetCapabilitySnapshot;

internal sealed record CapabilitySnapshot(
    IReadOnlyList<AgentCapabilityAdvertisement> Agents
);

// Formal escalation record for supervisor tracking — reserved for future phases where
// the SupervisorActor persists or forwards a structured escalation record (e.g., to an
// external incident tracker). Produced by TaskCoordinatorActor on terminal failures.
internal sealed record TaskEscalated(
    string TaskId,
    string Reason,
    int Level,
    DateTimeOffset At
);

// Monitor self-scheduling tick
internal sealed record MonitorTick;

// Sub-task spawning messages (Phase 14)
internal sealed record SpawnSubTask(
    string ParentTaskId,
    string ChildTaskId,
    string Title,
    string Description,
    int Depth = 0
);

internal sealed record SubTaskCompleted(
    string ParentTaskId,
    string ChildTaskId,
    string Output
);

internal sealed record SubTaskFailed(
    string ParentTaskId,
    string ChildTaskId,
    string Error
);

// Quality concern raised by agent actors when output confidence is below threshold (Phase 15)
// Producer: WorkerActor, ReviewerActor; Consumer: SupervisorActor, TaskCoordinatorActor
internal sealed record QualityConcern(
    string TaskId,
    SwarmRole Role,
    string Concern,
    double Confidence,
    DateTimeOffset At,
    string? PreferredAdapter = null
);
