"""Shared Supermemory API client — stdlib only.

Used by session hooks to load/save cross-session memory.
Container-tag algorithm matches the Node.js Supermemory plugin exactly
so memories are shared across Claude Code, Cline, and Copilot.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import urllib.error
import urllib.request
from pathlib import Path

API_BASE = "https://api.supermemory.ai"
HTTP_TIMEOUT = 15  # seconds — leaves headroom in 30s hook budget


# ---------------------------------------------------------------------------
# API key resolution
# ---------------------------------------------------------------------------


def get_api_key(project_root: str | Path) -> str | None:
    """Resolve Supermemory API key.

    Priority: env var > project config > global config > global credentials.
    """
    env_key = os.environ.get("SUPERMEMORY_API_KEY")
    if env_key:
        return env_key

    root = Path(project_root)
    locations = [
        root / ".claude" / ".supermemory-claude" / "config.json",
        Path.home() / ".supermemory-claude" / "config.json",
        Path.home() / ".supermemory-claude" / "credentials.json",
    ]
    for loc in locations:
        try:
            if loc.exists():
                data = json.loads(loc.read_text())
                key = data.get("apiKey") or data.get("api_key")
                if key:
                    return key
        except (json.JSONDecodeError, OSError):
            continue
    return None


# ---------------------------------------------------------------------------
# Git helpers — match Node.js plugin algorithm
# ---------------------------------------------------------------------------


def get_git_root(cwd: str) -> str | None:
    """Get the git root, with worktree support matching the plugin.

    Uses ``git rev-parse --git-common-dir`` so all worktrees of the same
    repo share the same container tag (unless SUPERMEMORY_ISOLATE_WORKTREES
    is set).
    """
    isolate = os.environ.get("SUPERMEMORY_ISOLATE_WORKTREES") == "true"

    try:
        if isolate:
            result = subprocess.run(
                ["git", "rev-parse", "--show-toplevel"],
                cwd=cwd,
                capture_output=True,
                text=True,
                timeout=5,
            )
            if result.returncode == 0 and result.stdout.strip():
                return result.stdout.strip()
            return None

        result = subprocess.run(
            ["git", "rev-parse", "--git-common-dir"],
            cwd=cwd,
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode != 0:
            return None

        git_common_dir = result.stdout.strip()

        # Normal repo: --git-common-dir returns '.git'
        if git_common_dir == ".git":
            result2 = subprocess.run(
                ["git", "rev-parse", "--show-toplevel"],
                cwd=cwd,
                capture_output=True,
                text=True,
                timeout=5,
            )
            if result2.returncode == 0 and result2.stdout.strip():
                return result2.stdout.strip()
            return None

        # Worktree: resolve the path (abspath, not resolve, to avoid
        # following symlinks — matches Node.js path.resolve behavior)
        resolved = os.path.abspath(os.path.join(cwd, git_common_dir))

        # If resolved ends with /.git and doesn't contain /.git/ in path
        if (
            os.path.basename(resolved) == ".git"
            and f"{os.sep}.git{os.sep}" not in resolved
        ):
            return os.path.dirname(resolved)

        # Fallback to --show-toplevel
        result3 = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            cwd=cwd,
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result3.returncode == 0 and result3.stdout.strip():
            return result3.stdout.strip()
        return None
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return None


def _get_git_repo_name(cwd: str) -> str | None:
    """Extract repo name from ``git remote get-url origin``."""
    try:
        result = subprocess.run(
            ["git", "remote", "get-url", "origin"],
            cwd=cwd,
            capture_output=True,
            text=True,
            timeout=5,
        )
        if result.returncode != 0:
            return None
        remote_url = result.stdout.strip()
        match = re.search(r"[/:]([^/]+?)(?:\.git)?$", remote_url)
        return match.group(1) if match else None
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError):
        return None


# ---------------------------------------------------------------------------
# Project config (for container-tag overrides)
# ---------------------------------------------------------------------------


def _load_project_config(cwd: str) -> dict | None:
    """Load .claude/.supermemory-claude/config.json relative to git root."""
    git_root = get_git_root(cwd)
    base = git_root or cwd
    config_path = Path(base) / ".claude" / ".supermemory-claude" / "config.json"
    try:
        if config_path.exists():
            return json.loads(config_path.read_text())
    except (json.JSONDecodeError, OSError):
        pass
    return None


# ---------------------------------------------------------------------------
# Container tags — must match Node.js plugin exactly
# ---------------------------------------------------------------------------


def _sha256_prefix(s: str) -> str:
    """SHA-256 first 16 hex chars, matching the plugin."""
    return hashlib.sha256(s.encode()).hexdigest()[:16]


def sanitize_repo_name(name: str) -> str:
    """Sanitize repo name for use as container tag.

    Matches the Node.js plugin: lowercase, replace non-alnum with _,
    collapse runs of _, strip leading/trailing _.
    """
    s = name.lower()
    s = re.sub(r"[^a-z0-9]", "_", s)
    s = re.sub(r"_+", "_", s)
    s = s.strip("_")
    return s


def get_container_tag(cwd: str) -> str:
    """Personal container tag: ``claudecode_project_<sha256[:16]>``."""
    config = _load_project_config(cwd)
    if config and config.get("personalContainerTag"):
        return config["personalContainerTag"]
    git_root = get_git_root(cwd)
    base_path = git_root or cwd
    return f"claudecode_project_{_sha256_prefix(base_path)}"


def get_repo_container_tag(cwd: str) -> str:
    """Repo container tag: ``repo_<sanitized_name>``."""
    config = _load_project_config(cwd)
    if config and config.get("repoContainerTag"):
        return config["repoContainerTag"]
    git_root = get_git_root(cwd)
    base_path = git_root or cwd
    repo_name = _get_git_repo_name(base_path)
    if not repo_name:
        repo_name = Path(base_path).name or "unknown"
    return f"repo_{sanitize_repo_name(repo_name)}"


# ---------------------------------------------------------------------------
# API calls
# ---------------------------------------------------------------------------


def _api_request(
    api_key: str, endpoint: str, body: dict, timeout: int = HTTP_TIMEOUT
) -> dict | None:
    """Make an authenticated POST to the Supermemory API."""
    url = f"{API_BASE}{endpoint}"
    req = urllib.request.Request(  # noqa: S310
        url,
        data=json.dumps(body).encode(),
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310
            return json.loads(resp.read())
    except (
        urllib.error.URLError,
        urllib.error.HTTPError,
        OSError,
        json.JSONDecodeError,
        Exception,
    ):
        return None


def get_profile(api_key: str, container_tag: str, query: str) -> dict | None:
    """POST /v3/memories/profile — get profiled context for a container."""
    return _api_request(
        api_key,
        "/v3/memories/profile",
        {
            "containerTags": [container_tag],
            "q": query,
        },
    )


def search_memories(api_key: str, query: str, container_tag: str) -> dict | None:
    """POST /v3/search — search memories in a container."""
    return _api_request(
        api_key,
        "/v3/search",
        {
            "q": query,
            "containerTags": [container_tag],
            "limit": 10,
        },
    )


def save_memory(
    api_key: str,
    content: str,
    container_tag: str,
    metadata: dict | None = None,
) -> dict | None:
    """POST /v3/memories — save a memory to Supermemory."""
    body: dict = {
        "content": content,
        "containerTags": [container_tag],
    }
    if metadata:
        body["metadata"] = metadata
    return _api_request(api_key, "/v3/memories", body)


# ---------------------------------------------------------------------------
# Context formatting — matches plugin output tags
# ---------------------------------------------------------------------------


def format_context(profile_result: dict | None, section_title: str) -> str:
    """Format a profile API response into a markdown section."""
    if not profile_result:
        return ""
    profile_text = profile_result.get("profile", "")
    if not profile_text:
        return ""
    return f"### {section_title}\n{profile_text}"


def combine_contexts(personal: str, repo: str) -> str:
    """Wrap personal + repo context in ``<supermemory-context>`` tags."""
    parts = [p for p in (personal, repo) if p]
    if not parts:
        return ""
    inner = "\n\n".join(parts)
    return (
        "<supermemory-context>\n"
        "The following is recalled context. Reference it only when relevant to the conversation.\n\n"
        f"{inner}\n\n"
        "Use these memories naturally when relevant — including indirect connections — "
        "but don't force them into every response or make assumptions beyond what's stated.\n"
        "</supermemory-context>"
    )
