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
    with open(config_path, encoding="utf-8") as f:
        data = yaml.safe_load(f)
    if not isinstance(data, dict):
        return {}
    return data


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

    When the glob contains path separators (e.g., ``*/SKILL.md``), the
    relative directory structure from source is preserved under target.

    Args:
        source: Source directory to search for files.
        target: Target directory to copy files into.
        glob: Glob pattern to match files (e.g., "*.md", "*/SKILL.md").

    Returns:
        List of copied file descriptions.
    """
    matching = sorted(source.glob(glob))
    if not matching:
        return []
    target.mkdir(parents=True, exist_ok=True)
    # Preserve subdirectory structure when glob spans directories
    preserve_dirs = "/" in glob or "\\" in glob
    results = []
    for src_file in matching:
        if src_file.is_file():
            if preserve_dirs:
                rel = src_file.relative_to(source)
                dest = target / rel
                dest.parent.mkdir(parents=True, exist_ok=True)
            else:
                dest = target / src_file.name
            shutil.copy2(src_file, dest)
            results.append(f"copy: {src_file.relative_to(source)} -> {dest}")
    return results


def execute_copy_file(source: Path, target: Path) -> str:
    """Copy a single file from source to target.

    Args:
        source: Source file path.
        target: Target file path.

    Returns:
        Description of what was done.
    """
    if not source.exists():
        return f"copy_file: source missing: {source}"
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)
    return f"copy_file: {source.name} -> {target}"


def execute_merge_claude_hooks(
    settings_path: Path,
    hooks_dir: Path,
    mapping: dict,
    project_root: Path | None = None,
) -> str:
    """Merge hook entries into Claude settings.json.

    For each mapping entry (e.g., ``session_start.py: SessionStart``), adds
    a hook command entry under the ``hooks`` key in settings.json, preserving
    all existing settings and avoiding duplicate commands.

    Args:
        settings_path: Path to .claude/settings.json (created if missing).
        hooks_dir: Directory containing hook scripts.
        mapping: Dict mapping hook filenames to Claude hook event names.
        project_root: If provided, hook paths are expressed relative to this
            directory so the generated file is portable across machines.

    Returns:
        Description of what was done.
    """
    settings_path.parent.mkdir(parents=True, exist_ok=True)

    # Load existing settings or start fresh
    if settings_path.exists() and settings_path.stat().st_size > 0:
        try:
            with open(settings_path) as f:
                settings = json.load(f)
        except json.JSONDecodeError:
            settings = {}
    else:
        settings = {}

    # Ensure hooks key exists
    if "hooks" not in settings:
        settings["hooks"] = {}

    for hook_file, event_name in mapping.items():
        hook_path = (hooks_dir / hook_file).resolve()
        if not hook_path.exists():
            continue
        if project_root is not None:
            try:
                rel_path = hook_path.relative_to(project_root.resolve())
                command = f'python3 "{rel_path}"'
            except ValueError:
                command = f'python3 "{hook_path}"'
        else:
            command = f'python3 "{hook_path}"'
        hook_entry = {"type": "command", "command": command}
        # Claude Code expects: { "hooks": [ { "type": "command", ... } ] }
        event_block = {"hooks": [hook_entry]}

        if event_name not in settings["hooks"]:
            settings["hooks"][event_name] = []

        # Check for duplicate by comparing command field in nested hooks
        existing_commands = [
            cmd.get("command")
            for block in settings["hooks"][event_name]
            for cmd in block.get("hooks", [])
        ]
        if command not in existing_commands:
            settings["hooks"][event_name].append(event_block)

    with open(settings_path, "w") as f:
        json.dump(settings, f, indent=2)

    hook_names = list(mapping.values())
    return f"merge: updated {settings_path} with hooks {hook_names}"

def execute_merge_kiro_hooks(
    hooks_dir: Path,
    mapping: dict,
    project_root: Path | None = None,
) -> list[str]:
    """Generate Kiro hook JSON files from the shared hook mapping.

    For each mapping entry, creates a JSON hook file in hooks_dir following
    the Kiro hook schema: ``{name, version, description, when, then}``.

    Args:
        hooks_dir: Target directory for generated hook JSON files.
        mapping: Dict mapping hook script filenames to hook config dicts.
            Each value should have: event, toolTypes (optional),
            description (optional).
        project_root: If provided, script paths are expressed relative to
            this directory.

    Returns:
        List of descriptions of what was done.
    """
    hooks_dir.mkdir(parents=True, exist_ok=True)
    results = []

    for hook_file, hook_config in mapping.items():
        if isinstance(hook_config, str):
            # Simple mapping: filename -> event name
            event_type = hook_config
            tool_types = None
            description = f"Auto-synced from {hook_file}"
        else:
            event_type = hook_config.get("event", "preToolUse")
            tool_types = hook_config.get("toolTypes")
            description = hook_config.get(
                "description", f"Auto-synced from {hook_file}"
            )

        # Build the script command path
        script_path = hooks_dir / hook_file
        if project_root is not None and script_path.exists():
            try:
                rel_path = script_path.relative_to(project_root.resolve())
                command = f"python3 {rel_path}"
            except ValueError:
                command = f"python3 {script_path}"
        else:
            command = f"python3 .kiro/hooks/{hook_file}"

        # Derive a hook name from the script filename
        hook_name = hook_file.replace(".py", "").replace("_", "-")
        target_file = hooks_dir / f"{hook_name}.json"

        hook_json = {
            "name": hook_name.replace("-", " ").title(),
            "version": "1.0.0",
            "description": description,
            "when": {"type": event_type},
            "then": {"type": "runCommand", "command": command},
        }

        if tool_types and event_type in ("preToolUse", "postToolUse"):
            hook_json["when"]["toolTypes"] = tool_types

        if event_type in ("fileEdited", "fileCreated", "fileDeleted"):
            patterns = hook_config.get("patterns") if isinstance(hook_config, dict) else None
            if patterns:
                hook_json["when"]["patterns"] = patterns

        with open(target_file, "w") as f:
            json.dump(hook_json, f, indent=2)

        results.append(f"kiro-hook: wrote {target_file}")

    return results



def execute_merge_kiro_hooks(
    hooks_dir: Path,
    mapping: dict,
    project_root: Path | None = None,
) -> list[str]:
    """Generate Kiro hook JSON files from the shared hook mapping.

    For each mapping entry, creates a JSON hook file in hooks_dir following
    the Kiro hook schema: ``{name, version, description, when, then}``.

    Args:
        hooks_dir: Target directory for generated hook JSON files.
        mapping: Dict mapping hook script filenames to hook config dicts.
            Each value should have: event, toolTypes (optional),
            description (optional).
        project_root: If provided, script paths are expressed relative to
            this directory.

    Returns:
        List of descriptions of what was done.
    """
    hooks_dir.mkdir(parents=True, exist_ok=True)
    results = []

    for hook_file, hook_config in mapping.items():
        if isinstance(hook_config, str):
            # Simple mapping: filename -> event name
            event_type = hook_config
            tool_types = None
            description = f"Auto-synced from {hook_file}"
        else:
            event_type = hook_config.get("event", "preToolUse")
            tool_types = hook_config.get("toolTypes")
            description = hook_config.get(
                "description", f"Auto-synced from {hook_file}"
            )

        # Build the script command path
        script_path = hooks_dir / hook_file
        if project_root is not None and script_path.exists():
            try:
                rel_path = script_path.relative_to(project_root.resolve())
                command = f"python3 {rel_path}"
            except ValueError:
                command = f"python3 {script_path}"
        else:
            command = f"python3 .kiro/hooks/{hook_file}"

        # Derive a hook name from the script filename
        hook_name = hook_file.replace(".py", "").replace("_", "-")
        target_file = hooks_dir / f"{hook_name}.json"

        hook_json = {
            "name": hook_name.replace("-", " ").title(),
            "version": "1.0.0",
            "description": description,
            "when": {"type": event_type},
            "then": {"type": "runCommand", "command": command},
        }

        if tool_types and event_type in ("preToolUse", "postToolUse"):
            hook_json["when"]["toolTypes"] = tool_types

        if event_type in ("fileEdited", "fileCreated", "fileDeleted"):
            patterns = (
                hook_config.get("patterns") if isinstance(hook_config, dict) else None
            )
            if patterns:
                hook_json["when"]["patterns"] = patterns

        with open(target_file, "w") as f:
            json.dump(hook_json, f, indent=2)

        results.append(f"kiro-hook: wrote {target_file}")

    return results


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

    target.write_text("\n\n".join(parts))
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

        if not target_rel:
            results.append(f"skip (missing target): {entry_name}")
            continue

        if action == "pointer":
            content = entry.get("content", "")
            target = project_root / target_rel
            result = execute_pointer(target, content)
            results.append(result)

        elif action == "copy":
            source_rel = entry.get("source", "")
            if not source_rel:
                results.append(f"skip (missing source): {entry_name}")
                continue
            source = project_root / source_rel
            target = project_root / target_rel
            glob_pattern = entry.get("glob")

            if glob_pattern or source.is_dir():
                # Directory copy with glob (default to "*" if source is a dir)
                pattern = glob_pattern or "*"
                copied = execute_copy(source, target, pattern)
                results.extend(
                    copied
                    if copied
                    else [f"copy: no matches for {pattern} in {source}"]
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
                result = execute_merge_claude_hooks(
                    settings_path, hooks_dir, mapping, project_root
                )
                results.append(result)
            elif fmt == "kiro-hooks-json":
                mapping = entry.get("mapping", {})
                hooks_dir = project_root / target_rel
                kiro_results = execute_merge_kiro_hooks(
                    hooks_dir, mapping, project_root
                )
                results.extend(kiro_results)
            else:
                results.append(f"skip: unknown merge format '{fmt}'")

        elif action == "concatenate":
            source_rel = entry.get("source", "")
            if not source_rel:
                results.append(f"skip (missing source): {entry_name}")
                continue
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
