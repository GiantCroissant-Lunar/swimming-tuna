# Replay API – Performance and Reliability

> Phase 8 / Issue 20  
> Track D – Telemetry + Ops

---

## Performance Targets

| Operation | Data Volume | Target |
|-----------|-------------|--------|
| `ListByTaskAsync` – single page | 1,000 events | < 2 s (parsing + transport) |
| `ListByRunAsync` – single page | 1,000 events | < 2 s (parsing + transport) |
| Full multi-page replay (cursor walk) | 1,000 events / 5 pages | < 10 s end-to-end |
| `AppendAsync` – in-memory sequence allocation | any | < 1 ms per event (after seeding) |

The transport budget (ArcadeDB query execution) depends on the host. The parsing budget alone
should complete in well under 100 ms for 1,000 events, as verified by
`ListByTaskAsync_LargeResponseParsing_CompletesWithinPerformanceTarget`.

---

## Pagination Design

Both `ListByTaskAsync` and `ListByRunAsync` use integer cursor pagination:

```
GET /memory/tasks/{taskId}/events?cursor=0&limit=200
→ { items: [...200 events...], nextCursor: 200 }

GET /memory/tasks/{taskId}/events?cursor=200&limit=200
→ { items: [...200 events...], nextCursor: 400 }

GET /memory/tasks/{taskId}/events?cursor=400&limit=200
→ { items: [...50 events...], nextCursor: null }   ← last page
```

Cursor value is the `taskSequence` (or `runSequence`) of the **last event on the current page**.
The next request passes `afterSequence=<cursor>`, and the SQL predicate
`taskSequence > :afterSequence` ensures no event is repeated or skipped.

### Limit Clamping

The `limit` parameter is clamped to `[1, 1000]` before being forwarded to ArcadeDB.
Callers that pass `0` or negative values receive exactly 1 event. Callers that pass
values above 1,000 receive at most 1,000 events.

---

## Verified Scenarios (automated tests)

| Test | What it validates |
|------|-------------------|
| `ListByTaskAsync_MultiPagePagination_ReturnsAllEventsInOrder` | 1,000 events returned correctly across 5 pages; sequences are contiguous and ascending |
| `ListByRunAsync_MultiPagePagination_ReturnsAllEventsInOrder` | 500 events across 5 pages via run cursor |
| `ListByTaskAsync_CursorResume_DoesNotReturnAlreadySeenEvents` | No duplicate events between consecutive pages |
| `ListByTaskAsync_LimitAboveMax_IsClamped` | `int.MaxValue` is clamped to ≤ 1,000 in the outgoing query |
| `ListByRunAsync_LimitAboveMax_IsClamped` | Same for run-scoped queries |
| `AppendAsync_ConcurrentMultipleTaskIds_NoSequenceCollisions` | 5 tasks × 20 concurrent appends → unique sequences per task |
| `ListByTaskAsync_LargeResponseParsing_CompletesWithinPerformanceTarget` | Parsing 1,000 events finishes in < 2 s |

---

## Failure Modes and Mitigations

### 1. ArcadeDB Unavailable / HTTP Error

**Symptom**: `AppendAsync`, `ListByTaskAsync`, or `ListByRunAsync` receives a non-2xx HTTP
response.

**Behaviour**: The repository catches the exception, increments a consecutive-failure counter,
and returns an empty list (reads) or silently no-ops (writes). This prevents the swarm from
crashing on persistence failure.

**Mitigation**:
- Deploy ArcadeDB with a health-check endpoint (`/api/v1/ready`) and configure a readiness probe.
- Use `Runtime__ArcadeDbEnabled=false` to disable persistence entirely when ArcadeDB is
  unavailable; the in-memory `TaskRegistry` continues to serve recent data.
- Monitor `consecutiveFailures` counter via structured log warnings:
  `ArcadeDB event {operation} failed ... consecutiveFailures={n}`.

### 2. Sequence Seeding Latency Under Cold Start

**Symptom**: The first `AppendAsync` per task/run triggers a `SELECT max(sequence)` query to
seed the in-process counter. Under high concurrency at cold start this query is serialised
through a single `SemaphoreSlim(1,1)`.

**Behaviour**: Contention is bounded because the semaphore is held only during the one-time
seeding query. Subsequent appends to the same key use a lock-free `AddOrUpdate`.

**Mitigation**:
- Pre-warm the sequence cache by calling `AppendAsync` with a synthetic event at startup, or
  by issuing a single `SELECT max(taskSequence)` query in `StartupMemoryBootstrapper`.
- If the seed query fails (ArcadeDB unreachable), `AppendAsync` aborts for that event and logs
  a warning. No sequence is allocated until ArcadeDB is reachable again.

### 3. Sequence Cache Eviction (> 10,000 distinct keys)

**Symptom**: When more than `MaxInMemorySequenceEntries` (10,000) distinct `taskId` or `runId`
keys accumulate, the oldest entries are trimmed in batches of 1,000. A subsequent append to an
evicted key re-seeds from the persisted max, which requires one extra ArcadeDB round-trip.

**Behaviour**: Sequences remain correct (seeded from persisted max). Throughput degrades
slightly for re-seeded keys.

**Mitigation**: This threshold handles ~10,000 concurrent unique task IDs. Long-running
deployments with high task throughput should monitor warning logs for repeated
`ArcadeDB event ... failed` messages and tune `MaxInMemorySequenceEntries` if needed.

### 4. Large Response Payload / Memory Pressure

**Symptom**: A `limit=1000` query over events with large JSON payloads may allocate significant
memory during `JsonDocument.Parse` and event-list construction.

**Behaviour**: All events in a page are materialised in memory before being returned. The
default page size of 200 and the hard cap of 1,000 bound the worst-case allocation.

**Mitigation**:
- Keep `limit` at the default (200) unless the caller needs a full-page walk.
- For bulk replay (e.g. analytics pipelines), paginate with a cursor rather than increasing
  the page size.
- If memory pressure is observed, lower the effective cap in `RuntimeOptions` via a
  configuration override.

### 5. Stale `nextCursor` After Late-Arriving Events

**Symptom**: ArcadeDB sequence numbers are allocated in-process. A restarted node seeds from
the persisted max and continues from there. If two nodes run concurrently (not the intended
topology), they could issue overlapping sequences.

**Behaviour**: The cursor predicate (`taskSequence > :afterSequence`) skips already-seen events
even when sequences gap. Late-arriving events with lower sequences would not appear in a
resumed pagination walk.

**Mitigation**: Run a single `SwarmAssistant.Runtime` node per ArcadeDB database. The current
architecture (single-process Akka.NET) enforces this by design.
