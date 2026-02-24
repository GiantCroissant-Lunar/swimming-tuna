"""ArcadeDB client for vector storage and retrieval."""

import json
import logging
import os
from datetime import datetime, timezone
from typing import List, Optional

import httpx

from src.models import CodeChunk, SchemaStatus, Language, NodeType

logger = logging.getLogger(__name__)


class ArcadeDbClient:
    """Client for ArcadeDB vector operations."""

    def __init__(
        self,
        url: Optional[str] = None,
        database: Optional[str] = None,
        username: Optional[str] = None,
        password: Optional[str] = None,
    ):
        self.url = (url or os.getenv("ARCADEDB_URL", "http://localhost:2480")).rstrip(
            "/"
        )
        self.database = database or os.getenv("ARCADEDB_DATABASE", "swarm_assistant")
        self.username = username or os.getenv("ARCADEDB_USERNAME", "root")
        self.password = password or os.getenv("ARCADEDB_PASSWORD", "playwithdata")
        self._client: Optional[httpx.Client] = None

    @property
    def client(self) -> httpx.Client:
        """Get or create HTTP client."""
        if self._client is None:
            self._client = httpx.Client(
                base_url=self.url, auth=(self.username, self.password), timeout=60.0
            )
        return self._client

    def close(self):
        """Close HTTP client."""
        if self._client:
            self._client.close()
            self._client = None

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        self.close()

    def health_check(self) -> bool:
        """Check if ArcadeDB is accessible."""
        try:
            response = self.client.get("/server")
            return response.status_code == 200
        except Exception:  # noqa: BLE001
            return False

    def check_schema(self) -> SchemaStatus:
        """Check if CodeChunk vertex type and vector index exist."""
        try:
            # Check if vertex type exists
            response = self.client.get(
                f"/api/v1/query/{self.database}/sql",
                params={"command": "SELECT FROM schema:types WHERE name = 'CodeChunk'"},
            )
            response.raise_for_status()
            result = response.json()

            exists = bool(result.get("result", []))

            if not exists:
                return SchemaStatus(exists=False)

            # Get indexes
            response = self.client.get(
                f"/api/v1/query/{self.database}/sql",
                params={
                    "command": "SELECT FROM schema:indexes WHERE type = 'CodeChunk'"
                },
            )
            result = response.json()
            indexes = [idx.get("name", "") for idx in result.get("result", [])]

            return SchemaStatus(exists=True, vertex_type="CodeChunk", indexes=indexes)
        except Exception as e:  # noqa: BLE001
            return SchemaStatus(exists=False, indexes=[str(e)])

    def create_schema(self, dimension: int = 384) -> bool:
        """Create CodeChunk vertex type and vector index."""
        try:
            # Create vertex type
            self._execute_command("""
                CREATE VERTEX TYPE CodeChunk IF NOT EXISTS
            """)

            # Create properties
            properties = [
                ("filePath", "STRING"),
                ("fullyQualifiedName", "STRING"),
                ("nodeType", "STRING"),
                ("language", "STRING"),
                ("content", "STRING"),
                ("startLine", "INTEGER"),
                ("endLine", "INTEGER"),
                ("lastModified", "DATETIME"),
                ("metadata", "EMBEDDED"),
                ("tokenCount", "INTEGER"),
                ("charCount", "INTEGER"),
            ]

            for prop_name, prop_type in properties:
                self._execute_command(f"""
                    CREATE PROPERTY CodeChunk.{prop_name} IF NOT EXISTS {prop_type}
                """)

            # Create embedding property with vector type
            self._execute_command("""
                CREATE PROPERTY CodeChunk.embedding IF NOT EXISTS
                ARRAY OF FLOAT
            """)

            # Create indexes
            self._execute_command("""
                CREATE INDEX CodeChunk.filePath IF NOT EXISTS ON CodeChunk(filePath) NOTUNIQUE
            """)

            self._execute_command("""
                CREATE INDEX CodeChunk.fullyQualifiedName IF NOT EXISTS
                ON CodeChunk(fullyQualifiedName) NOTUNIQUE
            """)

            self._execute_command("""
                CREATE INDEX CodeChunk.language IF NOT EXISTS ON CodeChunk(language) NOTUNIQUE
            """)

            self._execute_command("""
                CREATE INDEX CodeChunk.nodeType IF NOT EXISTS ON CodeChunk(nodeType) NOTUNIQUE
            """)

            # Create vector index for similarity search
            # Using L2 distance for vector similarity
            self._execute_command("""
                CREATE INDEX CodeChunk.embedding_vector IF NOT EXISTS
                ON CodeChunk(embedding) VECTOR ENGINE L2
            """)

            return True
        except Exception:  # noqa: BLE001
            logger.exception("Failed to create schema")
            return False

    def execute_command(self, command: str, params: Optional[dict] = None) -> dict:
        """Execute a SQL command with optional parameterized bindings."""
        payload: dict = {"language": "sql", "command": command.strip()}
        if params:
            payload["params"] = params
        response = self.client.post(
            f"/api/v1/command/{self.database}",
            json=payload,
        )
        response.raise_for_status()
        return response.json()

    def upsert_chunk(self, chunk: CodeChunk) -> tuple[Optional[str], bool]:
        """Insert or update a code chunk. Returns (record_id, was_update)."""
        try:
            # Check if chunk exists by file_path + fully_qualified_name
            query = """
                SELECT @rid FROM CodeChunk
                WHERE filePath = :filePath
                AND fullyQualifiedName = :fqn
            """
            response = self.client.post(
                f"/api/v1/query/{self.database}/sql",
                json={
                    "command": query,
                    "params": {
                        "filePath": chunk.file_path,
                        "fqn": chunk.fully_qualified_name,
                    },
                },
            )
            result = response.json()
            existing = result.get("result", [])

            embedding_list = chunk.embedding if chunk.embedding else []

            if existing:
                # Update existing
                rid = existing[0].get("@rid")
                self.execute_command(
                    """
                    UPDATE CodeChunk SET
                        content = :content,
                        startLine = :startLine,
                        endLine = :endLine,
                        embedding = :embedding,
                        lastModified = :lastModified,
                        tokenCount = :tokenCount,
                        charCount = :charCount
                    WHERE @rid = :rid
                    """,
                    params={
                        "content": chunk.content,
                        "startLine": chunk.start_line,
                        "endLine": chunk.end_line,
                        "embedding": embedding_list,
                        "lastModified": chunk.last_modified.isoformat()
                        if chunk.last_modified
                        else None,
                        "tokenCount": chunk.token_count or 0,
                        "charCount": chunk.char_count,
                        "rid": rid,
                    },
                )
                return rid, True
            else:
                # Insert new
                result = self.execute_command(
                    """
                    INSERT INTO CodeChunk SET
                        filePath = :filePath,
                        fullyQualifiedName = :fqn,
                        nodeType = :nodeType,
                        language = :language,
                        content = :content,
                        startLine = :startLine,
                        endLine = :endLine,
                        embedding = :embedding,
                        lastModified = :lastModified,
                        tokenCount = :tokenCount,
                        charCount = :charCount
                    """,
                    params={
                        "filePath": chunk.file_path,
                        "fqn": chunk.fully_qualified_name,
                        "nodeType": chunk.node_type.value,
                        "language": chunk.language.value,
                        "content": chunk.content,
                        "startLine": chunk.start_line,
                        "endLine": chunk.end_line,
                        "embedding": embedding_list,
                        "lastModified": chunk.last_modified.isoformat()
                        if chunk.last_modified
                        else None,
                        "tokenCount": chunk.token_count or 0,
                        "charCount": chunk.char_count,
                    },
                )
                return result.get("result", [{}])[0].get("@rid"), False
        except Exception:  # noqa: BLE001
            logger.exception("Failed to upsert chunk")
            return None, False

    def delete_chunks_by_file(self, file_path: str) -> int:
        """Delete all chunks for a file. Returns count deleted."""
        try:
            result = self.execute_command(
                "DELETE FROM CodeChunk WHERE filePath = :filePath",
                params={"filePath": file_path},
            )
            return result.get("count", 0)
        except Exception:  # noqa: BLE001
            logger.exception("Failed to delete chunks for %s", file_path)
            return 0

    def search_similar(
        self,
        embedding: List[float],
        top_k: int = 10,
        language_filter: Optional[List[Language]] = None,
        node_type_filter: Optional[List[NodeType]] = None,
        file_path_prefix: Optional[str] = None,
    ) -> List[CodeChunk]:
        """Search for similar code chunks using vector similarity."""
        try:
            where_conditions = []
            params: dict = {
                "topK": top_k,
            }

            if language_filter:
                lang_values = [lang.value for lang in language_filter]
                where_conditions.append("language IN :languages")
                params["languages"] = lang_values

            if node_type_filter:
                type_values = [t.value for t in node_type_filter]
                where_conditions.append("nodeType IN :nodeTypes")
                params["nodeTypes"] = type_values

            if file_path_prefix:
                where_conditions.append("filePath LIKE :filePathPrefix")
                params["filePathPrefix"] = f"{file_path_prefix}%"

            where_clause = ""
            if where_conditions:
                where_clause = "WHERE " + " AND ".join(where_conditions)

            # Embedding passed as JSON in the query string (no param support for vectors)
            embedding_json = json.dumps(embedding)
            query = f"""
                SELECT *, vectorDistance(embedding, {embedding_json}) as score
                FROM CodeChunk
                {where_clause}
                ORDER BY score ASC
                LIMIT :topK
            """

            response = self.client.post(
                f"/api/v1/query/{self.database}/sql",
                json={"command": query, "params": params},
            )
            response.raise_for_status()
            result = response.json()

            chunks = []
            for record in result.get("result", []):
                chunk = self._record_to_chunk(record)
                if chunk:
                    chunks.append(chunk)

            return chunks
        except Exception:  # noqa: BLE001
            logger.exception("Search failed")
            return []

    def _record_to_chunk(self, record: dict) -> Optional[CodeChunk]:
        """Convert ArcadeDB record to CodeChunk."""
        try:
            last_modified_str = record.get("lastModified")
            if last_modified_str:
                last_modified = datetime.fromisoformat(last_modified_str)
            else:
                last_modified = datetime.now(timezone.utc)

            return CodeChunk(
                id=record.get("@rid"),
                file_path=record.get("filePath", ""),
                fully_qualified_name=record.get("fullyQualifiedName", ""),
                node_type=NodeType(record.get("nodeType", "unknown")),
                language=Language(record.get("language", "csharp")),
                content=record.get("content", ""),
                start_line=record.get("startLine", 0),
                end_line=record.get("endLine", 0),
                embedding=record.get("embedding"),
                last_modified=last_modified,
                similarity_score=record.get("score"),
                token_count=record.get("tokenCount"),
                char_count=record.get("charCount", 0),
            )
        except Exception:  # noqa: BLE001
            logger.exception("Failed to convert record")
            return None
