# Trace Correlation in Replay Events

Each `TaskExecutionEvent` row stored in ArcadeDB carries optional `traceId` and
`spanId` fields.  These are W3C TraceContext identifiers that link the persisted
event to the distributed trace that was active when the role step executed.

## How identifiers are populated

When an actor emits a `TaskExecutionEvent` it should capture the current
OpenTelemetry `Activity`:

```csharp
var activity = Activity.Current;
var evt = new TaskExecutionEvent(
    EventId:       Guid.NewGuid().ToString(),
    RunId:         runId,
    TaskId:        taskId,
    EventType:     "role.execution.completed",
    Payload:       payload,
    OccurredAt:    DateTimeOffset.UtcNow,
    TaskSequence:  0,
    RunSequence:   0,
    TraceId:       activity?.TraceId.ToString(),
    SpanId:        activity?.SpanId.ToString());

await _eventWriter.AppendAsync(evt, cancellationToken);
```

The `RuntimeTelemetry.StartActivity` helper creates a span with `swarm.task.id`,
`swarm.run.id`, and `swarm.role` tags so every role step already has a meaningful
span ready to correlate against.

## Replay API response

Both replay endpoints surface the correlation fields:

| Endpoint | Description |
|----------|-------------|
| `GET /memory/tasks/{taskId}/events` | Events ordered by `taskSequence` |
| `GET /runs/{runId}/events`          | Events ordered by `runSequence`  |

Each item in the `items` array includes:

```json
{
  "eventId":      "...",
  "runId":        "...",
  "taskId":       "...",
  "eventType":    "role.execution.completed",
  "payload":      "...",
  "occurredAt":   "2025-06-02T09:30:00+00:00",
  "taskSequence": 3,
  "runSequence":  7,
  "traceId":      "4bf92f3577b34da6a3ce929d0e0e4736",
  "spanId":       "00f067aa0ba902b7"
}
```

`traceId` and `spanId` are `null` for events that were recorded before this
feature was introduced, or when no active span was present at write time.

## Joining replay logs with Langfuse traces

1. Fetch events for a task:

   ```
   GET /memory/tasks/{taskId}/events
   ```

2. Take the `traceId` from any event that has one.

3. Open Langfuse and navigate to **Traces** → search by trace ID.  The trace
   will show all spans for that role execution including prompt/completion
   tokens, latency, and model metadata.

4. To correlate in reverse (from a Langfuse trace to replay events), copy the
   trace ID from the Langfuse UI and query ArcadeDB:

   ```sql
   SELECT * FROM TaskExecutionEvent WHERE traceId = '<trace-id>'
   ORDER BY runSequence ASC
   ```

## ArcadeDB schema

The `TaskExecutionEvent` document type includes:

| Property      | Type   | Description                              |
|---------------|--------|------------------------------------------|
| `traceId`     | STRING | W3C trace ID (32 hex chars) or `null`    |
| `spanId`      | STRING | W3C span ID (16 hex chars) or `null`     |

Both columns are nullable so that existing event rows without trace data remain
readable via the replay API without migration.
