namespace SwarmAssistant.Runtime.Tasks;

/// <summary>
/// Deterministic synthetic run-ID strategy for task records that predate run-ID support.
/// </summary>
/// <remarks>
/// Legacy snapshots and events stored before <c>runId</c> was introduced have a null or
/// empty run-ID field in ArcadeDB. Rather than surfacing a null to callers, the reader
/// layer synthesises a stable, deterministic run-ID from the task's own <c>taskId</c>.
///
/// The synthetic ID uses the <see cref="Prefix"/> <c>legacy-</c> followed by the
/// <c>taskId</c>, e.g. <c>legacy-task-abc123</c>. Because the derivation is pure and
/// deterministic, repeated reads of the same record always produce the same synthetic ID,
/// which satisfies the API contract for run grouping without requiring a database migration.
///
/// Migration note: To upgrade legacy records to explicit run IDs, execute the following
/// ArcadeDB SQL against the <c>swarm_assistant</c> database once permanent run IDs have
/// been assigned externally:
/// <code>
///   -- backfill SwarmTask records that have no runId
///   UPDATE SwarmTask SET runId = CONCAT('legacy-', taskId) WHERE runId IS NULL;
///
///   -- backfill TaskExecutionEvent records that have no runId
///   UPDATE TaskExecutionEvent SET runId = CONCAT('legacy-', taskId) WHERE runId = '' OR runId IS NULL;
/// </code>
/// After running the migration, the synthetic-ID logic in the readers remains a safe
/// no-op because the persisted values are now non-empty and will be returned as-is.
/// </remarks>
internal static class LegacyRunId
{
    /// <summary>Prefix that marks all synthetically generated run IDs.</summary>
    public const string Prefix = "legacy-";

    /// <summary>
    /// Returns <paramref name="runId"/> when it is non-empty; otherwise synthesises a
    /// deterministic run ID from <paramref name="taskId"/> using the <see cref="Prefix"/>
    /// convention.
    /// </summary>
    /// <param name="runId">The persisted run ID, which may be null or empty.</param>
    /// <param name="taskId">The owning task ID used to build the synthetic value.</param>
    /// <returns>A non-null, non-empty run ID safe to expose through the API.</returns>
    public static string Resolve(string? runId, string taskId)
    {
        return string.IsNullOrWhiteSpace(runId)
            ? Prefix + taskId
            : runId;
    }
}
