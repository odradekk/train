import json
from pathlib import Path
import signal
import subprocess
import sys


code = subprocess.run(sys.argv[2:]).returncode
termination = {"name": signal.Signals(-code).name, "number": -code} if code < 0 else None
Path(sys.argv[1]).write_text(json.dumps({"exit_code": code, "signal": termination}), encoding="utf-8")
sys.exit(code if code >= 0 else 128 - code)
