#!/usr/bin/env python3
"""Session end hook — save session summary to Supermemory.

Reads config.json (synced by adapter) to decide whether to call Supermemory.
Claude Code sets enabled=false (plugin handles it); Cline/Copilot set enabled=true.
"""

from __future__ import annotations

import json
import sys
from datetime import datetime, timezone
from pathlib import Path


def _get_config_path() -> Path:
    """Return path to config.json next to this script."""
    return Path(__file__).resolve().parent / "config.json"


def _save_session_memory(
    project_root: str, adapter: str, session_id: str, transcript_path: str | None
) -> bool:
    """Save session content to Supermemory."""
    # Add lib dir to path for supermemory_client import
    lib_dir = str(Path(__file__).resolve().parent.parent / "lib")
    if lib_dir not in sys.path:
        sys.path.insert(0, lib_dir)

    from supermemory_client import (
        get_api_key,
        get_container_tag,
        get_repo_container_tag,
        save_memory,
    )

    api_key = get_api_key(project_root)
    if not api_key:
        return False

    cwd = project_root
    personal_tag = get_container_tag(cwd)

    # Read transcript if available
    content = ""
    if transcript_path:
        try:
            content = Path(transcript_path).read_text()
        except OSError:
            pass

    if not content:
        content = f"Session {session_id} ended (no transcript available)"

    timestamp = datetime.now(timezone.utc).isoformat()
    metadata = {
        "type": "session_turn",
        "project": Path(project_root).name,
        "timestamp": timestamp,
        "source_tool": adapter,
        "session_id": session_id,
    }

    # Save to personal container
    result = save_memory(api_key, content, personal_tag, metadata)

    # Also save to repo container for team sharing
    repo_tag = get_repo_container_tag(cwd)
    save_memory(api_key, content, repo_tag, metadata)

    return result is not None


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError, ValueError):
        payload = {}

    session_id = payload.get("session_id", "unknown")
    result: dict = {"result": "ok", "session_id": session_id}

    # Read config to decide whether to use Supermemory
    config_path = _get_config_path()
    try:
        config = json.loads(config_path.read_text()) if config_path.exists() else {}
    except (json.JSONDecodeError, OSError):
        config = {}

    supermemory_enabled = config.get("supermemory", {}).get("enabled", False)

    if supermemory_enabled:
        try:
            project_root = str(Path(__file__).resolve().parent.parent.parent)
            adapter = config.get("adapter", "unknown")
            transcript_path = payload.get("transcript_path")
            _save_session_memory(project_root, adapter, session_id, transcript_path)
        except Exception:
            pass  # Never block the session

    print(json.dumps(result))


if __name__ == "__main__":
    main()
