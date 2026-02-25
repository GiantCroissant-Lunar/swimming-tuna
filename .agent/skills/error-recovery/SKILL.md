---
name: error-recovery
description: Implement proper error handling, recovery patterns, and retry logic with backoff
tags: [error, exception, recovery, retry, fault, resilience]
roles: [builder, debugger]
scope: project
---

# Error Recovery

Implement robust error handling with proper recovery patterns and retry logic.

## Error Response Format

Use `ErrorEnvelope` not `ProblemDetails` for HTTP error responses.

```csharp
public sealed record ErrorEnvelope
{
    public required string Message { get; init; }
    public required string Code { get; init; }
    public Dictionary<string, object>? Details { get; init; }
}
```

## Retry with Backoff

Implement retry logic with exponential backoff for transient failures:

- Network timeouts
- 429 Too Many Requests
- 503 Service Unavailable
- Temporary database connection failures

Use appropriate max retry counts and backoff intervals based on the operation criticality.

## Error Context

Log error context before rethrowing:

```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to process request {RequestId}", requestId);
    throw;
}
```

Include relevant context: request IDs, user IDs, operation parameters.

## Never Swallow Exceptions

Never catch exceptions silently without logging or rethrowing:

```csharp
// ❌ BAD
catch (Exception) { }

// ✅ GOOD
catch (Exception ex)
{
    _logger.LogWarning(ex, "Non-critical operation failed, continuing");
}
```

## Fault Tolerance

Design for resilience:
- Use circuit breakers for external service calls
- Implement timeout policies
- Provide fallback behavior when appropriate
- Fail fast when recovery is impossible
