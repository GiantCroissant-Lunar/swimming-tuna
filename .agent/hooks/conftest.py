"""Ensure hook modules are importable when running from project root."""

import sys
from pathlib import Path

hooks_dir = str(Path(__file__).resolve().parent)
if hooks_dir not in sys.path:
    sys.path.insert(0, hooks_dir)
