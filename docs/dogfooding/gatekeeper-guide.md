# Gatekeeper Guide

The gatekeeper is the human or Claude Code session that reviews swarm-produced code
before it is committed to a feature branch. This guide captures patterns learned from
dogfooding cycles.

## Role

The swarm (planner → builder → reviewer pipeline) produces implementation changes on
`swarm/task-*` branches. The gatekeeper:

1. Reviews the diff for correctness, consistency, and security.
2. Applies targeted fixes (not rewrites — the swarm should do the heavy lifting).
3. Commits with a clear audit trail of what the swarm produced vs. what was fixed.
4. Merges the swarm branch into the feature branch.

## Review Checklist

### Spec-vs-Code Consistency

The most common class of swarm error is **drift between documentation and implementation**.
The builder writes working code, but the OpenAPI spec, JSON schema, or DTO doesn't
accurately describe it.

| Check | How |
|-------|-----|
| Field nullability | Compare DTO property types (nullable `?` or not) against JSON schema `type` arrays and OpenAPI `type` fields. |
| Required arrays | If a DTO field is non-nullable, it should be in the `required` array in both JSON schema and OpenAPI. |
| Enum/param values | If query params accept specific values (e.g. `prefer=cheapest`), verify the OpenAPI description lists the same values the code accepts. |
| Status/state semantics | Ensure status fields derive meaningful values, not duplicate raw enum fields. |

### Generated Models

After any OpenAPI or JSON schema change:

```bash
task models:verify    # check if stale
task models:generate  # regenerate if needed
```

The swarm does not currently run these commands. The gatekeeper must.

### Test Verification

```bash
dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet
```

All tests must pass before committing. If the swarm added tests, verify they test
meaningful behavior and not just happy-path serialization.

### Commit Convention

Use a commit message that credits the swarm and lists gatekeeper fixes:

```text
feat(<scope>): <what was built>

Swarm-produced (Copilot CLI via planner→builder→reviewer pipeline):
- <bullet list of swarm contributions>

Gatekeeper fixes (Claude Code review):
- <bullet list of corrections>
```

## Common Swarm Failure Patterns

| Pattern | Frequency | Mitigation |
|---------|-----------|------------|
| Schema nullability mismatch | High | Reviewer prompt now includes cross-validation checklist |
| Spec description doesn't match code values | Medium | Reviewer prompt updated |
| Generated models not regenerated | High | Gatekeeper always runs `task models:verify` |
| Status field duplicates another field | Low | Reviewer prompt checks semantic meaning |
| Missing `required` entries in schema | Medium | Check non-nullable DTO properties |

## Task Granularity Guidelines

Tasks submitted to the swarm work best when:

- **One concern per task** — "add HTTP endpoint" and "write OpenAPI spec for endpoint"
  are better as two tasks than one.
- **Implementation before spec** — submit the code task first, then a spec task that
  references the implementation. This prevents drift.
- **Tests bundled with implementation** — "implement X with tests" works better than
  separate implementation and test tasks.

## Workflow

```text
1. Submit tasks to swarm via POST /a2a/tasks
2. Monitor until all tasks reach terminal state
3. Checkout the swarm/task-* branch
4. Run review checklist above
5. Apply minimal gatekeeper fixes
6. Commit (swarm + gatekeeper attribution)
7. Merge into feat/* branch
8. Verify: tests pass, models verify, pre-commit hooks pass
9. Push / create PR
```
