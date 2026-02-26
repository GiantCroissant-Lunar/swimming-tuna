"""Pytest configuration for memvid-svc tests."""

import sys
from pathlib import Path
from unittest.mock import MagicMock

src_dir = Path(__file__).parent.parent / "src"
sys.path.insert(0, str(src_dir))

sys.modules["memvid_sdk"] = MagicMock()
