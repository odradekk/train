import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


RECORDER = Path(__file__).resolve().parents[1] / "game-process.py"


class GameProcessTests(unittest.TestCase):
    def record(self, command):
        with tempfile.TemporaryDirectory(prefix="train game process ") as directory:
            status = Path(directory) / "game-process.json"
            result = subprocess.run([sys.executable, str(RECORDER), str(status), sys.executable, "-c", command],
                                    capture_output=True, text=True, timeout=10)
            self.assertTrue(status.is_file(), result.stderr)
            return result.returncode, json.loads(status.read_text(encoding="utf-8"))

    def test_normal_completion_records_zero(self):
        code, status = self.record("raise SystemExit(0)")
        self.assertEqual(0, code)
        self.assertEqual({"exit_code": 0, "signal": None}, status)

    def test_nonzero_exit_is_preserved_without_a_signal(self):
        for expected in (7, 134):
            with self.subTest(exit_code=expected):
                code, status = self.record(f"raise SystemExit({expected})")
                self.assertEqual(expected, code)
                self.assertEqual({"exit_code": expected, "signal": None}, status)

    @unittest.skipUnless(os.name == "posix", "POSIX signal termination requires Linux")
    def test_signal_termination_records_the_signal_separately(self):
        code, status = self.record("import os, signal; os.kill(os.getpid(), signal.SIGTERM)")
        self.assertNotEqual(0, code)
        self.assertEqual({"exit_code": -15, "signal": {"name": "SIGTERM", "number": 15}}, status)


if __name__ == "__main__":
    unittest.main()
