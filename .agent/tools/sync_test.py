"""Tests for the agent sync engine (.agent/tools/sync.py).

Uses TDD: tests are written first, then the implementation.
All tests use tempfile.mkdtemp() for filesystem isolation.
"""

import json
import shutil
import sys
import tempfile
from pathlib import Path

import pytest

# Allow importing sync.py from the same directory
sys.path.insert(0, str(Path(__file__).parent))

from sync import (
    discover_adapters,
    execute_concatenate,
    execute_copy,
    execute_copy_file,
    execute_merge_claude_hooks,
    execute_pointer,
    load_adapter_config,
    sync_adapter,
)


class TestLoadAdapterConfig:
    """Test loading adapter YAML configs."""

    def setup_method(self):
        self.tmpdir = Path(tempfile.mkdtemp())

    def teardown_method(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_load_valid_config(self):
        """Load a valid YAML config and verify returned dict."""
        config_path = self.tmpdir / "config.yml"
        config_path.write_text(
            "name: test-adapter\n"
            "description: A test adapter\n"
            "sync:\n"
            "  rules:\n"
            "    target: .test/rules/\n"
            "    action: copy\n"
        )
        result = load_adapter_config(config_path)
        assert result["name"] == "test-adapter"
        assert result["description"] == "A test adapter"
        assert result["sync"]["rules"]["action"] == "copy"

    def test_raise_on_missing_config(self):
        """Raise FileNotFoundError when config file does not exist."""
        missing = self.tmpdir / "nonexistent" / "config.yml"
        with pytest.raises(FileNotFoundError):
            load_adapter_config(missing)

    def test_load_config_with_content_field(self):
        """Load config that has a multi-line content field (pointer action)."""
        config_path = self.tmpdir / "config.yml"
        config_path.write_text(
            "name: pointer-adapter\n"
            "sync:\n"
            "  agents_md:\n"
            "    target: CLAUDE.md\n"
            "    action: pointer\n"
            "    content: |\n"
            "      # Title\n"
            "      Some content here.\n"
        )
        result = load_adapter_config(config_path)
        assert result["sync"]["agents_md"]["action"] == "pointer"
        assert "# Title" in result["sync"]["agents_md"]["content"]


class TestDiscoverAdapters:
    """Test discovering adapter directories."""

    def setup_method(self):
        self.tmpdir = Path(tempfile.mkdtemp())

    def teardown_method(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_finds_all_adapter_dirs_with_config(self):
        """Discover all subdirectories that contain config.yml."""
        for name in ["claude", "codex", "cline"]:
            adapter_dir = self.tmpdir / name
            adapter_dir.mkdir()
            (adapter_dir / "config.yml").write_text(f"name: {name}\n")

        result = discover_adapters(self.tmpdir)
        names = sorted([p.name for p in result])
        assert names == ["claude", "cline", "codex"]

    def test_ignores_dirs_without_config(self):
        """Skip directories that have no config.yml."""
        (self.tmpdir / "with_config").mkdir()
        (self.tmpdir / "with_config" / "config.yml").write_text("name: ok\n")
        (self.tmpdir / "without_config").mkdir()

        result = discover_adapters(self.tmpdir)
        assert len(result) == 1
        assert result[0].name == "with_config"

    def test_empty_directory(self):
        """Return empty list for empty adapters directory."""
        result = discover_adapters(self.tmpdir)
        assert result == []

    def test_nonexistent_directory(self):
        """Return empty list if adapters directory does not exist."""
        result = discover_adapters(self.tmpdir / "nonexistent")
        assert result == []


class TestPointerAction:
    """Test execute_pointer: write content to a target file."""

    def setup_method(self):
        self.tmpdir = Path(tempfile.mkdtemp())

    def teardown_method(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_creates_file_with_content(self):
        """Create a new pointer file with specified content."""
        target = self.tmpdir / "CLAUDE.md"
        content = "# Claude Code\n\nSee AGENTS.md for guidance.\n"
        result = execute_pointer(target, content)
        assert target.exists()
        assert target.read_text() == content
        assert "CLAUDE.md" in result

    def test_overwrites_existing_file(self):
        """Overwrite an existing file with new pointer content."""
        target = self.tmpdir / "CLAUDE.md"
        target.write_text("old content")
        new_content = "new pointer content\n"
        execute_pointer(target, new_content)
        assert target.read_text() == new_content

    def test_creates_parent_directories(self):
        """Create parent directories if they don't exist."""
        target = self.tmpdir / "deep" / "nested" / "file.md"
        content = "nested content\n"
        execute_pointer(target, content)
        assert target.exists()
        assert target.read_text() == content


class TestCopyAction:
    """Test execute_copy: copy files matching a glob pattern."""

    def setup_method(self):
        self.tmpdir = Path(tempfile.mkdtemp())
        self.source_dir = self.tmpdir / "source"
        self.source_dir.mkdir()
        self.target_dir = self.tmpdir / "target"

    def teardown_method(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_copies_matching_glob(self):
        """Copy files that match the glob pattern from source to target."""
        (self.source_dir / "rules.md").write_text("# Rules")
        (self.source_dir / "guide.md").write_text("# Guide")
        (self.source_dir / "notes.txt").write_text("notes")

        result = execute_copy(self.source_dir, self.target_dir, "*.md")

        assert (self.target_dir / "rules.md").exists()
        assert (self.target_dir / "guide.md").exists()
        assert not (self.target_dir / "notes.txt").exists()
        assert len(result) == 2

    def test_skips_nonmatching_files(self):
        """Non-matching files are not copied."""
        (self.source_dir / "data.json").write_text("{}")
        (self.source_dir / "script.py").write_text("pass")

        result = execute_copy(self.source_dir, self.target_dir, "*.md")
        assert result == []
        # target_dir may or may not be created; no md files should exist
        if self.target_dir.exists():
            assert list(self.target_dir.iterdir()) == []

    def test_creates_target_directory(self):
        """Target directory is created if it doesn't exist."""
        (self.source_dir / "file.md").write_text("content")
        assert not self.target_dir.exists()
        execute_copy(self.source_dir, self.target_dir, "*.md")
        assert self.target_dir.exists()

    def test_overwrites_existing_files(self):
        """Overwrite files that already exist in target."""
        (self.source_dir / "file.md").write_text("new content")
        self.target_dir.mkdir(parents=True)
        (self.target_dir / "file.md").write_text("old content")

        execute_copy(self.source_dir, self.target_dir, "*.md")
        assert (self.target_dir / "file.md").read_text() == "new content"


class TestCopyFileAction:
    """Test execute_copy_file: copy a single file."""

    def setup_method(self):
        self.tmpdir = Path(tempfile.mkdtemp())

    def teardown_method(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_copies_single_file(self):
        """Copy one file from source to target."""
        source = self.tmpdir / "source.md"
        source.write_text("# Content")
        target = self.tmpdir / "dest" / "target.md"

        result = execute_copy_file(source, target)
        assert target.exists()
        assert target.read_text() == "# Content"
        assert "target.md" in result

    def test_overwrites_existing_target(self):
        """Overwrite an existing target file."""
        source = self.tmpdir / "source.md"
        source.write_text("new")
        target = self.tmpdir / "target.md"
        target.write_text("old")

        execute_copy_file(source, target)
        assert target.read_text() == "new"


class TestMergeAction:
    """Test execute_merge_claude_hooks: merge hooks into settings.json."""

    def setup_method(self):
        self.tmpdir = Path(tempfile.mkdtemp())
        self.hooks_dir = self.tmpdir / "hooks"
        self.hooks_dir.mkdir()
        # Create hook files
        (self.hooks_dir / "session_start.py").write_text("# session start hook")
        (self.hooks_dir / "session_end.py").write_text("# session end hook")
        (self.hooks_dir / "pre_tool_use.py").write_text("# pre tool use hook")

    def teardown_method(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_creates_settings_with_hooks(self):
        """Create a new settings.json with hook entries."""
        settings_path = self.tmpdir / "settings.json"
        mapping = {
            "session_start.py": "SessionStart",
            "session_end.py": "Stop",
        }

        execute_merge_claude_hooks(settings_path, self.hooks_dir, mapping)

        data = json.loads(settings_path.read_text())
        assert "hooks" in data
        assert "SessionStart" in data["hooks"]
        assert "Stop" in data["hooks"]

        # Verify command format uses absolute path
        commands = data["hooks"]["SessionStart"]
        assert len(commands) == 1
        assert commands[0]["type"] == "command"
        hook_path = str(self.hooks_dir / "session_start.py")
        assert hook_path in commands[0]["command"]

    def test_preserves_existing_settings(self):
        """Preserve other keys in existing settings.json."""
        settings_path = self.tmpdir / "settings.json"
        existing = {
            "model": "claude-opus-4-6",
            "permissions": {"allow": ["read"]},
        }
        settings_path.write_text(json.dumps(existing))

        mapping = {"session_start.py": "SessionStart"}
        execute_merge_claude_hooks(settings_path, self.hooks_dir, mapping)

        data = json.loads(settings_path.read_text())
        assert data["model"] == "claude-opus-4-6"
        assert data["permissions"] == {"allow": ["read"]}
        assert "SessionStart" in data["hooks"]

    def test_preserves_existing_hooks(self):
        """Preserve existing hook entries that are not in the mapping."""
        settings_path = self.tmpdir / "settings.json"
        existing = {
            "hooks": {"CustomHook": [{"type": "command", "command": "echo custom"}]}
        }
        settings_path.write_text(json.dumps(existing))

        mapping = {"session_start.py": "SessionStart"}
        execute_merge_claude_hooks(settings_path, self.hooks_dir, mapping)

        data = json.loads(settings_path.read_text())
        assert "CustomHook" in data["hooks"]
        assert "SessionStart" in data["hooks"]

    def test_avoids_duplicate_commands(self):
        """Do not add duplicate command entries for the same hook."""
        settings_path = self.tmpdir / "settings.json"
        hook_path = str((self.hooks_dir / "session_start.py").resolve())
        existing = {
            "hooks": {
                "SessionStart": [
                    {
                        "type": "command",
                        "command": f'python3 "{hook_path}"',
                    }
                ]
            }
        }
        settings_path.write_text(json.dumps(existing))

        mapping = {"session_start.py": "SessionStart"}
        execute_merge_claude_hooks(settings_path, self.hooks_dir, mapping)

        data = json.loads(settings_path.read_text())
        # Should still be exactly one entry, not duplicated
        assert len(data["hooks"]["SessionStart"]) == 1

    def test_creates_parent_directory(self):
        """Create parent directories for settings.json if needed."""
        settings_path = self.tmpdir / "deep" / "nested" / "settings.json"
        mapping = {"session_start.py": "SessionStart"}
        execute_merge_claude_hooks(settings_path, self.hooks_dir, mapping)
        assert settings_path.exists()


class TestConcatenateAction:
    """Test execute_concatenate: join matching files into one target."""

    def setup_method(self):
        self.tmpdir = Path(tempfile.mkdtemp())
        self.source_dir = self.tmpdir / "source"
        self.source_dir.mkdir()

    def teardown_method(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def test_concatenates_matching_files(self):
        """Join all matching files into the target file."""
        (self.source_dir / "aaa_first.md").write_text("# First\n")
        (self.source_dir / "bbb_second.md").write_text("# Second\n")
        (self.source_dir / "ignore.txt").write_text("ignored")

        target = self.tmpdir / "output.md"
        result = execute_concatenate(self.source_dir, target, "*.md")

        content = target.read_text()
        assert "# First" in content
        assert "# Second" in content
        assert "ignored" not in content
        assert "output.md" in result

    def test_files_concatenated_in_sorted_order(self):
        """Files are concatenated in sorted filename order."""
        (self.source_dir / "02_second.md").write_text("SECOND")
        (self.source_dir / "01_first.md").write_text("FIRST")
        (self.source_dir / "03_third.md").write_text("THIRD")

        target = self.tmpdir / "output.md"
        execute_concatenate(self.source_dir, target, "*.md")

        content = target.read_text()
        first_pos = content.index("FIRST")
        second_pos = content.index("SECOND")
        third_pos = content.index("THIRD")
        assert first_pos < second_pos < third_pos

    def test_creates_parent_directories(self):
        """Create parent directories for target if needed."""
        (self.source_dir / "file.md").write_text("content")
        target = self.tmpdir / "deep" / "nested" / "output.md"
        execute_concatenate(self.source_dir, target, "*.md")
        assert target.exists()

    def test_no_matching_files(self):
        """Return message when no files match the glob."""
        target = self.tmpdir / "output.md"
        result = execute_concatenate(self.source_dir, target, "*.md")
        assert "no files" in result.lower() or "0" in result


class TestSyncAdapter:
    """Test sync_adapter: process all sync entries for one adapter."""

    def setup_method(self):
        self.tmpdir = Path(tempfile.mkdtemp())
        self.project_root = self.tmpdir / "project"
        self.project_root.mkdir()
        self.adapters_dir = self.project_root / ".agent" / "adapters"
        self.adapters_dir.mkdir(parents=True)

    def teardown_method(self):
        shutil.rmtree(self.tmpdir, ignore_errors=True)

    def _make_adapter(self, name, config_text):
        """Helper to create an adapter dir with config."""
        adapter_dir = self.adapters_dir / name
        adapter_dir.mkdir(parents=True, exist_ok=True)
        (adapter_dir / "config.yml").write_text(config_text)
        return adapter_dir

    def test_sync_pointer_action(self):
        """sync_adapter processes pointer action correctly."""
        adapter_dir = self._make_adapter(
            "test",
            "name: test\n"
            "sync:\n"
            "  readme:\n"
            "    target: README.md\n"
            "    action: pointer\n"
            "    content: |\n"
            "      # Hello World\n",
        )
        results = sync_adapter(adapter_dir, self.project_root)
        assert len(results) > 0
        assert (self.project_root / "README.md").exists()

    def test_sync_none_action(self):
        """sync_adapter skips 'none' actions."""
        adapter_dir = self._make_adapter(
            "test",
            "name: test\n"
            "sync:\n"
            "  skills:\n"
            "    target: .agent/skills/\n"
            "    action: none\n",
        )
        results = sync_adapter(adapter_dir, self.project_root)
        # Should have a skip message
        assert any("skip" in r.lower() or "none" in r.lower() for r in results)

    def test_sync_copy_action(self):
        """sync_adapter processes copy action with glob."""
        # Set up source files
        rules_dir = self.project_root / ".agent" / "rules"
        rules_dir.mkdir(parents=True)
        (rules_dir / "conv.md").write_text("# Conventions")
        (rules_dir / "sec.md").write_text("# Security")

        adapter_dir = self._make_adapter(
            "test",
            "name: test\n"
            "sync:\n"
            "  rules:\n"
            "    target: .test/rules/\n"
            "    action: copy\n"
            "    source: .agent/rules/\n"
            '    glob: "*.md"\n',
        )
        sync_adapter(adapter_dir, self.project_root)
        assert (self.project_root / ".test" / "rules" / "conv.md").exists()
        assert (self.project_root / ".test" / "rules" / "sec.md").exists()

    def test_sync_copy_single_file(self):
        """sync_adapter copies a single file when source is a file (no glob)."""
        agents_md = self.project_root / "AGENTS.md"
        agents_md.write_text("# AGENTS")

        adapter_dir = self._make_adapter(
            "test",
            "name: test\n"
            "sync:\n"
            "  agents_md:\n"
            "    target: .clinerules/AGENTS.md\n"
            "    action: copy\n"
            "    source: AGENTS.md\n",
        )
        sync_adapter(adapter_dir, self.project_root)
        assert (self.project_root / ".clinerules" / "AGENTS.md").exists()
        assert (
            self.project_root / ".clinerules" / "AGENTS.md"
        ).read_text() == "# AGENTS"

    def test_sync_copy_directory_without_glob(self):
        """sync_adapter auto-copies all files when source is a directory with no glob."""
        hooks_dir = self.project_root / ".agent" / "hooks"
        hooks_dir.mkdir(parents=True)
        (hooks_dir / "start.py").write_text("# start")
        (hooks_dir / "end.py").write_text("# end")

        adapter_dir = self._make_adapter(
            "test",
            "name: test\n"
            "sync:\n"
            "  hooks:\n"
            "    target: .test/hooks/\n"
            "    action: copy\n"
            "    source: .agent/hooks/\n",
        )
        sync_adapter(adapter_dir, self.project_root)
        assert (self.project_root / ".test" / "hooks" / "start.py").exists()
        assert (self.project_root / ".test" / "hooks" / "end.py").exists()
