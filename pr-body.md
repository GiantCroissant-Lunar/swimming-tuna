## Summary

Remove `IDisposable` interface and `Dispose()` methods from `SwarmAgentActor` and `TaskCoordinatorActor`. Akka.NET actors should use `PostStop()` for resource cleanup, not `IDisposable` — both actors already had proper `PostStop()` implementations handling all disposable fields.

## Changes

- **SwarmAgentActor**: Removed `IDisposable` and `Dispose()`. `PostStop()` already cancels `_heartbeatSchedule` and stops `_endpointHost`. Added `[SuppressMessage]` for CA1001 since the actor owns disposable fields but cleans them via `PostStop()`.
- **TaskCoordinatorActor**: Removed `IDisposable` and `Dispose()`. `PostStop()` already cancels/disposes `_verifyCts`. Added `[SuppressMessage]` for CA1001 with the same justification.

## Why

Akka actors have a managed lifecycle (`PreStart` → `PostStop`). Implementing `IDisposable` is misleading because:
1. Actors are not disposed by callers — the actor system manages their lifecycle
2. `Dispose()` would never be called in normal operation
3. `PostStop()` is the correct Akka pattern for cleanup

## Verification

- `dotnet build` passes with no errors
- No CA1001 warnings for either actor (suppressed with justification)
