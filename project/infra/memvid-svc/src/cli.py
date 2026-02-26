"""CLI wrapper around memvid-sdk for subprocess invocation from C#."""

import argparse
import json
import os
import sys

import memvid_sdk


def _error(message: str) -> None:
    """Print error JSON to stderr and exit 1."""
    json.dump({"error": message}, sys.stderr)
    sys.stderr.write("\n")
    sys.exit(1)


def _output(data: dict) -> None:
    """Print result JSON to stdout."""
    json.dump(data, sys.stdout)
    sys.stdout.write("\n")


def cmd_create(args: argparse.Namespace) -> None:
    """Create a new memvid store at the given path."""
    try:
        memvid_sdk.create(args.path)
        _output({"created": args.path})
    except Exception as e:
        _error(str(e))


def cmd_put(args: argparse.Namespace) -> None:
    """Read JSON from stdin and add an entry to the store."""
    try:
        raw = sys.stdin.read()
        doc = json.loads(raw)
    except json.JSONDecodeError as e:
        _error(f"invalid JSON on stdin: {e}")

    for key in ("title", "label", "text"):
        if key not in doc:
            _error(f"missing required key: {key}")

    try:
        mem = memvid_sdk.use("basic", args.path)
        frame_id = mem.put(
            title=doc["title"],
            label=doc["label"],
            text=doc["text"],
            metadata=doc.get("metadata"),
        )
        mem.commit()
        _output({"frame_id": frame_id})
    except Exception as e:
        _error(str(e))


def cmd_find(args: argparse.Namespace) -> None:
    """Search the store for matching entries."""
    try:
        mem = memvid_sdk.use("basic", args.path)
        hits = mem.find(args.query, k=args.k, mode=args.mode)
        results = [{"title": h.title, "text": h.text, "score": h.score} for h in hits]
        _output({"results": results})
    except Exception as e:
        _error(str(e))


def cmd_timeline(args: argparse.Namespace) -> None:
    """List recent entries from the store."""
    try:
        mem = memvid_sdk.use("basic", args.path)
        entries = mem.timeline(limit=args.limit)
        items = [{"title": e.title, "label": e.label, "text": e.text} for e in entries]
        _output({"entries": items})
    except Exception as e:
        _error(str(e))


def cmd_info(args: argparse.Namespace) -> None:
    """Return store metadata."""
    try:
        path = args.path
        mem = memvid_sdk.use("basic", path)
        size_bytes = os.path.getsize(path)
        _output({"path": path, "frames": len(mem), "size_bytes": size_bytes})
    except Exception as e:
        _error(str(e))


def build_parser() -> argparse.ArgumentParser:
    """Build the argparse parser with all subcommands."""
    parser = argparse.ArgumentParser(
        prog="memvid-svc",
        description="Thin CLI wrapper around memvid-sdk",
    )
    subs = parser.add_subparsers(dest="command", required=True)

    # create
    p_create = subs.add_parser("create", help="Create a new store")
    p_create.add_argument("path", help="Path for the new .mv2 store")
    p_create.set_defaults(func=cmd_create)

    # put
    p_put = subs.add_parser("put", help="Add an entry (JSON from stdin)")
    p_put.add_argument("path", help="Path to existing .mv2 store")
    p_put.set_defaults(func=cmd_put)

    # find
    p_find = subs.add_parser("find", help="Search the store")
    p_find.add_argument("path", help="Path to existing .mv2 store")
    p_find.add_argument("--query", required=True, help="Search query")
    p_find.add_argument("--k", type=int, default=5, help="Number of results")
    p_find.add_argument(
        "--mode",
        default="auto",
        choices=["lex", "sem", "auto"],
        help="Search mode",
    )
    p_find.set_defaults(func=cmd_find)

    # timeline
    p_timeline = subs.add_parser("timeline", help="List recent entries")
    p_timeline.add_argument("path", help="Path to existing .mv2 store")
    p_timeline.add_argument(
        "--limit", type=int, default=50, help="Max entries to return"
    )
    p_timeline.set_defaults(func=cmd_timeline)

    # info
    p_info = subs.add_parser("info", help="Show store metadata")
    p_info.add_argument("path", help="Path to existing .mv2 store")
    p_info.set_defaults(func=cmd_info)

    return parser


def main() -> None:
    """Parse arguments and dispatch to the appropriate command."""
    parser = build_parser()
    args = parser.parse_args()
    args.func(args)
