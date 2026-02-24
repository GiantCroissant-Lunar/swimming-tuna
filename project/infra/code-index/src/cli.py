"""CLI for code indexing operations."""

import sys
from pathlib import Path
from typing import List, Optional

import typer
from rich.console import Console
from rich.progress import Progress, SpinnerColumn, TextColumn
from rich.table import Table

from src.models import IndexRequest, Language
from src.indexer import CodeIndexer
from src.arcadedb_client import ArcadeDbClient
from src.embedder import EmbeddingGenerator


app = typer.Typer(
    name="code-index",
    help="Structural code indexing with AST-aware chunking",
    rich_markup_mode="rich",
)
console = Console()


@app.command()
def index(
    source_path: Path = typer.Argument(
        ...,
        help="Path to source code directory",
        exists=True,
        file_okay=False,
        dir_okay=True,
    ),
    languages: List[str] = typer.Option(
        ["csharp"], "--language", "-l", help="Languages to index"
    ),
    incremental: bool = typer.Option(
        True, "--incremental/--full", help="Use git diff for incremental indexing"
    ),
    dry_run: bool = typer.Option(
        False, "--dry-run", help="Parse without storing to database"
    ),
):
    """Index source files into the code database."""

    # Parse languages
    lang_enums = []
    for lang in languages:
        try:
            lang_enums.append(Language(lang.lower()))
        except ValueError:
            console.print(f"[red]Unknown language: {lang}[/red]")
            sys.exit(1)

    request = IndexRequest(
        source_path=str(source_path),
        languages=lang_enums,
        incremental=incremental,
        dry_run=dry_run,
    )

    with Progress(
        SpinnerColumn(),
        TextColumn("[progress.description]{task.description}"),
        console=console,
    ) as progress:
        task = progress.add_task("Indexing...", total=None)

        indexer = CodeIndexer()

        # Ensure schema
        progress.update(task, description="Checking database schema...")
        if not indexer.ensure_schema():
            console.print("[red]Failed to create database schema[/red]")
            sys.exit(1)

        progress.update(task, description="Processing files...")
        response = indexer.index(request)

    # Print results
    console.print()
    console.print("[green]Indexing complete![/green]")
    console.print()

    table = Table(title="Results")
    table.add_column("Metric", style="cyan")
    table.add_column("Value", style="magenta")

    table.add_row("Files processed", str(response.total_files))
    table.add_row("Chunks extracted", str(response.total_chunks))
    table.add_row("Chunks indexed", str(response.indexed_chunks))
    table.add_row("Chunks updated", str(response.updated_chunks))
    table.add_row("Chunks deleted", str(response.deleted_chunks))
    table.add_row("Duration", f"{response.duration_seconds:.2f}s")

    console.print(table)

    if response.errors:
        console.print()
        console.print(f"[yellow]Errors ({len(response.errors)}):[/yellow]")
        for error in response.errors[:10]:
            console.print(f"  - {error}")
        if len(response.errors) > 10:
            console.print(f"  ... and {len(response.errors) - 10} more")


@app.command()
def search(
    query: str = typer.Argument(..., help="Search query"),
    top_k: int = typer.Option(10, "--top-k", "-k", help="Number of results"),
    language: Optional[str] = typer.Option(
        None, "--language", "-l", help="Filter by language"
    ),
    node_type: Optional[str] = typer.Option(
        None, "--type", "-t", help="Filter by node type"
    ),
    file_prefix: Optional[str] = typer.Option(
        None, "--file", "-f", help="Filter by file path prefix"
    ),
):
    """Search for similar code chunks."""

    embedder = EmbeddingGenerator()
    db = ArcadeDbClient()

    # Generate query embedding
    with console.status("Generating query embedding..."):
        query_embedding = embedder.embed_text(query)

    # Parse filters
    lang_filter = [Language(language)] if language else None
    type_filter = None  # TODO: Parse node type

    # Search
    with console.status("Searching..."):
        results = db.search_similar(
            embedding=query_embedding,
            top_k=top_k,
            language_filter=lang_filter,
            node_type_filter=type_filter,
            file_path_prefix=file_prefix,
        )

    if not results:
        console.print("[yellow]No results found[/yellow]")
        return

    # Display results
    console.print()
    console.print(f"[green]Found {len(results)} results:[/green]")
    console.print()

    for i, chunk in enumerate(results, 1):
        table = Table(title=f"Result {i}: {chunk.fully_qualified_name}")
        table.add_column("Field", style="cyan")
        table.add_column("Value", style="white")

        table.add_row("File", chunk.file_path)
        table.add_row("Type", chunk.node_type.value)
        table.add_row("Language", chunk.language.value)
        table.add_row("Lines", f"{chunk.start_line}-{chunk.end_line}")
        table.add_row("Tokens", str(chunk.token_count or "N/A"))

        console.print(table)
        console.print()
        console.print("[dim]Content:[/dim]")
        console.print(
            f"```\n{chunk.content[:500]}{'...' if len(chunk.content) > 500 else ''}\n```"
        )
        console.print()
        console.print("-" * 80)
        console.print()


@app.command()
def stats():
    """Show index statistics."""
    db = ArcadeDbClient()

    try:
        result = db._execute_command("SELECT COUNT(*) as count FROM CodeChunk")
        total = result.get("result", [{}])[0].get("count", 0)

        console.print(f"[green]Total chunks: {total}[/green]")
        console.print()

        if total > 0:
            # By language
            result = db._execute_command("""
                SELECT language, COUNT(*) as count
                FROM CodeChunk
                GROUP BY language
            """)

            table = Table(title="Chunks by Language")
            table.add_column("Language", style="cyan")
            table.add_column("Count", style="magenta")

            for row in result.get("result", []):
                table.add_row(row.get("language", "unknown"), str(row.get("count", 0)))

            console.print(table)
            console.print()

            # By node type
            result = db._execute_command("""
                SELECT nodeType, COUNT(*) as count
                FROM CodeChunk
                GROUP BY nodeType
                ORDER BY count DESC
            """)

            table = Table(title="Chunks by Node Type")
            table.add_column("Node Type", style="cyan")
            table.add_column("Count", style="magenta")

            for row in result.get("result", []):
                table.add_row(row.get("nodeType", "unknown"), str(row.get("count", 0)))

            console.print(table)

    except Exception as e:
        console.print(f"[red]Error: {e}[/red]")


@app.command()
def reset(force: bool = typer.Option(False, "--force", help="Skip confirmation")):
    """Reset the code index (delete all chunks)."""
    if not force:
        confirm = typer.confirm("This will delete all indexed code chunks. Continue?")
        if not confirm:
            console.print("[yellow]Aborted[/yellow]")
            return

    db = ArcadeDbClient()

    try:
        result = db._execute_command("DELETE FROM CodeChunk")
        count = result.get("count", 0)
        console.print(f"[green]Deleted {count} chunks[/green]")
    except Exception as e:
        console.print(f"[red]Error: {e}[/red]")


@app.command()
def serve(
    host: str = typer.Option("0.0.0.0", "--host", "-h", help="Host to bind"),
    port: int = typer.Option(8080, "--port", "-p", help="Port to bind"),
):
    """Start the retrieval API server."""
    import uvicorn
    from src.api import app

    console.print(f"[green]Starting server on {host}:{port}[/green]")
    uvicorn.run(app, host=host, port=port)


if __name__ == "__main__":
    app()
