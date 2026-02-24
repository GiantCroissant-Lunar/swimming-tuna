# Code Index Pipeline

Structural code indexing with AST-aware chunking and vector search for SwarmAssistant.

## Overview

The Code Index Pipeline provides codebase-aware context to swarm agents by:
1. Parsing source files using Tree-sitter AST
2. Extracting structural chunks (classes, methods, interfaces)
3. Generating embeddings using FastEmbed (local ONNX, no GPU required)
4. Storing in ArcadeDB vector index
5. Serving via HTTP API for retrieval

## Architecture

```text
Source files
  → Tree-sitter parse (AST)
  → Structural chunking (class/method/function boundaries)
  → FastEmbed (ONNX embeddings)
  → ArcadeDB vector index
  → Retrieval API
```

## Quick Start

### 1. Start ArcadeDB and Code Index Services

```bash
cd project/infra/code-index
docker compose --env-file env/local.env up -d
```

### 2. Index Your Codebase

```bash
# Full index
docker compose run --rm code-index index /source --language csharp

# Incremental index (git-diff-aware)
docker compose run --rm code-index index /source --language csharp --incremental
```

### 3. Query the Index

```bash
# Search for relevant code
curl -X POST http://localhost:8080/search \
  -H "Content-Type: application/json" \
  -d '{"query": "authentication service", "top_k": 5}'
```

## Configuration

Environment variables (set in `env/local.env`):

| Variable | Default | Description |
|----------|---------|-------------|
| `CODE_INDEX_PORT` | 8080 | API server port |
| `ARCADEDB_URL` | http://arcadedb:2480 | ArcadeDB HTTP URL |
| `ARCADEDB_DATABASE` | swarm_assistant | Database name |
| `EMBEDDING_MODEL` | BAAI/bge-small-en-v1.5 | FastEmbed model |
| `EMBEDDING_DIMENSION` | 384 | Vector dimension |
| `CHUNK_MAX_TOKENS` | 512 | Max tokens per chunk |

## .NET Runtime Integration

Enable code index in `appsettings.Local.json`:

```json
{
  "Runtime": {
    "CodeIndexEnabled": true,
    "CodeIndexUrl": "http://localhost:8080",
    "CodeIndexMaxChunks": 10,
    "CodeIndexForPlanner": true,
    "CodeIndexForBuilder": true,
    "CodeIndexForReviewer": true
  }
}
```

When enabled, the TaskCoordinatorActor will query the code index before dispatching Planner, Builder, and Reviewer tasks, enriching their prompts with relevant code context.

## CLI Commands

```bash
# Index source code
docker compose run --rm code-index index /path/to/source --language csharp

# Search code index
docker compose run --rm code-index search "authentication logic"

# Show statistics
docker compose run --rm code-index stats

# Reset index (delete all chunks)
docker compose run --rm code-index reset --force

# Start retrieval API server
docker compose up code-index
```

## API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/health` | GET | Health check |
| `/search` | POST | Vector similarity search |
| `/schema` | GET | Database schema status |
| `/stats` | GET | Index statistics |

### Search Request Body

```json
{
  "query": "how does auth work?",
  "top_k": 10,
  "languages": ["csharp"],
  "node_types": ["method", "class"],
  "file_path_prefix": "src/Services"
}
```

## Development

### Build Docker Image

```bash
cd project/infra/code-index
docker build -t swarm-assistant/code-index:latest .
```

### Run Tests

```bash
cd project/infra/code-index
pip install -r requirements.txt
python -m pytest src/tests/
```

## Supported Languages

- C# (primary)
- JavaScript/TypeScript
- Python

## Chunk Types

- `class` / `class_declaration`
- `interface` / `interface_declaration`
- `method` / `method_definition`
- `property` / `property_declaration`
- `function` / `function_declaration`
- And more...

## Design Decisions

1. **AST-aware chunking**: Splits at structural boundaries (methods, classes) rather than arbitrary line counts
2. **Local embeddings**: FastEmbed with ONNX models - no GPU or cloud API required
3. **ArcadeDB vector store**: Reuses existing infrastructure, supports L2 vector similarity
4. **Containerized**: All components run in Docker for reproducibility
5. **Incremental indexing**: Git-diff-aware updates avoid full re-indexing
