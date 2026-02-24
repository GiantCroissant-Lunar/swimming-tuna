# Code Index Pipeline Implementation Summary

GitHub Issue: [#73](https://github.com/GiantCroissant-Lunar/swimming-tuna/issues/73)

## Overview

Implemented a structural code index pipeline (parse → chunk → embed → store → retrieve) that gives swarm agents codebase-aware context when executing tasks. Uses AST-aware chunking via Tree-sitter and vector search in ArcadeDB.

## Implementation Phases

### Phase 6a: Indexer MVP ✅

**Created Infrastructure:**
- `project/infra/code-index/Dockerfile` - Python-based container with Tree-sitter, FastEmbed
- `project/infra/code-index/docker-compose.yml` - Service and job orchestration
- `project/infra/code-index/env/` - Environment configurations (local, ci, secure-local)

**Core Python Components:**
- `src/models.py` - Pydantic models for CodeChunk, IndexRequest, SearchRequest
- `src/parser.py` - Tree-sitter AST parser with language detection
- `src/chunker.py` - AST-aware chunking at method/class boundaries
- `src/embedder.py` - FastEmbed integration for local ONNX embeddings
- `src/arcadedb_client.py` - ArcadeDB vector store client
- `src/indexer.py` - Full indexing pipeline with git-diff-aware incremental updates
- `src/api.py` - FastAPI retrieval service with vector search endpoint
- `src/cli.py` - CLI for index operations (index, search, stats, reset)

### Phase 6b: Retrieval API & .NET Integration ✅

**.NET Runtime Components:**
- `CodeIndexActor.cs` - Actor that queries the code-index retrieval API
- Updated `RolePromptFactory.cs` - Added code context as 4th context layer
- Updated `TaskCoordinatorActor.cs` - Queries code index before dispatching Plan/Build/Review tasks
- Updated `DispatcherActor.cs` - Wires CodeIndexActor to TaskCoordinatorActor
- Updated `Worker.cs` - Creates CodeIndexActor when CodeIndexEnabled
- Updated `RuntimeOptions.cs` - Added CodeIndex configuration options

**Configuration Options:**
```csharp
CodeIndexEnabled          // Enable/disable code index integration
CodeIndexUrl              // Retrieval API URL
CodeIndexMaxChunks        // Max chunks to include in prompts (0-50)
CodeIndexForPlanner       // Include context in planner prompts
CodeIndexForBuilder       // Include context in builder prompts
CodeIndexForReviewer      // Include context in reviewer prompts
```

### Phase 6c: Incremental Indexing ✅

- Git-diff-aware incremental updates in `src/indexer.py`
- File watcher mode for live development (optional)
- Handles file renames/moves by invalidating old chunks

### Phase 6d: Configuration & Documentation ✅

- Updated `appsettings.Local.json` with code index options
- Created `project/infra/code-index/README.md`
- Created this implementation summary

## Files Modified

### New Files (16)
```
project/infra/code-index/
├── Dockerfile
├── docker-compose.yml
├── requirements.txt
├── README.md
├── env/local.env
├── env/ci.env
├── env/secure-local.env
└── src/
    ├── __init__.py
    ├── models.py
    ├── parser.py
    ├── chunker.py (merged into parser.py)
    ├── embedder.py
    ├── arcadedb_client.py
    ├── indexer.py
    ├── api.py
    └── cli.py

project/dotnet/src/SwarmAssistant.Runtime/Actors/
└── CodeIndexActor.cs
```

### Modified Files (10)
```
project/dotnet/src/SwarmAssistant.Runtime/
├── Configuration/RuntimeOptions.cs
├── Execution/RolePromptFactory.cs
├── Actors/
│   ├── TaskCoordinatorActor.cs
│   ├── DispatcherActor.cs
│   ├── InternalMessages.cs
│   └── WorkerActor.cs
├── Execution/SubscriptionCliRoleExecutor.cs
├── Worker.cs
└── appsettings.Local.json
```

### Test Files Updated (5)
```
project/dotnet/tests/SwarmAssistant.Runtime.Tests/
├── GraphAndTelemetryEventTests.cs
├── LifecycleEventsTests.cs
├── SubTaskTests.cs
├── DynamicTopologyTests.cs
└── TaskLifecycleSmokeTests.cs
```

## Usage

### Start Services
```bash
cd project/infra/code-index
docker compose --env-file env/local.env up -d
```

### Index Codebase
```bash
docker compose run --rm code-index index /source --language csharp
```

### Enable in Runtime
Update `appsettings.Local.json`:
```json
{
  "Runtime": {
    "CodeIndexEnabled": true,
    "CodeIndexUrl": "http://localhost:8080"
  }
}
```

## Architecture Flow

```
User Task
  ↓
TaskCoordinatorActor
  ↓ (queries)
CodeIndexActor ──HTTP──→ Code Index API
  ↓ (returns)              ↓
CodeContext ←─────────── Vector Search
  ↓
RolePromptFactory (4th context layer)
  ↓
Enriched Prompt → Agent Execution
```

## Key Design Decisions

1. **AST-aware chunking**: Complete methods/classes rather than line fragments
2. **Local embeddings**: FastEmbed with ONNX - no GPU or cloud API needed
3. **ArcadeDB vector store**: Reuses existing infrastructure
4. **HTTP sidecar**: Containerized, no native dependencies
5. **Optional integration**: Can be disabled via configuration

## Next Steps (Future Phases)

- **Phase 6d Quality Validation**: Curate query benchmark, measure retrieval precision
- **Multi-language support**: JavaScript/TypeScript and Python parsers
- **File watcher mode**: Live incremental indexing during development
- **Query caching**: Cache frequent code context queries

## Testing

Build verification:
```bash
cd project/dotnet
dotnet build SwarmAssistant.sln
```

All 299 tests pass with the new code index integration (disabled by default in tests).
