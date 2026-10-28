import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from scene_scale_run import ScaleRun, input_identity, read, tree_identity, write

SCRIPT = Path(__file__).resolve().parents[1] / "scene-scale.py"


class ScaleCommandTests(unittest.TestCase):
    def test_timeout_preserves_bytes_text_and_absent_streams(self):
        for stdout, stderr in ((b"first", b"second"), ("first", "second"), (None, "second")):
            with self.subTest(stdout=stdout), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                run = ScaleRun(directory, directory, "Release", "generate")
                with patch("subprocess.run", side_effect=subprocess.TimeoutExpired("probe", 1, stdout, stderr)):
                    with self.assertRaises(subprocess.TimeoutExpired):
                        run.command(["probe"], "10x-stats", 1)
                self.assertEqual(("" if stdout is None else "first") + "second",
                                 (directory / "10x-stats.log").read_text(encoding="utf-8"))
                self.assertEqual("10x-stats", run.manifest["stage"])

    def test_nonzero_probe_preserves_json_diagnostics_and_failure_name(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            run = ScaleRun(directory, directory, "Release", "verify")
            with self.assertRaisesRegex(RuntimeError, "1x-local96_failed"):
                run.command([sys.executable, "-c", 'print(\'{"outcome":"unknown"}\'); raise SystemExit(1)'],
                            "1x-local96", 10, json_output=True)
            self.assertEqual({"outcome": "unknown"}, read(directory / "1x-local96.json"))
            self.assertEqual("1x-local96.json", run.manifest["failed_observation"])

    def test_real_timeout_keeps_output_written_before_termination(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            run = ScaleRun(directory, directory, "Release", "generate")
            with self.assertRaises(subprocess.TimeoutExpired):
                run.command([sys.executable, "-u", "-c", 'import time; print("started"); time.sleep(30)'],
                            "probe", 1)
            self.assertIn("started", (directory / "probe.log").read_text(encoding="utf-8"))

    def test_tree_identity_uses_sorted_relative_names_and_actual_file_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "z").write_bytes(b"z\r\n")
            (directory / "a").write_bytes(b"a\n")
            expected = {name: hashlib.sha256(value).hexdigest() for name, value in (("a", b"a\n"), ("z", b"z\r\n"))}
            digest = hashlib.sha256("".join(name + "\0" + value + "\n" for name, value in expected.items()).encode()).hexdigest()
            self.assertEqual({"sha256": digest, "files": expected}, tree_identity(directory))

    def test_verify_refuses_incomplete_or_modified_generation_before_building(self):
        for failure in ("generation_incomplete", "recipe_identity_changed", "input_identity_changed"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                inputs, output = directory / "inputs", directory / "verify"
                inputs.mkdir()
                write(inputs / "recipe.json", {"commands": []})
                variants = []
                for multiple in (1, 10, 30, 100):
                    (inputs / f"inputs/{multiple}x/builtin").mkdir(parents=True)
                    save = f"{multiple}.json"
                    write(inputs / save, {"multiple": multiple})
                    variant = {"multiple": multiple, "save": save}
                    variant["input_identity"] = input_identity(inputs, variant)
                    variants.append(variant)
                manifest = {"completed": failure != "generation_incomplete", "operation": "generate",
                            "recipe_sha256": variants[0]["input_identity"]["recipe_sha256"], "variants": variants}
                write(inputs / "scale-manifest.json", manifest)
                if failure == "recipe_identity_changed":
                    write(inputs / "recipe.json", {"commands": ["changed"]})
                elif failure == "input_identity_changed":
                    # 100× 不运行近景探针，其存档仍须绑定生成清单。
                    write(inputs / "100.json", {"changed": True})
                result = subprocess.run([sys.executable, "-B", SCRIPT, "verify", "--input", inputs,
                                         "--output", output, "--root", directory], capture_output=True, text=True)
                self.assertEqual(1, result.returncode, result.stdout + result.stderr)
                observed = read(output / "scale-verification.json")
                self.assertEqual(failure, observed["failure"])
                self.assertFalse(observed["completed"])
                self.assertFalse((output / "build-scenescale.log").exists())


if __name__ == "__main__":
    unittest.main()
