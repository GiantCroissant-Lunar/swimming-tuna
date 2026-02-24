using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SwarmAssistant.Runtime.Tasks;

/// <summary>
/// Centralised helper that shapes and appends <see cref="TaskExecutionEvent"/> records
/// to the append-only store. Keeps actor code free of event-shape logic.
/// All write methods are fire-and-forget safe: they swallow exceptions so callers
/// (Akka actors, which must stay synchronous) can use <c>_ = recorder.Record…()</c>.
/// </summary>
public sealed class RuntimeEventRecorder
{
    private readonly ITaskExecutionEventWriter? _writer;
    private readonly ILogger<RuntimeEventRecorder> _logger;

    public RuntimeEventRecorder(ITaskExecutionEventWriter? writer, ILogger<RuntimeEventRecorder> logger)
    {
        _writer = writer;
        _logger = logger;
    }

    // ── event-type constants ──────────────────────────────────────────────────

    public const string TaskSubmitted = "task.submitted";
    public const string CoordinationStarted = "coordination.started";
    public const string RoleStarted = "role.started";
    public const string RoleCompleted = "role.completed";
    public const string RoleFailed = "role.failed";
    public const string TaskDone = "task.done";
    public const string TaskFailed = "task.failed";
    public const string DiagnosticContext = "diagnostic.context";

    private sealed record TaskSubmittedEventPayload(string Title);
    private sealed record RoleEventPayload(string Role);
    private sealed record RoleCompletedEventPayload(string Role, double Confidence);
    private sealed record RoleFailedEventPayload(string Role, string Error);
    private sealed record TaskFailedEventPayload(string Error);
    private sealed record DiagnosticContextPayload(
        string Action,
        string Role,
        int PromptLength,
        bool HasCodeContext,
        int CodeChunkCount,
        bool HasStrategyAdvice,
        IReadOnlyList<string> TargetFiles);

    // ── public record methods ─────────────────────────────────────────────────

    public Task RecordTaskSubmittedAsync(string taskId, string? runId, string title) =>
        AppendAsync(TaskSubmitted, taskId, runId,
            JsonSerializer.Serialize(new TaskSubmittedEventPayload(title)));

    public Task RecordCoordinationStartedAsync(string taskId, string? runId) =>
        AppendAsync(CoordinationStarted, taskId, runId, null);

    public Task RecordRoleStartedAsync(string taskId, string? runId, string role) =>
        AppendAsync(RoleStarted, taskId, runId,
            JsonSerializer.Serialize(new RoleEventPayload(role)));

    public Task RecordRoleCompletedAsync(string taskId, string? runId, string role, double confidence) =>
        AppendAsync(RoleCompleted, taskId, runId,
            JsonSerializer.Serialize(new RoleCompletedEventPayload(role, confidence)));

    public Task RecordRoleFailedAsync(string taskId, string? runId, string role, string error) =>
        AppendAsync(RoleFailed, taskId, runId,
            JsonSerializer.Serialize(new RoleFailedEventPayload(role, error)));

    public Task RecordTaskDoneAsync(string taskId, string? runId) =>
        AppendAsync(TaskDone, taskId, runId, null);

    public Task RecordTaskFailedAsync(string taskId, string? runId, string error) =>
        AppendAsync(TaskFailed, taskId, runId,
            JsonSerializer.Serialize(new TaskFailedEventPayload(error)));

    public Task RecordDiagnosticContextAsync(
        string taskId,
        string? runId,
        string action,
        string role,
        int promptLength,
        bool hasCodeContext,
        int codeChunkCount,
        bool hasStrategyAdvice,
        IReadOnlyList<string> targetFiles) =>
        AppendAsync(DiagnosticContext, taskId, runId,
            JsonSerializer.Serialize(
                new DiagnosticContextPayload(action, role, promptLength, hasCodeContext, codeChunkCount, hasStrategyAdvice, targetFiles)));

    // ── internal append helper ────────────────────────────────────────────────

    private Task AppendAsync(string eventType, string taskId, string? runId, string? payload)
    {
        if (_writer is null)
        {
            return Task.CompletedTask;
        }

        var resolvedRunId = LegacyRunId.Resolve(runId, taskId);
        var activity = Activity.Current;

        var evt = new TaskExecutionEvent(
            EventId: Guid.NewGuid().ToString("N"),
            RunId: resolvedRunId,
            TaskId: taskId,
            EventType: eventType,
            Payload: payload,
            OccurredAt: DateTimeOffset.UtcNow,
            TaskSequence: 0,   // assigned by the repository
            RunSequence: 0,    // assigned by the repository
            TraceId: activity?.TraceId.ToHexString(),
            SpanId: activity?.SpanId.ToHexString());

        return WriteAsync(evt);
    }

    private async Task WriteAsync(TaskExecutionEvent evt)
    {
        try
        {
            await _writer!.AppendAsync(evt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist execution event eventType={EventType} taskId={TaskId}",
                evt.EventType,
                evt.TaskId);
        }
    }
}
