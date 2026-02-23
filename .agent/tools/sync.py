#!/usr/bin/env python3
"""Agent sync engine.

Reads adapter configs from .agent/adapters/<tool>/config.yml and executes
sync actions to set up tool-specific files (rules, hooks, pointers, etc.).

Usage:
    python .agent/tools/sync.py --all           # sync all adapters
    python .agent/tools/sync.py --adapter claude # sync one adapter
"""

import argparse
import json
import shutil
import sys
from pathlib import Path

import yaml


def find_project_root() -> Path:
    """Walk up from script location to find directory containing .agent/."""
    current = Path(__file__).resolve().parent
    while current != current.parent:
        if (current / ".agent").is_dir():
            return current
        current = current.parent
    # Fallback: assume two levels up from .agent/tools/
    return Path(__file__).resolve().parent.parent.parent


def load_adapter_config(config_path: Path) -> dict:
    """Load a YAML adapter config file.

    Args:
        config_path: Path to config.yml file.

    Returns:
        Parsed YAML as a dictionary.

    Raises:
        FileNotFoundError: If config_path does not exist.
    """
    if not config_path.exists():
        raise FileNotFoundError(f"Config not found: {config_path}")
    with open(config_path) as f:
        return yaml.safe_load(f)


def discover_adapters(adapters_dir: Path) -> list[Path]:
    """Find all adapter directories that contain a config.yml.

    Args:
        adapters_dir: Path to .agent/adapters/ directory.

    Returns:
        Sorted list of adapter directory paths.
    """
    if not adapters_dir.is_dir():
        return []
    result = []
    for entry in sorted(adapters_dir.iterdir()):
        if entry.is_dir() and (entry / "config.yml").exists():
            result.append(entry)
    return result


def execute_pointer(target: Path, content: str) -> str:
    """Write pointer file content to a target path.

    Args:
        target: Path where the pointer file will be written.
        content: Text content to write.

    Returns:
        Description of what was done.
    """
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content)
    return f"pointer: wrote {target}"


def execute_copy(source: Path, target: Path, glob: str) -> list[str]:
    """Copy files matching a glob pattern from source dir to target dir.

    Args:
        source: Source directory to search for files.
        target: Target directory to copy files into.
        glob: Glob pattern to match files (e.g., "*.md").

    Returns:
        List of copied file descriptions.
    """
    matching = sorted(source.glob(glob))
    if not matching:
        return []
    target.mkdir(parents=True, exist_ok=True)
    results = []
    for src_file in matching:
        if src_file.is_file():
            dest = target / src_file.name
            shutil.copy2(src_file, dest)
            results.append(f"copy: {src_file.name} -> {dest}")
    return results


def execute_copy_file(source: Path, target: Path) -> str:
    """Copy a single file from source to target.

    Args:
        source: Source file path.
        target: Target file path.

    Returns:
        Description of what was done.
    """
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)
    return f"copy_file: {source.name} -> {target}"


def execute_merge_claude_hooks(
    settings_path: Path, hooks_dir: Path, mapping: dict
) -> str:
    """Merge hook entries into Claude settings.json.

    For each mapping entry (e.g., ``session_start.py: SessionStart``), adds
    a hook command entry under the ``hooks`` key in settings.json, preserving
    all existing settings and avoiding duplicate commands.

    Args:
        settings_path: Path to .claude/settings.json (created if missing).
        hooks_dir: Directory containing hook scripts.
        mapping: Dict mapping hook filenames to Claude hook event names.

    Returns:
        Description of what was done.
    """
    settings_path.parent.mkdir(parents=True, exist_ok=True)

    # Load existing settings or start fresh
    if settings_path.exists():
        with open(settings_path) as f:
            settings = json.load(f)
    else:
        settings = {}

    # Ensure hooks key exists
    if "hooks" not in settings:
        settings["hooks"] = {}

    for hook_file, event_name in mapping.items():
        hook_path = (hooks_dir / hook_file).resolve()
        command = f'python "{hook_path}"'
        entry = {"type": "command", "command": command}

        if event_name not in settings["hooks"]:
            settings["hooks"][event_name] = []

        # Check for duplicate by comparing command field
        existing_commands = [e.get("command") for e in settings["hooks"][event_name]]
        if command not in existing_commands:
            settings["hooks"][event_name].append(entry)

    with open(settings_path, "w") as f:
        json.dump(settings, f, indent=2)

    hook_names = list(mapping.values())
    return f"merge: updated {settings_path} with hooks {hook_names}"


