import json
import shutil
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import summarize


class ReportPathTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.directory = self.root / "original"
        self.report = self.directory / "scene/report.json"
        self.report.parent.mkdir(parents=True)
        self.report.write_text(json.dumps({"outcome": "completed", "timing": {"elapsed_seconds": 5}}))
        self.report.with_name("process.json").write_text(json.dumps(
            {"exit_code": 0, "timed_out": False, "cleanup_succeeded": True}))
        self.summary = {"runs": [{"scenario": "scene-test", "succeeded": True, "report": "scene/report.json"}]}
        self.write_summary()

    def write_summary(self):
        (self.directory / "summary.json").write_text(json.dumps(self.summary))

    def metric(self, directory):
        return summarize.summarize_directories([directory])["scenarios"]["scene-test"]["metrics"][
            "timing.elapsed_seconds"]["A"]

    def test_absolute_and_escaping_reports_are_unknown(self):
        shutil.copytree(self.directory, self.root / "outside")
        for path, reason in ((str(self.report), "report_absolute_path"),
                             ("C:\\outside\\report.json", "report_absolute_path"),
                             ("/outside/report.json", "report_absolute_path"),
                             ("\x00", "report_invalid_path"),
                             ("../outside/scene/report.json", "report_outside_directory")):
            with self.subTest(path=path):
                self.summary["runs"][0]["report"] = path
                self.write_summary()
                metric = self.metric(self.directory)
                self.assertEqual((None, 0, 1), tuple(metric[key] for key in ("median", "valid_runs", "unknown_runs")))
                self.assertEqual({reason: 1}, metric["unknown_reasons"])

    def test_copied_output_reads_its_own_report(self):
        copied = shutil.copytree(self.directory, self.root / "copied")
        report = copied / "scene/report.json"
        content = json.loads(report.read_text())
        content["timing"]["elapsed_seconds"] = 999
        report.write_text(json.dumps(content))
        self.assertEqual(999, self.metric(copied)["median"])
        self.assertEqual(5, self.metric(self.directory)["median"])

    def test_input_description_keeps_the_supplied_relative_path(self):
        result = summarize.summarize_directories([Path("relative-results")])
        self.assertEqual("relative-results", result["inputs"]["A"])


if __name__ == "__main__":
    unittest.main()
