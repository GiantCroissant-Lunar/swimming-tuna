# Code Index Pipeline Container
# Bundles Tree-sitter, FastEmbed, and retrieval API
# Usage:
#   - Service mode: docker compose up code-index
#   - Index job: docker compose run --rm code-index index

FROM python:3.12-slim-bookworm AS base

# Install system dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    git \
    libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# Set working directory
WORKDIR /app

# Install Python dependencies
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Download tree-sitter language parsers
RUN python -c "
from tree_sitter import Language
import tree_sitter_c_sharp as tspython_cs
import tree_sitter_javascript as tspython_js
import tree_sitter_python as tspython_py
print('Tree-sitter languages ready')
"

# Copy application code
COPY src/ ./src/

# Environment configuration
ENV PYTHONPATH=/app
ENV CODE_INDEX_MODE=service
ENV ARCADEDB_URL=http://arcadedb:2480
ENV ARCADEDB_DATABASE=swarm_assistant
ENV ARCADEDB_USERNAME=root
ENV EMBEDDING_MODEL=BAAI/bge-small-en-v1.5
ENV EMBEDDING_DIMENSION=384
ENV CHUNK_MAX_TOKENS=512
ENV CHUNK_OVERLAP_TOKENS=50

# Default command (service mode)
CMD ["python", "-m", "src.api"]
