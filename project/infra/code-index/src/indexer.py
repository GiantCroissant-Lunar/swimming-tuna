"""Full indexing pipeline with git-aware incremental updates."""

import logging
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional, Tuple

from git import Repo

from src.models import CodeChunk, IndexRequest, IndexResponse, Language
from src.parser import TreeSitterParser, CodeExtractor, find_source_files
from src.embedder import EmbeddingGenerator, TokenCounter
from src.arcadedb_client import ArcadeDbClient

logger = logging.getLogger(__name__)


class CodeIndexer:
    """Main indexer orchestrating parse → chunk → embed → store."""

    def __init__(
        self,
        arcadedb_client: Optional[ArcadeDbClient] = None,
        embedder: Optional[EmbeddingGenerator] = None,
    ):
        self.parser = TreeSitterParser()
        self.extractor = CodeExtractor(self.parser)
        self.embedder = embedder or EmbeddingGenerator()
        self.token_counter = TokenCounter()
        self.db = arcadedb_client or ArcadeDbClient()

    def ensure_schema(self) -> bool:
        """Ensure database schema exists."""
        schema = self.db.check_schema()
        if not schema.exists:
            return self.db.create_schema(dimension=self.embedder.dimension)
        return True

    def index(self, request: IndexRequest) -> IndexResponse:
        """Execute full indexing pipeline."""
        start_time = time.time()
        source_path = Path(request.source_path)

        if not source_path.exists():
            return IndexResponse(
                total_files=0,
                total_chunks=0,
                indexed_chunks=0,
                updated_chunks=0,
                deleted_chunks=0,
                errors=[f"Source path does not exist: {source_path}"],
                duration_seconds=0,
            )

        # Get files to index
        if request.incremental and (source_path / ".git").exists():
            files_to_index, files_to_delete = self._get_changed_files(
                source_path, request.languages
            )
        else:
            files_to_index = find_source_files(source_path, request.languages)
            files_to_delete = []

        total_chunks = 0
        indexed_chunks = 0
        updated_chunks = 0
        errors = []

        # Process files in batches
        batch_size = 50
        for i in range(0, len(files_to_index), batch_size):
            batch = files_to_index[i : i + batch_size]

            for file_path, language in batch:
                try:
                    chunks = self._process_file(
                        file_path, source_path, language, request.dry_run
                    )
                    total_chunks += len(chunks)

                    if not request.dry_run:
                        for chunk in chunks:
                            rid, was_update = self.db.upsert_chunk(chunk)
                            if rid:
                                if was_update:
                                    updated_chunks += 1
                                else:
                                    indexed_chunks += 1
                except Exception as e:
                    errors.append(f"{file_path}: {str(e)}")

        # Delete chunks for removed files
        deleted_chunks = 0
        for file_path in files_to_delete:
            if not request.dry_run:
                deleted_chunks += self.db.delete_chunks_by_file(file_path)

        duration = time.time() - start_time

        return IndexResponse(
            total_files=len(files_to_index),
            total_chunks=total_chunks,
            indexed_chunks=indexed_chunks,
            updated_chunks=updated_chunks,
            deleted_chunks=deleted_chunks,
            errors=errors,
            duration_seconds=duration,
        )

    def _process_file(
        self, file_path: Path, source_root: Path, language: Language, dry_run: bool
    ) -> List[CodeChunk]:
        """Process a single file: parse, chunk, embed."""
        # Extract chunks from AST
        chunks = self.extractor.extract_chunks(file_path, source_root, language)

        if not chunks:
            return []

        # Count tokens
        for chunk in chunks:
            self.token_counter.count_chunk_tokens(chunk)

        # Get file modification time (UTC-aware)
        mtime = datetime.fromtimestamp(file_path.stat().st_mtime, tz=timezone.utc)
        for chunk in chunks:
            chunk.last_modified = mtime

        if not dry_run:
            # Generate embeddings
            self.embedder.embed_chunks(chunks)

        return chunks

    def _get_changed_files(
        self, repo_path: Path, languages: List[Language]
    ) -> Tuple[List[Tuple[Path, Language]], List[str]]:
        """Get changed files using git diff."""
        try:
            repo = Repo(repo_path)

            # Get diff against HEAD
            diff_index = repo.index.diff("HEAD")

            changed_files = []
            deleted_files = []

            # Check for changes
            for diff_item in diff_index:
                if diff_item.deleted_file:
                    deleted_files.append(diff_item.a_path or diff_item.b_path)
                elif diff_item.change_type == "R":
                    # Rename: delete old path, index new path
                    if diff_item.a_path:
                        deleted_files.append(diff_item.a_path)
                    new_path = diff_item.b_path or diff_item.a_path
                    full_path = repo_path / new_path
                    if full_path.exists():
                        lang = self._detect_language_from_path(full_path)
                        if lang and lang in languages:
                            changed_files.append((full_path, lang))
                elif diff_item.change_type in ("M", "A"):
                    file_path = diff_item.a_path or diff_item.b_path
                    full_path = repo_path / file_path
                    if full_path.exists():
                        lang = self._detect_language_from_path(full_path)
                        if lang and lang in languages:
                            changed_files.append((full_path, lang))

            # Also check untracked files
            for file_path in repo.untracked_files:
                full_path = repo_path / file_path
                lang = self._detect_language_from_path(full_path)
                if lang and lang in languages:
                    changed_files.append((full_path, lang))

            return changed_files, deleted_files
        except Exception:  # noqa: BLE001
            logger.warning("Git diff failed, falling back to full index", exc_info=True)
            return find_source_files(repo_path, languages), []

    def _detect_language_from_path(self, file_path: Path) -> Optional[Language]:
        """Detect language from file path."""
        from src.parser import detect_language

        return detect_language(file_path)

    def get_index_stats(self) -> dict:
        """Get indexing statistics."""
        try:
            result = self.db.execute_command("SELECT COUNT(*) as count FROM CodeChunk")
            total_chunks = result.get("result", [{}])[0].get("count", 0)

            result = self.db.execute_command("""
                SELECT language, COUNT(*) as count
                FROM CodeChunk
                GROUP BY language
            """)
            by_language = {
                r.get("language", "unknown"): r.get("count", 0)
                for r in result.get("result", [])
            }

            return {"total_chunks": total_chunks, "by_language": by_language}
        except Exception as e:
            return {"error": str(e)}


class IncrementalIndexer:
    """Incremental indexer with file watching support."""

    def __init__(self, indexer: CodeIndexer):
        self.indexer = indexer
        self._last_index_time: Optional[datetime] = None

    def index_since_last(self, request: IndexRequest) -> IndexResponse:
        """Index files changed since last index run."""
        # TODO: Implement timestamp-based incremental indexing
        # For now, use git-based incremental
        return self.indexer.index(request)

    def watch_and_index(self, request: IndexRequest, interval_seconds: int = 60):
        """Watch for file changes and index periodically."""
        import time

        logger.info(
            "Starting file watcher for %s (interval: %ds)",
            request.source_path,
            interval_seconds,
        )

        while True:
            try:
                response = self.index_since_last(request)
                if response.total_files > 0:
                    logger.info(
                        "Indexed %d files, %d chunks (%.1fs)",
                        response.total_files,
                        response.total_chunks,
                        response.duration_seconds,
                    )

                time.sleep(interval_seconds)
            except KeyboardInterrupt:
                logger.info("Stopping file watcher")
                break
            except Exception:  # noqa: BLE001
                logger.exception("Error during indexing")
                time.sleep(interval_seconds)
