"""Tests for session_start and session_end hooks."""

from __future__ import annotations

import json
from io import StringIO
from pathlib import Path
from unittest import mock

import pytest


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _run_hook(hook_module, stdin_data: dict | str = "") -> dict:
    """Run a hook's main() with fake stdin and capture stdout JSON."""
    if isinstance(stdin_data, dict):
        stdin_data = json.dumps(stdin_data)
    with mock.patch("sys.stdin", StringIO(stdin_data)):
        with mock.patch("sys.stdout", new_callable=StringIO) as mock_out:
            hook_module.main()
            return json.loads(mock_out.getvalue())


# ---------------------------------------------------------------------------
# Config loading
# ---------------------------------------------------------------------------


class TestConfigLoading:
    def test_missing_config_means_disabled(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ):
        """No config.json → supermemory disabled (safe default)."""
        # Import the hook module with a patched __file__ so it looks for
        # config.json in tmp_path
        monkeypatch.setattr(
            "session_start._get_config_path", lambda: tmp_path / "config.json"
        )
        import session_start

        result = _run_hook(session_start, {"session_id": "s1"})
        assert result["result"] == "ok"

    def test_disabled_config_skips_supermemory(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ):
        config_path = tmp_path / "config.json"
        config_path.write_text(
            json.dumps(
                {
                    "adapter": "claude",
                    "supermemory": {"enabled": False},
                }
            )
        )
        monkeypatch.setattr("session_start._get_config_path", lambda: config_path)
        import session_start

        result = _run_hook(session_start, {"session_id": "s2"})
        assert result["result"] == "ok"

    def test_enabled_config_triggers_supermemory(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ):
        config_path = tmp_path / "config.json"
        config_path.write_text(
            json.dumps(
                {
                    "adapter": "cline",
                    "supermemory": {"enabled": True},
                }
            )
        )
        monkeypatch.setattr("session_start._get_config_path", lambda: config_path)
        import session_start

        # Mock the supermemory client functions
        with mock.patch.object(
            session_start,
            "_load_supermemory_context",
            return_value="<supermemory-context>test</supermemory-context>",
        ):
            result = _run_hook(session_start, {"session_id": "s3"})
            assert result["result"] == "ok"
            assert "supermemory-context" in result.get("additionalContext", "")


# ---------------------------------------------------------------------------
# session_start
# ---------------------------------------------------------------------------


class TestSessionStart:
    def test_returns_ok_with_session_id(
        self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
    ):
        config_path = tmp_path / "config.json"
        config_path.write_text(json.dumps({"supermemory": {"enabled": False}}))
        monkeypatch.setattr("session_start._get_config_path", lambda: config_path)
        import session_start

        result = _run_hook(session_start, {"session_id": "abc"})
        assert result["result"] == "ok"
        assert result["session_id"] == "abc"

    def test_handles_empty_stdin(self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
        config_path = tmp_path / "config.json"
        config_path.write_text(json.dumps({"supermemory": {"enabled": False}}))
        monkeypatch.setattr("session_start._get_config_path", lambda: config_path)
        import session_start

        result = _run_hook(session_start, "")
        assert result["result"] == "ok"

    def test_supermemory_enabled_adds_context(
        self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
    ):
        config_path = tmp_path / "config.json"
        config_path.write_text(
            json.dumps(
                {
                    "adapter": "cline",
                    "supermemory": {"enabled": True},
                }
            )
        )
        monkeypatch.setattr("session_start._get_config_path", lambda: config_path)
        import session_start

        context = "<supermemory-context>\nmemories here\n</supermemory-context>"
        with mock.patch.object(
            session_start, "_load_supermemory_context", return_value=context
        ):
            result = _run_hook(session_start, {"session_id": "s4"})
            assert result["result"] == "ok"
            assert result["additionalContext"] == context

    def test_supermemory_error_still_returns_ok(
        self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
    ):
        config_path = tmp_path / "config.json"
        config_path.write_text(
            json.dumps(
                {
                    "adapter": "cline",
                    "supermemory": {"enabled": True},
                }
            )
        )
        monkeypatch.setattr("session_start._get_config_path", lambda: config_path)
        import session_start

        with mock.patch.object(
            session_start, "_load_supermemory_context", side_effect=Exception("boom")
        ):
            result = _run_hook(session_start, {"session_id": "s5"})
            assert result["result"] == "ok"

    def test_no_context_returned(self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
        config_path = tmp_path / "config.json"
        config_path.write_text(
            json.dumps(
                {
                    "adapter": "cline",
                    "supermemory": {"enabled": True},
                }
            )
        )
        monkeypatch.setattr("session_start._get_config_path", lambda: config_path)
        import session_start

        with mock.patch.object(
            session_start, "_load_supermemory_context", return_value=""
        ):
            result = _run_hook(session_start, {"session_id": "s6"})
            assert result["result"] == "ok"
            assert "additionalContext" not in result


# ---------------------------------------------------------------------------
# session_end
# ---------------------------------------------------------------------------


class TestSessionEnd:
    def test_returns_ok_disabled(self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
        config_path = tmp_path / "config.json"
        config_path.write_text(json.dumps({"supermemory": {"enabled": False}}))
        monkeypatch.setattr("session_end._get_config_path", lambda: config_path)
        import session_end

        result = _run_hook(session_end, {"session_id": "s1"})
        assert result["result"] == "ok"

    def test_handles_empty_stdin(self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
        config_path = tmp_path / "config.json"
        config_path.write_text(json.dumps({"supermemory": {"enabled": False}}))
        monkeypatch.setattr("session_end._get_config_path", lambda: config_path)
        import session_end

        result = _run_hook(session_end, "")
        assert result["result"] == "ok"

    def test_supermemory_enabled_saves(
        self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
    ):
        config_path = tmp_path / "config.json"
        config_path.write_text(
            json.dumps(
                {
                    "adapter": "cline",
                    "supermemory": {"enabled": True},
                }
            )
        )
        monkeypatch.setattr("session_end._get_config_path", lambda: config_path)
        import session_end

        with mock.patch.object(
            session_end, "_save_session_memory", return_value=True
        ) as mock_save:
            result = _run_hook(
                session_end,
                {
                    "session_id": "s2",
                    "transcript_path": "/tmp/transcript.json",
                },
            )
            assert result["result"] == "ok"
            mock_save.assert_called_once()

    def test_supermemory_save_error_still_returns_ok(
        self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
    ):
        config_path = tmp_path / "config.json"
        config_path.write_text(
            json.dumps(
                {
                    "adapter": "cline",
                    "supermemory": {"enabled": True},
                }
            )
        )
        monkeypatch.setattr("session_end._get_config_path", lambda: config_path)
        import session_end

        with mock.patch.object(
            session_end, "_save_session_memory", side_effect=Exception("boom")
        ):
            result = _run_hook(session_end, {"session_id": "s3"})
            assert result["result"] == "ok"

    def test_no_transcript_saves_marker(
        self, monkeypatch: pytest.MonkeyPatch, tmp_path: Path
    ):
        config_path = tmp_path / "config.json"
        config_path.write_text(
            json.dumps(
                {
                    "adapter": "cline",
                    "supermemory": {"enabled": True},
                }
            )
        )
        monkeypatch.setattr("session_end._get_config_path", lambda: config_path)
        import session_end

        with mock.patch.object(
            session_end, "_save_session_memory", return_value=True
        ) as mock_save:
            result = _run_hook(session_end, {"session_id": "s4"})
            assert result["result"] == "ok"
            mock_save.assert_called_once()
