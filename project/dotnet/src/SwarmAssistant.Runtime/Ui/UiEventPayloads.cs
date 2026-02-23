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
