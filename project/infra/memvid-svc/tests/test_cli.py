"""Tests for memvid CLI commands."""

import argparse
import json
import sys

import pytest
from pytest_mock import MockerFixture

from cli import cmd_create, cmd_find, cmd_info, cmd_put, cmd_timeline


@pytest.fixture
def mock_mem_sdk(mocker: MockerFixture):
    mock_create = mocker.patch("cli.memvid_sdk.create")
    mock_use = mocker.patch("cli.memvid_sdk.use")
    return mock_create, mock_use


class TestCmdCreate:
    def test_cmd_create_creates_store(self, tmp_path, mock_mem_sdk, capsys):
        mock_create, _ = mock_mem_sdk
        store_path = tmp_path / "test.mv2"
        args = argparse.Namespace(path=str(store_path))

        cmd_create(args)

        mock_create.assert_called_once_with(str(store_path))

        captured = capsys.readouterr()
        result = json.loads(captured.out)
        assert result == {"created": str(store_path)}
        assert captured.err == ""

    def test_cmd_create_exception(self, tmp_path, mock_mem_sdk, capsys):
        mock_create, _ = mock_mem_sdk
        store_path = tmp_path / "test.mv2"
        args = argparse.Namespace(path=str(store_path))
        mock_create.side_effect = OSError("Permission denied")

        with pytest.raises(SystemExit) as exc_info:
            cmd_create(args)

        assert exc_info.value.code == 1
        mock_create.assert_called_once_with(str(store_path))

        captured = capsys.readouterr()
        result = json.loads(captured.err)
        assert result == {"error": "Permission denied"}
        assert captured.out == ""


