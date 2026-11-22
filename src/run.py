#!/usr/bin/env python3
""" 
Wykonuje skrypty w wirtualnym środowisku po zainstalowaniu brakujących zależności
--fetch pobiera dane
--clear usuwa dane z redisa
"""
import subprocess
import sys
import venv
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPTS_DIR = HERE / "Scripts"
VENV_DIR = HERE / ".venv"

# Selector flag -> script (change filenames to yours)
TARGETS = {
    "--fetch": SCRIPTS_DIR / "kinowo_scraper_dedup.py",
    "--clear": SCRIPTS_DIR / "clear_kinowo_redis.py",
}

# Where the script runs: True = the folder you ran the command from
# (so relative paths in your arguments work), False = the Scripts folder.
RUN_FROM_CALLER_CWD = True

argv = sys.argv[1:]
if not argv or argv[0] not in TARGETS:
    sys.exit(
        "Usage: python run.py (--fetch | --clear) [script arguments...]\n"
        "Everything after the first flag is passed to the script unchanged."
    )

selector, script_args = argv[0], argv[1:]
target = TARGETS[selector]

python = VENV_DIR / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")

# Create the venv on first run
if not python.exists():
    print("Creating virtual environment...", file=sys.stderr)
    venv.create(VENV_DIR, with_pip=True)

# Install dependencies if present
req = HERE / "requirements.txt"
if req.exists():
    subprocess.check_call([str(python), "-m", "pip", "install", "-q", "-r", str(req)])

# Run the script, forwarding all remaining arguments verbatim
try:
    sys.exit(
        subprocess.call(
            [str(python), str(target), *script_args],
            cwd=None if RUN_FROM_CALLER_CWD else SCRIPTS_DIR,
        )
    )
except KeyboardInterrupt:
    sys.exit(130)