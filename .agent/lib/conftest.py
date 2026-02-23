"""Ensure supermemory_client is importable when running from project root."""

import sys
from pathlib import Path

lib_dir = str(Path(__file__).resolve().parent)
if lib_dir not in sys.path:
    sys.path.insert(0, lib_dir)
