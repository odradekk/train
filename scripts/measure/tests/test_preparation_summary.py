import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import summarize


class PreparationSummaryTests(unittest.TestCase):
    @staticmethod
    def report(net, raw, gc, status="passed"):
        item = {"metric": "scene_preparation_maximum", "unit": "seconds", "status": status,
                "measured": net, "raw_maximum": raw, "gc_pause_maximum": gc}
        return summarize.report_metrics({"assessment": {"items": [item]}}), None, None

    def test_preserves_net_raw_and_gc_maxima_independently(self):
        # Three different frames can supply the three maxima; subtracting maxima is invalid.
        result = summarize.summarize_preparation([
            self.report(0.004, 0.012, 0.009), self.report(0.003, 0.020, 0.018)])
        self.assertEqual("passed", result["status"])
        self.assertEqual([0.004, 0.003], result["run_maxima"])
        self.assertEqual(0.004, result["maximum"])
        self.assertEqual([0.012, 0.020], result["raw_run_maxima"])
        self.assertEqual(0.020, result["raw_maximum"])
        self.assertEqual([0.009, 0.018], result["gc_pause_run_maxima"])
        self.assertEqual(0.018, result["gc_pause_maximum"])

    def test_missing_raw_or_gc_makes_old_report_unknown(self):
        for field in ("raw_maximum", "gc_pause_maximum"):
            with self.subTest(field=field):
                report, path, reason = self.report(0.003, 0.003, 0)
                del report["scene_preparation"][field]
                result = summarize.summarize_preparation([(report, path, reason)])
                self.assertEqual(("unknown", [None], None, 0, 1), tuple(result[key] for key in (
                    "status", "run_maxima", "maximum", "valid_runs", "unknown_runs")))
                self.assertEqual([None], result["raw_run_maxima"])
                self.assertEqual([None], result["gc_pause_run_maxima"])

    def test_invalid_raw_or_gc_cannot_certify_a_run(self):
        for field in ("raw_maximum", "gc_pause_maximum"):
            for value in (None, True, -0.001, float("nan"), float("inf"), "0"):
                with self.subTest(field=field, value=value):
                    report, path, reason = self.report(0.003, 0.003, 0)
                    report["scene_preparation"][field] = value
                    result = summarize.summarize_preparation([(report, path, reason)])
                    self.assertEqual(("unknown", [None], 0, 1), tuple(result[key] for key in (
                        "status", "run_maxima", "valid_runs", "unknown_runs")))

    def test_known_failure_is_not_hidden_by_missing_gc(self):
        missing, path, reason = self.report(0.003, 0.003, 0)
        del missing["scene_preparation"]["gc_pause_maximum"]
        result = summarize.summarize_preparation([
            self.report(0.005, 0.015, 0.011, "failed"), (missing, path, reason)])
        self.assertEqual(("failed", 0.005, 1, 1), tuple(result[key] for key in (
            "status", "maximum", "valid_runs", "unknown_runs")))
        self.assertEqual([0.005, None], result["run_maxima"])



if __name__ == "__main__":
    unittest.main()
