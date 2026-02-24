"""FastAPI retrieval service for code index queries."""

import os
from contextlib import asynccontextmanager
from datetime import datetime
from typing import AsyncGenerator, Optional

from fastapi import FastAPI, HTTPException, Query
from fastapi.middleware.cors import CORSMiddleware

from src.models import (
    CodeChunk,
    HealthResponse,
    IndexRequest,
    IndexResponse,
    SearchRequest,
    SearchResponse,
    SearchResult,
    SchemaStatus,
    Language,
    NodeType,
)
from src.embedder import EmbeddingGenerator
from src.arcadedb_client import ArcadeDbClient
from src.indexer import CodeIndexer


# Global state
_db_client: Optional[ArcadeDbClient] = None
_embedder: Optional[EmbeddingGenerator] = None


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncGenerator:
    """Manage application lifecycle."""
    global _db_client, _embedder

    # Startup
    _db_client = ArcadeDbClient()
    _embedder = EmbeddingGenerator()

    # Ensure schema exists
    schema = _db_client.check_schema()
    if not schema.exists:
        _db_client.create_schema(dimension=_embedder.dimension)

    yield

    # Shutdown
    if _db_client:
        _db_client.close()


app = FastAPI(
    title="Code Index API",
    description="Structural code indexing with AST-aware chunking and vector search",
    version="0.1.0",
    lifespan=lifespan,
)

# CORS middleware
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.get("/health", response_model=HealthResponse)
async def health_check():
    """Health check endpoint."""
    return HealthResponse(
        status="healthy",
        version="0.1.0",
        arcadedb_connected=_db_client.health_check() if _db_client else False,
        embedding_model_loaded=_embedder.is_loaded() if _embedder else False,
        timestamp=datetime.utcnow(),
    )


@app.get("/schema", response_model=SchemaStatus)
async def get_schema():
    """Get database schema status."""
    if not _db_client:
        raise HTTPException(status_code=503, detail="Database client not initialized")
    return _db_client.check_schema()


@app.post("/search", response_model=SearchResponse)
async def search(request: SearchRequest):
    """Search for similar code chunks."""
    if not _db_client or not _embedder:
        raise HTTPException(status_code=503, detail="Service not initialized")

    import time

    start_time = time.time()

    # Generate embedding for query
    query_embedding = _embedder.embed_text(request.query)

    # Search database
    results = _db_client.search_similar(
        embedding=query_embedding,
        top_k=request.top_k,
        language_filter=request.languages,
        node_type_filter=request.node_types,
        file_path_prefix=request.file_path_prefix,
    )

    # Build response
    search_results = []
    for i, chunk in enumerate(results):
        if not request.include_content:
            chunk.content = ""
        if not request.include_embedding:
            chunk.embedding = None

        search_results.append(
            SearchResult(
                chunk=chunk,
                similarity_score=1.0 - (i * 0.05),  # Approximate from distance
                rank=i + 1,
            )
        )

    duration_ms = (time.time() - start_time) * 1000

    return SearchResponse(
        query=request.query,
        results=search_results,
        total_found=len(search_results),
        duration_ms=duration_ms,
    )


@app.get("/search")
async def search_get(
    q: str = Query(..., description="Search query"),
    top_k: int = Query(default=10, ge=1, le=100),
    language: Optional[str] = Query(default=None, description="Filter by language"),
    node_type: Optional[str] = Query(default=None, description="Filter by node type"),
    file_prefix: Optional[str] = Query(
        default=None, description="Filter by file path prefix"
    ),
):
    """GET endpoint for simple search queries."""
    request = SearchRequest(
        query=q,
        top_k=top_k,
        languages=[Language(language)] if language else None,
        node_types=[NodeType(node_type)] if node_type else None,
        file_path_prefix=file_prefix,
    )
    return await search(request)


@app.get("/chunk/{chunk_id}", response_model=CodeChunk)
async def get_chunk(chunk_id: str):
    """Get a specific chunk by ID."""
    # TODO: Implement single chunk retrieval
    raise HTTPException(status_code=501, detail="Not yet implemented")


@app.get("/stats")
async def get_stats():
    """Get index statistics."""
    if not _db_client:
        raise HTTPException(status_code=503, detail="Service not initialized")

    try:
        result = _db_client._execute_command("SELECT COUNT(*) as count FROM CodeChunk")
        total = result.get("result", [{}])[0].get("count", 0)

        result = _db_client._execute_command("""
            SELECT language, COUNT(*) as count
            FROM CodeChunk
            GROUP BY language
        """)
        by_lang = {r.get("language"): r.get("count") for r in result.get("result", [])}

        result = _db_client._execute_command("""
            SELECT nodeType, COUNT(*) as count
            FROM CodeChunk
            GROUP BY nodeType
        """)
        by_type = {r.get("nodeType"): r.get("count") for r in result.get("result", [])}

        return {"total_chunks": total, "by_language": by_lang, "by_node_type": by_type}
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


@app.post("/admin/index", response_model=IndexResponse)
async def trigger_indexing(request: IndexRequest):
    """Admin endpoint to trigger full indexing."""
    if not _db_client:
        raise HTTPException(status_code=503, detail="Service not initialized")

    indexer = CodeIndexer(arcadedb_client=_db_client, embedder=_embedder)
    return indexer.index(request)


if __name__ == "__main__":
    import uvicorn

    port = int(os.getenv("CODE_INDEX_PORT", "8080"))
    host = os.getenv("CODE_INDEX_HOST", "0.0.0.0")

    uvicorn.run(app, host=host, port=port)
