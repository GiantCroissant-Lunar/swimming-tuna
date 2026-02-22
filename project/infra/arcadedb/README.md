# ArcadeDB Integration Notes (Phase 6 Scaffold)

Phase 6 adds runtime configuration contracts for ArcadeDB integration:

- `Runtime__ArcadeDbEnabled`
- `Runtime__ArcadeDbHttpUrl`
- `Runtime__ArcadeDbDatabase`

No persistence adapter is wired in this phase. Planned follow-up scope:

1. Connect coordinator task lifecycle events to ArcadeDB write model.
2. Store `Task`, `Artifact`, and `RoleExecution` vertices.
3. Add edges for `(:Task)-[:HAS_ARTIFACT]->(:Artifact)` and `(:Task)-[:EXECUTED_BY]->(:RoleExecution)`.
4. Add memory-query tool endpoints for graph lookup and vector retrieval.
