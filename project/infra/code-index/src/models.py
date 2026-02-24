"""Pydantic models for code chunks and indexing operations."""

from datetime import datetime
from enum import Enum
from typing import List, Optional

from pydantic import BaseModel, Field


class NodeType(str, Enum):
    """AST node types for code chunks."""

    # C# specific
    CLASS = "class"
    INTERFACE = "interface"
    STRUCT = "struct"
    RECORD = "record"
    ENUM = "enum"
    METHOD = "method"
    CONSTRUCTOR = "constructor"
    PROPERTY = "property"
    FIELD = "field"
    EVENT = "event"
    DELEGATE = "delegate"
    NAMESPACE = "namespace"

    # JavaScript/TypeScript
    FUNCTION = "function"
    ARROW_FUNCTION = "arrow_function"
    CLASS_DECLARATION = "class_declaration"
    METHOD_DEFINITION = "method_definition"

    # Python
    FUNCTION_DEF = "function_def"
    CLASS_DEF = "class_def"
    ASYNC_FUNCTION_DEF = "async_function_def"

    # Generic
    BLOCK = "block"
    UNKNOWN = "unknown"


class Language(str, Enum):
    """Supported programming languages."""

    CSHARP = "csharp"
    JAVASCRIPT = "javascript"
    TYPESCRIPT = "typescript"
    PYTHON = "python"


class CodeChunk(BaseModel):
    """A structural code chunk extracted from AST."""

    id: Optional[str] = Field(None, description="Unique identifier in ArcadeDB")
    file_path: str = Field(..., description="Relative path to source file")
    fully_qualified_name: str = Field(
        ..., description="Full namespace.type.member name"
    )
    node_type: NodeType = Field(..., description="AST node type")
    language: Language = Field(..., description="Programming language")
    content: str = Field(..., description="Source code content")
    start_line: int = Field(..., description="1-based start line number")
    end_line: int = Field(..., description="1-based end line number")
    embedding: Optional[List[float]] = Field(None, description="Vector embedding")
    last_modified: datetime = Field(default_factory=datetime.utcnow)
    metadata: dict = Field(default_factory=dict, description="Additional metadata")

    # Token counts for debugging
    token_count: Optional[int] = Field(None, description="Token count in content")
    char_count: int = Field(..., description="Character count in content")

    class Config:
        json_schema_extra = {
            "example": {
                "file_path": "src/Services/AuthService.cs",
                "fully_qualified_name": "SwarmAssistant.Services.AuthService.Authenticate",
                "node_type": "method",
                "language": "csharp",
                "content": "public async Task<AuthResult> AuthenticateAsync(string username, string password)",
                "start_line": 45,
                "end_line": 67,
                "token_count": 128,
                "char_count": 450,
            }
        }


class IndexRequest(BaseModel):
    """Request to index source files."""

    source_path: str = Field(..., description="Path to source code directory")
    languages: List[Language] = Field(
        default=[Language.CSHARP], description="Languages to index"
    )
    incremental: bool = Field(
        default=True, description="Use git diff for incremental update"
    )
    dry_run: bool = Field(default=False, description="Parse without storing")


class IndexResponse(BaseModel):
    """Response from indexing operation."""

    total_files: int = Field(..., description="Total files processed")
    total_chunks: int = Field(..., description="Total chunks extracted")
    indexed_chunks: int = Field(..., description="Chunks stored in database")
    updated_chunks: int = Field(..., description="Chunks updated")
    deleted_chunks: int = Field(..., description="Chunks deleted")
    errors: List[str] = Field(default_factory=list)
    duration_seconds: float = Field(..., description="Processing time")


class SearchRequest(BaseModel):
    """Request to search code chunks."""

    query: str = Field(..., description="Natural language query or code snippet")
    top_k: int = Field(default=10, ge=1, le=100, description="Number of results")

    # Metadata filters
    languages: Optional[List[Language]] = Field(None, description="Filter by language")
    node_types: Optional[List[NodeType]] = Field(
        None, description="Filter by node type"
    )
    file_path_prefix: Optional[str] = Field(
        None, description="Filter by file path prefix"
    )

    # Search options
    include_content: bool = Field(
        default=True, description="Include full content in results"
    )
    include_embedding: bool = Field(
        default=False, description="Include embedding vectors"
    )


class SearchResult(BaseModel):
    """Single search result."""

    chunk: CodeChunk
    similarity_score: float = Field(..., ge=0.0, le=1.0)
    rank: int


class SearchResponse(BaseModel):
    """Response from search operation."""

    query: str
    results: List[SearchResult]
    total_found: int
    duration_ms: float


class HealthResponse(BaseModel):
    """Health check response."""

    status: str
    version: str
    arcadedb_connected: bool
    embedding_model_loaded: bool
    timestamp: datetime


class SchemaStatus(BaseModel):
    """Database schema status."""

    exists: bool
    vertex_type: Optional[str] = None
    indexes: List[str] = Field(default_factory=list)
