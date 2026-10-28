import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import summarize


class MeasurementSummaryTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)

    def add_run(self, operations=(), scenario="scene-node", side="A", **fields):
        directory = self.root / side
        directory.mkdir(exist_ok=True)
        summary_path = directory / "summary.json"
        summary = json.loads(summary_path.read_text()) if summary_path.exists() else {"runs": []}
        report = directory / str(len(summary["runs"]) + 1) / "report.json"
        report.parent.mkdir()
        report.write_text(json.dumps({"outcome": "completed", **fields}), encoding="utf-8")
        report.with_name("process.json").write_text(json.dumps(
            {"exit_code": 0, "timed_out": False, "cleanup_succeeded": True}), encoding="utf-8")
        report.with_name("scene-operations.jsonl").write_text(
            "".join(json.dumps(record) + "\n" for record in operations), encoding="utf-8")
        summary["runs"].append({"scenario": scenario, "report": str(report.relative_to(directory)),
                                "succeeded": True})
        summary_path.write_text(json.dumps(summary), encoding="utf-8")
        return report

    def result(self, scenario="scene-node"):
        return summarize.summarize_directories([self.root / "A"])["scenarios"][scenario]

    def test_openings_pool_valid_raw_samples_by_operation_using_nearest_rank(self):
        for index in range(1, 31):
            self.add_run([{"kind": "open", "name": "map-node", "sample_valid": True,
                           "click_to_operable_seconds": index / 100,
                           "operable_to_ready_seconds": index,
                           "load_button_to_operable_seconds": None}])
        self.add_run([
            {"kind": "open", "name": "map-node", "sample_valid": False,
             "click_to_operable_seconds": 1000},
            {"kind": "open", "name": "map-node", "sample_valid": 1,
             "click_to_operable_seconds": 1000},
            {"kind": "open", "name": "reload-node", "sample_valid": True,
             "click_to_operable_seconds": 0.2, "operable_to_ready_seconds": 0,
             "load_button": 1, "load_button_to_operable_seconds": 3.5},
            {"kind": "natural-map", "name": "not-an-opening", "sample_valid": True}])
        result = self.result()
        self.assertIn("openings", result)
        openings = result["openings"]["A"]["by_operation"]
        self.assertEqual({"map-node", "reload-node"}, set(openings))
        self.assertEqual((30, 2), (openings["map-node"]["valid_samples"],
                                  openings["map-node"]["invalid_samples"]))
        latency = openings["map-node"]["click_to_operable_seconds"]
        self.assertEqual((30, 0.15, 0.29, 0.3, 0.3), tuple(
            latency[key] for key in ("samples", "p50", "p95", "p99", "maximum")))
        self.assertEqual(30, openings["map-node"]["operable_to_ready_seconds"]["maximum"])
        self.assertEqual((0, 0), tuple(openings["map-node"]["load_button_to_operable_seconds"][key]
                                     for key in ("samples", "unknown_samples")))
        self.assertEqual(3.5, openings["reload-node"]["load_button_to_operable_seconds"]["maximum"])
        self.assertEqual(0, openings["reload-node"]["operable_to_ready_seconds"]["maximum"])

    def test_hourly_map_slopes_keep_each_runs_span_and_cross_run_range(self):
        for increment in (100, 150):
            self.add_run([
                {"kind": "natural-map", "at": at,
                 "memory": {"samples": 3, "private_bytes": value,
                            "working_set_bytes": 2 * value, "video_bytes": 3 * value}}
                for at, value in ((100, 1000), (1900, 1000 + increment),
                                  (3700, 1000 + 2 * increment), (5500, 1000 + 4 * increment),
                                  (7300, 1000 + 6 * increment))], scenario="scene-soak")
        result = self.result("scene-soak")
        self.assertIn("hourly_memory", result)
        memory = result["hourly_memory"]["A"]
        self.assertEqual("first_natural_map", memory["origin"])
        first, second = memory["runs"][0]["hours"]
        self.assertEqual((1, 3, 3600, 200), tuple(
            first[key] for key in ("hour", "samples", "observed_seconds", "private_bytes_per_hour")))
        self.assertEqual((2, 3, 3600, 400), tuple(
            second[key] for key in ("hour", "samples", "observed_seconds", "private_bytes_per_hour")))
        self.assertEqual((400, 600), (first["working_set_bytes_per_hour"], first["video_bytes_per_hour"]))
        self.assertEqual((250, 200, 300), tuple(memory["by_hour"]["1"]["private_bytes_per_hour"][key]
                                              for key in ("median", "min", "max")))
        self.assertEqual((500, 400, 600), tuple(memory["by_hour"]["2"]["private_bytes_per_hour"][key]
                                              for key in ("median", "min", "max")))

    def test_partial_hour_is_retained_but_unknown_across_runs(self):
        self.add_run([
            {"kind": "natural-map", "at": at,
             "memory": {"samples": 1, "private_bytes": value,
                        "working_set_bytes": value, "video_bytes": value}}
            for at, value in ((250, 0), (3850, 1), (7450, 2), (7630, 50_000_002))],
            scenario="scene-soak")
        memory = self.result("scene-soak")["hourly_memory"]["A"]
        partial = memory["runs"][0]["hours"][2]
        self.assertEqual((2, 180, "partial_hour", None), tuple(partial[key] for key in (
            "samples", "observed_seconds", "reason", "private_bytes_per_hour")))
        metric = memory["by_hour"]["3"]["private_bytes_per_hour"]
        self.assertEqual(("unknown", 0, 1, {"partial_hour": 1}), tuple(metric[key] for key in (
            "status", "valid_runs", "unknown_runs", "unknown_reasons")))

    def test_complete_hours_share_boundary_samples_at_regular_cadence(self):
        records = [{"kind": "natural-map", "at": at,
                    "memory": {"samples": 1, "private_bytes": at,
                               "working_set_bytes": at, "video_bytes": at}}
                   for at in range(250, 7451, 180)]
        self.add_run(records, scenario="scene-soak")
        memory = self.result("scene-soak")["hourly_memory"]["A"]
        for hour in memory["runs"][0]["hours"]:
            self.assertEqual((21, 3600, None), tuple(hour[key] for key in (
                "samples", "observed_seconds", "reason")))
            self.assertAlmostEqual(3600, hour["private_bytes_per_hour"])
            self.assertEqual(1, memory["by_hour"][str(hour["hour"])]["private_bytes_per_hour"]["valid_runs"])

    def test_hourly_coverage_uses_original_samples_bracketing_jittered_boundaries(self):
        self.add_run([
            {"kind": "natural-map", "at": at,
             "memory": {"samples": 1, "private_bytes": at,
                        "working_set_bytes": at, "video_bytes": at}}
            for at in (0, 1801, 3602, 5403, 7204)], scenario="scene-soak")
        memory = self.result("scene-soak")["hourly_memory"]["A"]
        first, second, partial = memory["runs"][0]["hours"]
        self.assertEqual((0, 3600, 0, 3602, 3602, None), tuple(first[key] for key in (
            "start_at", "end_at", "observed_start_at", "observed_end_at", "observed_seconds", "reason")))
        self.assertEqual((3600, 7200, 1801, 7204, None), tuple(second[key] for key in (
            "start_at", "end_at", "observed_start_at", "observed_end_at", "reason")))
        self.assertAlmostEqual(3600, memory["by_hour"]["2"]["private_bytes_per_hour"]["median"])
        self.assertEqual("partial_hour", partial["reason"])

    def test_hourly_slope_uses_least_squares_for_non_collinear_points(self):
        self.add_run([
            {"kind": "natural-map", "at": at,
             "memory": {"samples": 1, "private_bytes": value,
                        "working_set_bytes": value, "video_bytes": value}}
            for at, value in ((100, 0), (1000, 3), (3700, 3))], scenario="scene-soak")
        memory = self.result("scene-soak")["hourly_memory"]["A"]
        self.assertAlmostEqual(30 / 13, memory["by_hour"]["1"]["private_bytes_per_hour"]["median"])

    def test_failed_missing_and_truncated_sidecars_never_contribute_partial_openings(self):
        opening = {"kind": "open", "name": "map-node", "sample_valid": True,
                   "click_to_operable_seconds": 0.3}
        self.add_run([opening])
        self.add_run([{**opening, "click_to_operable_seconds": 1000}], outcome="failed")
        broken = self.add_run([{**opening, "click_to_operable_seconds": 1000}])
        with broken.with_name("scene-operations.jsonl").open("a") as stream:
            stream.write('{"kind":')
        missing = self.add_run([opening])
        missing.with_name("scene-operations.jsonl").unlink()
        openings = self.result()["openings"]["A"]
        self.assertEqual({"report_not_completed": 1, "operations_unreadable_jsonl": 1,
                          "operations_missing_file": 1}, openings["unknown_reasons"])
        self.assertEqual(3, openings["unknown_runs"])
        self.assertEqual(1, openings["by_operation"]["map-node"]["valid_samples"])
        self.assertEqual(0.3, openings["by_operation"]["map-node"]["click_to_operable_seconds"]["maximum"])

    def test_opening_metrics_keep_unavailable_and_non_numeric_values_unknown(self):
        for value in (None, "unavailable", True, -1, float("nan")):
            self.add_run([{"kind": "open", "name": "map-node", "sample_valid": True,
                           "click_to_operable_seconds": value}])
        latency = self.result()["openings"]["A"]["by_operation"]["map-node"]["click_to_operable_seconds"]
        self.assertEqual((0, 5, None, None), tuple(latency[key] for key in (
            "samples", "unknown_samples", "p95", "maximum")))

    def test_hourly_slopes_do_not_fill_missing_hours_or_counters_with_zero(self):
        self.add_run([
            {"kind": "natural-map", "at": at, "memory": {"samples": 2, "private_bytes": private,
                                                          "working_set_bytes": 10, "video_bytes": None}}
            for at, private in ((100, 4), (3700, 3), (5500, 1), (7300, 0), (10900, None))], scenario="scene-soak")
        self.add_run([], scenario="scene-soak")
        memory = self.result("scene-soak")["hourly_memory"]["A"]
        first, second, third = memory["runs"][0]["hours"]
        self.assertEqual(-1, first["private_bytes_per_hour"])
        self.assertEqual(-3, second["private_bytes_per_hour"])
        self.assertIsNone(third["private_bytes_per_hour"])
        self.assertIsNone(second["video_bytes_per_hour"])
        self.assertEqual(0, second["working_set_bytes_per_hour"])
        self.assertEqual("map_samples_missing", memory["runs"][1]["reason"])
        self.assertEqual((-3, 1, 1), tuple(memory["by_hour"]["2"]["private_bytes_per_hour"][key]
                                          for key in ("median", "valid_runs", "unknown_runs")))


if __name__ == "__main__":
    unittest.main()