class TestCmdPut:
    def test_cmd_put_success(self, tmp_path, mock_mem_sdk, capsys, monkeypatch, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.put.return_value = "1"
        mock_use.return_value = mock_mem

        store_path = tmp_path / "test.mv2"
        args = argparse.Namespace(path=str(store_path))

        input_data = {
            "title": "Test Title",
            "label": "test-label",
            "text": "Test content here",
        }
        monkeypatch.setattr(
            sys, "stdin", mocker.MagicMock(read=lambda: json.dumps(input_data))
        )

        cmd_put(args)

        mock_use.assert_called_once_with("basic", str(store_path))
        mock_mem.put.assert_called_once_with(
            title="Test Title",
            label="test-label",
            text="Test content here",
            metadata=None,
        )

        captured = capsys.readouterr()
        result = json.loads(captured.out)
        assert result == {"frame_id": 1}
        assert captured.err == ""

    def test_cmd_put_invalid_json(
        self, tmp_path, mock_mem_sdk, capsys, monkeypatch, mocker
    ):
        mock_create, mock_use = mock_mem_sdk
        store_path = tmp_path / "test.mv2"
        args = argparse.Namespace(path=str(store_path))

        invalid_json = "{ invalid json"
        monkeypatch.setattr(sys, "stdin", mocker.MagicMock(read=lambda: invalid_json))

        with pytest.raises(SystemExit) as exc_info:
            cmd_put(args)

        assert exc_info.value.code == 1
        mock_use.assert_not_called()

        captured = capsys.readouterr()
        result = json.loads(captured.err)
        assert "error" in result
        assert "invalid JSON on stdin" in result["error"]
        assert captured.out == ""

    def test_cmd_put_missing_title(
        self, tmp_path, mock_mem_sdk, capsys, monkeypatch, mocker
    ):
        mock_create, mock_use = mock_mem_sdk
        store_path = tmp_path / "test.mv2"
        args = argparse.Namespace(path=str(store_path))

        input_data = {
            "label": "test-label",
            "text": "Test content here",
        }
        monkeypatch.setattr(
            sys, "stdin", mocker.MagicMock(read=lambda: json.dumps(input_data))
        )

        with pytest.raises(SystemExit) as exc_info:
            cmd_put(args)

        assert exc_info.value.code == 1
        mock_use.assert_not_called()

        captured = capsys.readouterr()
        result = json.loads(captured.err)
        assert result == {"error": "missing required key: title"}
        assert captured.out == ""

    def test_cmd_put_missing_label(
        self, tmp_path, mock_mem_sdk, capsys, monkeypatch, mocker
    ):
        mock_create, mock_use = mock_mem_sdk
        store_path = tmp_path / "test.mv2"
        args = argparse.Namespace(path=str(store_path))

        input_data = {
            "title": "Test Title",
            "text": "Test content here",
        }
        monkeypatch.setattr(
            sys, "stdin", mocker.MagicMock(read=lambda: json.dumps(input_data))
        )

        with pytest.raises(SystemExit) as exc_info:
            cmd_put(args)

        assert exc_info.value.code == 1
        mock_use.assert_not_called()

        captured = capsys.readouterr()
        result = json.loads(captured.err)
        assert result == {"error": "missing required key: label"}
        assert captured.out == ""

    def test_cmd_put_missing_text(
        self, tmp_path, mock_mem_sdk, capsys, monkeypatch, mocker
    ):
        mock_create, mock_use = mock_mem_sdk
        store_path = tmp_path / "test.mv2"
        args = argparse.Namespace(path=str(store_path))

        input_data = {
            "title": "Test Title",
            "label": "test-label",
        }
        monkeypatch.setattr(
            sys, "stdin", mocker.MagicMock(read=lambda: json.dumps(input_data))
        )

        with pytest.raises(SystemExit) as exc_info:
            cmd_put(args)

        assert exc_info.value.code == 1
        mock_use.assert_not_called()

        captured = capsys.readouterr()
        result = json.loads(captured.err)
        assert result == {"error": "missing required key: text"}
        assert captured.out == ""

    def test_cmd_put_sdk_exception(
        self, tmp_path, mock_mem_sdk, capsys, monkeypatch, mocker
    ):
        mock_create, mock_use = mock_mem_sdk
        mock_use.side_effect = OSError("Store not found")

        store_path = tmp_path / "test.mv2"
        args = argparse.Namespace(path=str(store_path))

        input_data = {
            "title": "Test Title",
            "label": "test-label",
            "text": "Test content here",
        }
        monkeypatch.setattr(
            sys, "stdin", mocker.MagicMock(read=lambda: json.dumps(input_data))
        )

        with pytest.raises(SystemExit) as exc_info:
            cmd_put(args)

        assert exc_info.value.code == 1
        mock_use.assert_called_once_with("basic", str(store_path))

        captured = capsys.readouterr()
        result = json.loads(captured.err)
        assert result == {"error": "Store not found"}
        assert captured.out == ""

    def test_cmd_put_with_metadata(
        self, tmp_path, mock_mem_sdk, capsys, monkeypatch, mocker
    ):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.put.return_value = "2"
        mock_use.return_value = mock_mem

        store_path = tmp_path / "test.mv2"
        args = argparse.Namespace(path=str(store_path))

        input_data = {
            "title": "Test Title",
            "label": "test-label",
            "text": "Test content here",
            "metadata": {"source": "cli", "version": "1.0"},
        }
        monkeypatch.setattr(
            sys, "stdin", mocker.MagicMock(read=lambda: json.dumps(input_data))
        )

        cmd_put(args)

        mock_use.assert_called_once_with("basic", str(store_path))
        mock_mem.put.assert_called_once_with(
            title="Test Title",
            label="test-label",
            text="Test content here",
            metadata={"source": "cli", "version": "1.0"},
        )

        captured = capsys.readouterr()
        result = json.loads(captured.out)
        assert result == {"frame_id": 2}
        assert captured.err == ""


class TestCmdFind:
    def test_cmd_find_success(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.find.return_value = {
            "hits": [
                {"title": "Test Title 1", "text": "Test content 1", "score": 0.95},
                {"title": "Test Title 2", "text": "Test content 2", "score": 0.85},
            ]
        }
        mock_use.return_value = mock_mem

        args = argparse.Namespace(
            path="/path/to/store.mv2", query="test query", k=10, mode="sem"
        )

        cmd_find(args)

        mock_use.assert_called_once_with("basic", "/path/to/store.mv2")
        mock_mem.find.assert_called_once_with("test query", k=10, mode="sem")

        captured = capsys.readouterr()
        result = json.loads(captured.out)
        assert result == {
            "results": [
                {"title": "Test Title 1", "text": "Test content 1", "score": 0.95},
                {"title": "Test Title 2", "text": "Test content 2", "score": 0.85},
            ]
        }
        assert captured.err == ""

    def test_cmd_find_empty_hits(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.find.return_value = {"hits": []}
        mock_use.return_value = mock_mem

        args = argparse.Namespace(
            path="/path/to/store.mv2", query="test query", k=5, mode="auto"
        )

        cmd_find(args)

        mock_use.assert_called_once_with("basic", "/path/to/store.mv2")
        mock_mem.find.assert_called_once_with("test query", k=5, mode="auto")

        captured = capsys.readouterr()
        result = json.loads(captured.out)
        assert result == {"results": []}
        assert captured.err == ""

    def test_cmd_find_response_without_hits(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.find.return_value = {}
        mock_use.return_value = mock_mem

        args = argparse.Namespace(
            path="/path/to/store.mv2", query="test query", k=5, mode="lex"
        )

        cmd_find(args)

        mock_use.assert_called_once_with("basic", "/path/to/store.mv2")
        mock_mem.find.assert_called_once_with("test query", k=5, mode="lex")

        captured = capsys.readouterr()
        result = json.loads(captured.out)
        assert result == {"results": []}
        assert captured.err == ""

    def test_cmd_find_sdk_exception(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_use.side_effect = OSError("Store not found")

        args = argparse.Namespace(
            path="/path/to/store.mv2", query="test query", k=5, mode="auto"
        )

        with pytest.raises(SystemExit) as exc_info:
            cmd_find(args)

        assert exc_info.value.code == 1
        mock_use.assert_called_once_with("basic", "/path/to/store.mv2")

        captured = capsys.readouterr()
        result = json.loads(captured.err)
        assert result == {"error": "Store not found"}
        assert captured.out == ""

    def test_cmd_find_find_exception(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.find.side_effect = ValueError("Invalid query")
        mock_use.return_value = mock_mem

        args = argparse.Namespace(
            path="/path/to/store.mv2", query="test query", k=5, mode="auto"
        )

        with pytest.raises(SystemExit) as exc_info:
            cmd_find(args)

        assert exc_info.value.code == 1
        mock_use.assert_called_once_with("basic", "/path/to/store.mv2")
        mock_mem.find.assert_called_once_with("test query", k=5, mode="auto")

        captured = capsys.readouterr()
        result = json.loads(captured.err)
        assert result == {"error": "Invalid query"}
        assert captured.out == ""


class TestCmdTimeline:
    def test_cmd_timeline_success(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.timeline.return_value = [
            {"title": "Entry 1", "labels": ["label1"], "preview": "Preview 1"},
            {"title": "Entry 2", "labels": ["label2"], "preview": "Preview 2"},
        ]
        mock_use.return_value = mock_mem

        args = argparse.Namespace(path="/path/to/store.mv2", limit=20)

        cmd_timeline(args)

        mock_use.assert_called_once_with("basic", "/path/to/store.mv2")
        mock_mem.timeline.assert_called_once_with(limit=20)

        captured = capsys.readouterr()
        result = json.loads(captured.out)
        assert result == {
            "entries": [
                {"title": "Entry 1", "label": ["label1"], "text": "Preview 1"},
                {"title": "Entry 2", "label": ["label2"], "text": "Preview 2"},
            ]
        }
        assert captured.err == ""

    def test_cmd_timeline_empty_entries(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.timeline.return_value = []
        mock_use.return_value = mock_mem

        args = argparse.Namespace(path="/path/to/store.mv2", limit=50)

        cmd_timeline(args)

        mock_use.assert_called_once_with("basic", "/path/to/store.mv2")
        mock_mem.timeline.assert_called_once_with(limit=50)

        captured = capsys.readouterr()
        result = json.loads(captured.out)
        assert result == {"entries": []}
        assert captured.err == ""

    def test_cmd_timeline_default_limit(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.timeline.return_value = [
            {"title": "Entry 1", "labels": ["label1"], "preview": "Preview 1"},
        ]
        mock_use.return_value = mock_mem

        args = argparse.Namespace(path="/path/to/store.mv2", limit=50)

        cmd_timeline(args)

        mock_mem.timeline.assert_called_once_with(limit=50)

        captured = capsys.readouterr()
        result = json.loads(captured.out)
        assert result == {
            "entries": [
                {"title": "Entry 1", "label": ["label1"], "text": "Preview 1"},
            ]
        }
        assert captured.err == ""

    def test_cmd_timeline_sdk_exception(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_use.side_effect = OSError("Store not found")

        args = argparse.Namespace(path="/path/to/store.mv2", limit=10)

        with pytest.raises(SystemExit) as exc_info:
            cmd_timeline(args)

        assert exc_info.value.code == 1
        mock_use.assert_called_once_with("basic", "/path/to/store.mv2")

        captured = capsys.readouterr()
        result = json.loads(captured.err)
        assert result == {"error": "Store not found"}
        assert captured.out == ""

    def test_cmd_timeline_exception(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.timeline.side_effect = ValueError("Invalid limit")
        mock_use.return_value = mock_mem

        args = argparse.Namespace(path="/path/to/store.mv2", limit=10)

        with pytest.raises(SystemExit) as exc_info:
            cmd_timeline(args)

        assert exc_info.value.code == 1
        mock_use.assert_called_once_with("basic", "/path/to/store.mv2")
        mock_mem.timeline.assert_called_once_with(limit=10)

        captured = capsys.readouterr()
        result = json.loads(captured.err)
        assert result == {"error": "Invalid limit"}
        assert captured.out == ""


class TestCmdInfo:
    def test_cmd_info_success(self, mock_mem_sdk, capsys, mocker):
        mock_create, mock_use = mock_mem_sdk
        mock_mem = mocker.MagicMock()
        mock_mem.__len__.return_value = 42
        mock_use.return_value = mock_mem

        mocker.patch("os.path.getsize", return_value=1024)

        args = argparse.Namespace(path="/path/to/store.mv2")

        cmd_info(args)

        mock_use.assert_called_once_with("basic", "/path/to/store.mv2")
        mock_mem.__len__.assert_called_once()

        captured = capsys.readouterr()
        result = json.loads(captured.out)
        assert result["path"] == "/path/to/store.mv2"
        assert result["frames"] == 42
        assert result["size_bytes"] == 1024
        assert captured.err == ""
