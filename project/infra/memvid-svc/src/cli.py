"""CLI wrapper around memvid-sdk for subprocess invocation from C#."""

import argparse
import json
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

    return parser


def main() -> None:
    """Parse arguments and dispatch to the appropriate command."""
    parser = build_parser()
    args = parser.parse_args()
    args.func(args)