def execute_concatenate(source: Path, target: Path, glob: str) -> str:
    """Concatenate matching files from source into one target file.

    Files are concatenated in sorted filename order, separated by newlines.

    Args:
        source: Source directory to search for files.
        target: Target file path for concatenated output.
        glob: Glob pattern to match files.

    Returns:
        Description of what was done.
    """
    matching = sorted(source.glob(glob))
    matching = [f for f in matching if f.is_file()]

    if not matching:
        return f"concatenate: no files matching '{glob}' in {source}"

    target.parent.mkdir(parents=True, exist_ok=True)

    parts = []
    for src_file in matching:
        parts.append(src_file.read_text())

    target.write_text("\n".join(parts))
    return f"concatenate: {len(matching)} files -> {target}"


def sync_adapter(adapter_dir: Path, project_root: Path) -> list[str]:
    """Process all sync entries for one adapter.

    Args:
        adapter_dir: Path to the adapter directory (contains config.yml).
        project_root: Root directory of the project.

    Returns:
        List of result descriptions for each sync action.
    """
    config = load_adapter_config(adapter_dir / "config.yml")
    sync_entries = config.get("sync", {})
    if not sync_entries:
        return [f"skip: {adapter_dir.name} has no sync entries"]

    results = []
    for entry_name, entry in sync_entries.items():
        if not isinstance(entry, dict):
            continue

        action = entry.get("action", "none")
        target_rel = entry.get("target")

        if action == "none":
            results.append(f"skip (none): {entry_name}")
            continue

        if action == "pointer":
            content = entry.get("content", "")
            target = project_root / target_rel
            result = execute_pointer(target, content)
            results.append(result)

        elif action == "copy":
            source_rel = entry.get("source", "")
            source = project_root / source_rel
            target = project_root / target_rel
            glob_pattern = entry.get("glob")

            if glob_pattern:
                # Directory copy with glob
                copied = execute_copy(source, target, glob_pattern)
                results.extend(
                    copied
                    if copied
                    else [f"copy: no matches for {glob_pattern} in {source}"]
                )
            else:
                # Single file copy
                result = execute_copy_file(source, target)
                results.append(result)

        elif action == "merge":
            fmt = entry.get("format")
            if fmt == "claude-settings-json":
                mapping = entry.get("mapping", {})
                settings_path = project_root / target_rel
                hooks_dir = project_root / ".agent" / "hooks"
                result = execute_merge_claude_hooks(settings_path, hooks_dir, mapping)
                results.append(result)
            else:
                results.append(f"skip: unknown merge format '{fmt}'")

        elif action == "concatenate":
            source_rel = entry.get("source", "")
            source = project_root / source_rel
            target = project_root / target_rel
            glob_pattern = entry.get("glob", "*")
            result = execute_concatenate(source, target, glob_pattern)
            results.append(result)

        else:
            results.append(f"skip: unknown action '{action}' for {entry_name}")

    return results


def main() -> None:
    """CLI entrypoint with argparse."""
    parser = argparse.ArgumentParser(description="Sync agent adapter configurations.")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument(
        "--all",
        action="store_true",
        help="Sync all adapters",
    )
    group.add_argument(
        "--adapter",
        type=str,
        help="Sync a specific adapter by name",
    )
    args = parser.parse_args()

    project_root = find_project_root()
    adapters_dir = project_root / ".agent" / "adapters"

    if args.all:
        adapter_dirs = discover_adapters(adapters_dir)
        if not adapter_dirs:
            print("No adapters found.")
            sys.exit(0)
        for adapter_dir in adapter_dirs:
            print(f"\n--- Syncing {adapter_dir.name} ---")
            results = sync_adapter(adapter_dir, project_root)
            for r in results:
                print(f"  {r}")
    else:
        adapter_dir = adapters_dir / args.adapter
        if not (adapter_dir / "config.yml").exists():
            print(f"Adapter '{args.adapter}' not found at {adapter_dir}")
            sys.exit(1)
        print(f"\n--- Syncing {args.adapter} ---")
        results = sync_adapter(adapter_dir, project_root)
        for r in results:
            print(f"  {r}")

    print("\nSync complete.")


if __name__ == "__main__":
    main()
