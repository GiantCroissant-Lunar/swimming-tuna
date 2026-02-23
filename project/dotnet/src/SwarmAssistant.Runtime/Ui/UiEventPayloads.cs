namespace SwarmAssistant.Runtime.Ui;

// ── Graph lifecycle payloads (emitted by DispatcherActor) ──────────────────

/// <summary>Payload for <c>agui.task.submitted</c>.</summary>
public sealed record TaskSubmittedPayload(string TaskId, string Title, string Description);

/// <summary>Payload for <c>agui.graph.link_created</c>.</summary>
public sealed record GraphLinkCreatedPayload(string ParentTaskId, string ChildTaskId, int Depth, string Title);

/// <summary>Payload for <c>agui.graph.child_completed</c>.</summary>
public sealed record GraphChildCompletedPayload(string ParentTaskId, string ChildTaskId);

/// <summary>Payload for <c>agui.graph.child_failed</c>.</summary>
public sealed record GraphChildFailedPayload(string ParentTaskId, string ChildTaskId, string Error);

// ── Telemetry payloads (emitted by TaskCoordinatorActor) ──────────────────

/// <summary>
/// Payload for <c>agui.telemetry.quality</c>.
/// Emitted on role completion and on quality concern events.
/// <paramref name="Concern"/> is only present when emitted from a quality concern.
/// </summary>
public sealed record TelemetryQualityPayload(string Role, double Confidence, int RetryCount, string? Concern = null);

/// <summary>Payload for <c>agui.telemetry.retry</c>.</summary>
public sealed record TelemetryRetryPayload(string Role, int RetryCount, string Reason);

/// <summary>Payload for <c>agui.telemetry.consensus</c>.</summary>
public sealed record TelemetryConsensusPayload(bool Approved, int VoteCount, int RetryCount);

/// <summary>Payload for <c>agui.telemetry.circuit</c>.</summary>
public sealed record TelemetryCircuitPayload(string AdapterCircuitKey, string State, bool HasOpenCircuits);

// ── Role lifecycle payloads (emitted by TaskCoordinatorActor) ─────────────

/// <summary>Payload for <c>agui.role.dispatched</c>. Emitted when coordinator sends a role task to a worker/reviewer.</summary>
public sealed record RoleDispatchedPayload(string Role, string TaskId);

/// <summary>Payload for <c>agui.role.started</c>. Emitted when a worker/reviewer begins executing the role.</summary>
public sealed record RoleStartedPayload(string Role, string TaskId, string? ActorName = null);

/// <summary>Payload for <c>agui.role.succeeded</c>. Emitted on role success (non-orchestrator roles only).</summary>
public sealed record RoleSucceededPayload(string Role, string TaskId, double Confidence, string? AdapterId = null);

/// <summary>Payload for <c>agui.role.failed</c>. Emitted on role failure (non-orchestrator roles only).</summary>
public sealed record RoleFailedPayload(string Role, string TaskId, string Error);

/// <summary>Payload for <c>agui.task.escalated</c>. Emitted when the task is escalated due to retries or dead-end.</summary>
public sealed record TaskEscalatedPayload(string TaskId, string Reason, int Level);

/// <summary>Payload for <c>agui.task.intervention</c>. Emitted when a human intervention action is accepted.</summary>
public sealed record TaskInterventionPayload(string TaskId, string ActionId, string DecidedBy = "human");
