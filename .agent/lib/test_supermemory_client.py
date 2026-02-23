"""Tests for supermemory_client — shared Supermemory API client."""

from __future__ import annotations

import hashlib
import json
import subprocess
from pathlib import Path
from unittest import mock

import pytest

from supermemory_client import (
    combine_contexts,
    format_context,
    get_api_key,
    get_container_tag,
    get_git_root,
    get_profile,
    get_repo_container_tag,
    sanitize_repo_name,
    save_memory,
    search_memories,
)

# ---------------------------------------------------------------------------
# get_api_key
# ---------------------------------------------------------------------------


class TestGetApiKey:
    def test_env_var_takes_priority(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ):
        monkeypatch.setenv("SUPERMEMORY_API_KEY", "env_key")
        # Even if project config exists, env var wins
        cfg = tmp_path / ".claude" / ".supermemory-claude" / "config.json"
        cfg.parent.mkdir(parents=True)
        cfg.write_text(json.dumps({"apiKey": "proj_key"}))  # pragma: allowlist secret
        assert get_api_key(tmp_path) == "env_key"

    def test_project_config_apiKey(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ):
        monkeypatch.delenv("SUPERMEMORY_API_KEY", raising=False)
        cfg = tmp_path / ".claude" / ".supermemory-claude" / "config.json"
        cfg.parent.mkdir(parents=True)
        cfg.write_text(json.dumps({"apiKey": "proj_key"}))  # pragma: allowlist secret
        assert get_api_key(tmp_path) == "proj_key"

    def test_project_config_api_key_snake(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ):
        monkeypatch.delenv("SUPERMEMORY_API_KEY", raising=False)
        cfg = tmp_path / ".claude" / ".supermemory-claude" / "config.json"
        cfg.parent.mkdir(parents=True)
        cfg.write_text(json.dumps({"api_key": "snake_key"}))  # pragma: allowlist secret
        assert get_api_key(tmp_path) == "snake_key"

    def test_global_config(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.delenv("SUPERMEMORY_API_KEY", raising=False)
        monkeypatch.setattr(Path, "home", lambda: tmp_path)
        cfg = tmp_path / ".supermemory-claude" / "config.json"
        cfg.parent.mkdir(parents=True)
        cfg.write_text(json.dumps({"apiKey": "global_key"}))  # pragma: allowlist secret
        assert get_api_key(tmp_path / "project") == "global_key"

    def test_global_credentials(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.delenv("SUPERMEMORY_API_KEY", raising=False)
        monkeypatch.setattr(Path, "home", lambda: tmp_path)
        cred = tmp_path / ".supermemory-claude" / "credentials.json"
        cred.parent.mkdir(parents=True)
        cred.write_text(json.dumps({"apiKey": "cred_key"}))  # pragma: allowlist secret
        assert get_api_key(tmp_path / "project") == "cred_key"

    def test_no_key_found(self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
        monkeypatch.delenv("SUPERMEMORY_API_KEY", raising=False)
        monkeypatch.setattr(Path, "home", lambda: tmp_path)
        assert get_api_key(tmp_path) is None

    def test_malformed_json_skipped(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ):
        monkeypatch.delenv("SUPERMEMORY_API_KEY", raising=False)
        monkeypatch.setattr(Path, "home", lambda: tmp_path)
        cfg = tmp_path / ".claude" / ".supermemory-claude" / "config.json"
        cfg.parent.mkdir(parents=True)
        cfg.write_text("not json")
        assert get_api_key(tmp_path) is None


# ---------------------------------------------------------------------------
# get_git_root
# ---------------------------------------------------------------------------


class TestGetGitRoot:
    def test_returns_git_root_for_normal_repo(self, tmp_path: Path):
        """In a normal repo, --git-common-dir returns '.git'."""
        with mock.patch("supermemory_client.subprocess") as mock_sub:
            mock_sub.run.return_value = mock.Mock(
                returncode=0, stdout=".git\n", stderr=""
            )
            mock_sub.CalledProcessError = subprocess.CalledProcessError

            # When git-common-dir is '.git', fallback to --show-toplevel
            def side_effect(*args, **kwargs):
                cmd = args[0]
                if "--git-common-dir" in cmd:
                    return mock.Mock(returncode=0, stdout=".git\n", stderr="")
                if "--show-toplevel" in cmd:
                    return mock.Mock(
                        returncode=0, stdout="/home/user/repo\n", stderr=""
                    )
                return mock.Mock(returncode=1, stdout="", stderr="")

            mock_sub.run.side_effect = side_effect
            assert get_git_root(str(tmp_path)) == "/home/user/repo"

    def test_returns_parent_of_git_dir_for_worktree(self, tmp_path: Path):
        """Worktrees: --git-common-dir returns absolute path ending in .git."""
        with mock.patch("supermemory_client.subprocess") as mock_sub:
            mock_sub.CalledProcessError = subprocess.CalledProcessError

            def side_effect(*args, **kwargs):
                cmd = args[0]
                if "--git-common-dir" in cmd:
                    return mock.Mock(
                        returncode=0, stdout="/home/user/repo/.git\n", stderr=""
                    )
                return mock.Mock(returncode=1, stdout="", stderr="")

            mock_sub.run.side_effect = side_effect
            assert get_git_root(str(tmp_path)) == "/home/user/repo"

    def test_returns_none_outside_git(self, tmp_path: Path):
        with mock.patch("supermemory_client.subprocess") as mock_sub:
            mock_sub.CalledProcessError = subprocess.CalledProcessError
            mock_sub.run.return_value = mock.Mock(
                returncode=128, stdout="", stderr="fatal"
            )
            assert get_git_root(str(tmp_path)) is None

    def test_isolate_worktrees_env(
        self, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
    ):
        """With SUPERMEMORY_ISOLATE_WORKTREES=true, uses --show-toplevel directly."""
        monkeypatch.setenv("SUPERMEMORY_ISOLATE_WORKTREES", "true")
        with mock.patch("supermemory_client.subprocess") as mock_sub:
            mock_sub.CalledProcessError = subprocess.CalledProcessError

            def side_effect(*args, **kwargs):
                cmd = args[0]
                if "--show-toplevel" in cmd:
                    return mock.Mock(returncode=0, stdout="/worktree/path\n", stderr="")
                return mock.Mock(returncode=1, stdout="", stderr="")

            mock_sub.run.side_effect = side_effect
            assert get_git_root(str(tmp_path)) == "/worktree/path"


# ---------------------------------------------------------------------------
# sanitize_repo_name
# ---------------------------------------------------------------------------


class TestSanitizeRepoName:
    def test_basic(self):
        assert sanitize_repo_name("my-repo") == "my_repo"

    def test_uppercase(self):
        assert sanitize_repo_name("My-Repo") == "my_repo"

    def test_special_chars(self):
        assert sanitize_repo_name("my.repo@2024") == "my_repo_2024"

    def test_collapse_underscores(self):
        assert sanitize_repo_name("a--b__c") == "a_b_c"

    def test_strip_leading_trailing(self):
        assert sanitize_repo_name("-repo-") == "repo"


# ---------------------------------------------------------------------------
# get_container_tag / get_repo_container_tag
# ---------------------------------------------------------------------------


class TestContainerTags:
    def test_container_tag_from_git_root(self):
        with mock.patch(
            "supermemory_client.get_git_root", return_value="/home/user/repo"
        ):
            with mock.patch(
                "supermemory_client._load_project_config", return_value=None
            ):
                tag = get_container_tag("/home/user/repo")
                expected_hash = hashlib.sha256(b"/home/user/repo").hexdigest()[:16]
                assert tag == f"claudecode_project_{expected_hash}"

    def test_container_tag_fallback_to_cwd(self):
        with mock.patch("supermemory_client.get_git_root", return_value=None):
            with mock.patch(
                "supermemory_client._load_project_config", return_value=None
            ):
                tag = get_container_tag("/some/dir")
                expected_hash = hashlib.sha256(b"/some/dir").hexdigest()[:16]
                assert tag == f"claudecode_project_{expected_hash}"

    def test_container_tag_override(self):
        cfg = {"personalContainerTag": "custom_tag"}
        with mock.patch("supermemory_client.get_git_root", return_value="/repo"):
            with mock.patch(
                "supermemory_client._load_project_config", return_value=cfg
            ):
                assert get_container_tag("/repo") == "custom_tag"

    def test_repo_container_tag_from_origin(self):
        with mock.patch("supermemory_client.get_git_root", return_value="/repo"):
            with mock.patch(
                "supermemory_client._load_project_config", return_value=None
            ):
                with mock.patch(
                    "supermemory_client._get_git_repo_name",
                    return_value="swimming-tuna",
                ):
                    tag = get_repo_container_tag("/repo")
                    assert tag == "repo_swimming_tuna"

    def test_repo_container_tag_fallback_dirname(self):
        with mock.patch(
            "supermemory_client.get_git_root", return_value="/home/user/my-project"
        ):
            with mock.patch(
                "supermemory_client._load_project_config", return_value=None
            ):
                with mock.patch(
                    "supermemory_client._get_git_repo_name", return_value=None
                ):
                    tag = get_repo_container_tag("/home/user/my-project")
                    assert tag == "repo_my_project"

    def test_repo_container_tag_override(self):
        cfg = {"repoContainerTag": "custom_repo"}
        with mock.patch("supermemory_client.get_git_root", return_value="/repo"):
            with mock.patch(
                "supermemory_client._load_project_config", return_value=cfg
            ):
                assert get_repo_container_tag("/repo") == "custom_repo"


# ---------------------------------------------------------------------------
# get_profile
# ---------------------------------------------------------------------------


class TestGetProfile:
    def test_success(self):
        response_body = json.dumps(
            {
                "profile": "User prefers TDD",
                "memories": [{"content": "fact1"}],
            }
        ).encode()

        mock_resp = mock.MagicMock()
        mock_resp.read.return_value = response_body
        mock_resp.__enter__ = mock.Mock(return_value=mock_resp)
        mock_resp.__exit__ = mock.Mock(return_value=False)

        with mock.patch(
            "supermemory_client.urllib.request.urlopen", return_value=mock_resp
        ):
            result = get_profile("key123", "tag1", "session start")
            assert result["profile"] == "User prefers TDD"

    def test_network_error_returns_none(self):
        with mock.patch(
            "supermemory_client.urllib.request.urlopen",
            side_effect=Exception("network fail"),
        ):
            assert get_profile("key", "tag", "query") is None

    def test_timeout_returns_none(self):
        import urllib.error

        with mock.patch(
            "supermemory_client.urllib.request.urlopen",
            side_effect=urllib.error.URLError("timeout"),
        ):
            assert get_profile("key", "tag", "query") is None


# ---------------------------------------------------------------------------
# search_memories
# ---------------------------------------------------------------------------


class TestSearchMemories:
    def test_success(self):
        response_body = json.dumps(
            {
                "results": [{"content": "memory1", "similarity": 0.9}],
            }
        ).encode()

        mock_resp = mock.MagicMock()
        mock_resp.read.return_value = response_body
        mock_resp.__enter__ = mock.Mock(return_value=mock_resp)
        mock_resp.__exit__ = mock.Mock(return_value=False)

        with mock.patch(
            "supermemory_client.urllib.request.urlopen", return_value=mock_resp
        ):
            result = search_memories("key", "query", "tag")
            assert len(result["results"]) == 1

    def test_network_error_returns_none(self):
        with mock.patch(
            "supermemory_client.urllib.request.urlopen",
            side_effect=Exception("fail"),
        ):
            assert search_memories("key", "query", "tag") is None


# ---------------------------------------------------------------------------
# save_memory
# ---------------------------------------------------------------------------


class TestSaveMemory:
    def test_success(self):
        response_body = json.dumps({"id": "mem_123"}).encode()

        mock_resp = mock.MagicMock()
        mock_resp.read.return_value = response_body
        mock_resp.__enter__ = mock.Mock(return_value=mock_resp)
        mock_resp.__exit__ = mock.Mock(return_value=False)

        with mock.patch(
            "supermemory_client.urllib.request.urlopen", return_value=mock_resp
        ) as mock_open:
            result = save_memory("key", "content", "tag", {"type": "session"})
            assert result["id"] == "mem_123"
            # Verify request was made with correct data
            call_args = mock_open.call_args
            req = call_args[0][0]
            body = json.loads(req.data)
            assert body["content"] == "content"
            assert body["containerTags"] == ["tag"]
            assert body["metadata"]["type"] == "session"

    def test_network_error_returns_none(self):
        with mock.patch(
            "supermemory_client.urllib.request.urlopen",
            side_effect=Exception("fail"),
        ):
            assert save_memory("key", "content", "tag") is None


# ---------------------------------------------------------------------------
# format_context / combine_contexts
# ---------------------------------------------------------------------------


class TestFormatContext:
    def test_formats_profile_result(self):
        result = {"profile": "User likes TDD", "memories": []}
        out = format_context(result, "Personal Memories")
        assert "### Personal Memories" in out
        assert "User likes TDD" in out

    def test_none_returns_empty(self):
        assert format_context(None, "test") == ""

    def test_empty_profile(self):
        result = {"profile": "", "memories": []}
        assert format_context(result, "test") == ""


class TestCombineContexts:
    def test_combines_personal_and_repo(self):
        personal = "### Personal Memories\nfact1"
        repo = "### Project Knowledge\nfact2"
        combined = combine_contexts(personal, repo)
        assert "<supermemory-context>" in combined
        assert "fact1" in combined
        assert "fact2" in combined
        assert "</supermemory-context>" in combined

    def test_empty_inputs(self):
        assert combine_contexts("", "") == ""

    def test_single_input(self):
        combined = combine_contexts("### Personal\ndata", "")
        assert "<supermemory-context>" in combined
        assert "data" in combined
