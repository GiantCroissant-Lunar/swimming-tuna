"""FastEmbed integration for local embedding generation."""

import os
from typing import List, Optional

from fastembed import TextEmbedding

from src.models import CodeChunk


class EmbeddingGenerator:
    """Generate embeddings using FastEmbed (local ONNX, no GPU required)."""

    def __init__(
        self, model_name: Optional[str] = None, cache_dir: Optional[str] = None
    ):
        self.model_name = model_name or os.getenv(
            "EMBEDDING_MODEL", "BAAI/bge-small-en-v1.5"
        )
        self.cache_dir = cache_dir
        self._model: Optional[TextEmbedding] = None
        self._dimension: Optional[int] = None

    @property
    def model(self) -> TextEmbedding:
        """Lazy-load the embedding model."""
        if self._model is None:
            self._model = TextEmbedding(
                model_name=self.model_name, cache_dir=self.cache_dir
            )
        return self._model

    @property
    def dimension(self) -> int:
        """Get embedding dimension."""
        if self._dimension is None:
            # Get dimension from model or config
            dimension = int(os.getenv("EMBEDDING_DIMENSION", "384"))
            self._dimension = dimension
        return self._dimension

    def embed_text(self, text: str) -> List[float]:
        """Generate embedding for a single text."""
        embeddings = list(self.model.embed([text]))
        return embeddings[0].tolist()

    def embed_texts(self, texts: List[str]) -> List[List[float]]:
        """Generate embeddings for multiple texts."""
        embeddings = list(self.model.embed(texts))
        return [e.tolist() for e in embeddings]

    def embed_chunks(self, chunks: List[CodeChunk]) -> None:
        """Generate and assign embeddings to code chunks in-place."""
        if not chunks:
            return

        texts = [chunk.content for chunk in chunks]
        embeddings = self.embed_texts(texts)

        for chunk, embedding in zip(chunks, embeddings):
            chunk.embedding = embedding

    def is_loaded(self) -> bool:
        """Check if model is loaded."""
        return self._model is not None


class TokenCounter:
    """Estimate token count for code chunks."""

    def __init__(self):
        self._encoding = None

    def _get_encoding(self):
        """Lazy-load tiktoken encoding."""
        if self._encoding is None:
            try:
                import tiktoken

                self._encoding = tiktoken.get_encoding("cl100k_base")
            except ImportError:
                self._encoding = None
        return self._encoding

    def count_tokens(self, text: str) -> int:
        """Count tokens in text."""
        encoding = self._get_encoding()
        if encoding:
            return len(encoding.encode(text))
        # Fallback: rough approximation (4 chars per token)
        return len(text) // 4

    def count_chunk_tokens(self, chunk: CodeChunk) -> int:
        """Count tokens in a code chunk and update the chunk."""
        token_count = self.count_tokens(chunk.content)
        chunk.token_count = token_count
        return token_count
