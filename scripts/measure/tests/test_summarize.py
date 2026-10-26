import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import summarize


class DirectoryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def add_run(self, side="A", scenario="scene-test", report=None, **run_fields):
        directory = self.root / side
        directory.mkdir(exist_ok=True)
        summary_path = directory / "summary.json"
        summary = json.loads(summary_path.read_text()) if summary_path.exists() else {"runs": []}
        run_directory = directory / str(len(summary["runs"]) + 1)
        run_directory.mkdir()
        report_path = run_directory / "report.json"
        report_path.write_text(json.dumps(report if report is not None else self.report()), encoding="utf-8")
        (run_directory / "process.json").write_text(json.dumps(
            {"exit_code": 0, "timed_out": False, "cleanup_succeeded": True}), encoding="utf-8")
        run = {"scenario": scenario, "repeat": len(summary["runs"]) + 1,
               "report": str(report_path.relative_to(directory)), "succeeded": True}
        run.update(run_fields)
        summary["runs"].append(run)
        summary_path.write_text(json.dumps(summary), encoding="utf-8")
        return report_path

    @staticmethod
    def report(elapsed=2):
        return {"outcome": "completed", "timing": {
            "elapsed_seconds": elapsed, "actual_speed": 3,
            "window_median_actual_speed": 4, "window_minimum_actual_speed": 1,
            "frame_seconds": {"p50": 0.01, "p95": 0.03, "p99": 0.06, "maximum": 0.1},
            "simulation_seconds": {"p50": 0.001, "p95": 0.003, "p99": 0.006, "maximum": 0.01},
            "frames": [{"interval_seconds": 1000}]}, "memory": [], "operations": []}

    def metrics(self, result, scenario="scene-test"):
        return result["scenarios"][scenario]["metrics"]

    def test_groups_only_by_scenario_and_uses_each_reports_fixed_timing_values(self):
        self.add_run(report=self.report(2), route_camera="a")
        self.add_run(report=self.report(8), route_camera="b", report_unused="ignored")
        self.add_run(scenario="scene-other", report=self.report(20))
        result = summarize.summarize_directories([self.root / "A"])
        self.assertEqual({"scene-test", "scene-other"}, set(result["scenarios"]))
        self.assertEqual(2, result["scenarios"]["scene-test"]["runs"]["A"])
        metrics = self.metrics(result)
        self.assertEqual((5, 2, 8), tuple(metrics["timing.elapsed_seconds"]["A"][key]
                                         for key in ("median", "min", "max")))
        expected = {"frame_seconds.p50": 0.01, "frame_seconds.p95": 0.03,
                    "frame_seconds.p99": 0.06, "frame_seconds.maximum": 0.1,
                    "simulation_seconds.p50": 0.001, "simulation_seconds.p95": 0.003,
                    "simulation_seconds.p99": 0.006, "simulation_seconds.maximum": 0.01,
                    "actual_speed": 3, "window_median_actual_speed": 4,
                    "window_minimum_actual_speed": 1, "elapsed_seconds": 5}
        for name, value in expected.items():
            self.assertEqual(value, metrics[f"timing.{name}"]["A"]["median"], name)
        self.assertNotIn("timing.frames", metrics)

    def test_operations_use_name_and_take_each_runs_median_before_combining_runs(self):
        first = self.report()
        first["operations"] = [
            {"name": "new-operation", "duration_seconds": value, "succeeded": True}
            for value in (1, 3, 20)]
        second = self.report()
        second["operations"] = [{"name": "new-operation", "duration_seconds": 10, "succeeded": True}]
        self.add_run(report=first)
        self.add_run(report=second)
        metric = self.metrics(summarize.summarize_directories([self.root / "A"]))[
            "operations.new-operation.duration_seconds"]["A"]
        self.assertEqual((6.5, 3, 10), tuple(metric[key] for key in ("median", "min", "max")))
        self.assertEqual(2, metric["valid_runs"])

    def test_failed_or_non_numeric_operation_invalidates_that_runs_operation(self):
        for index, invalid in enumerate(({"succeeded": False}, {"duration_seconds": "unavailable"},
                                       {"duration_seconds": None}, {"duration_seconds": True}, {})):
            with self.subTest(invalid=invalid):
                report = self.report()
                bad = {"name": "load", "duration_seconds": 99, "succeeded": True}
                bad.update(invalid)
                if not invalid:
                    del bad["succeeded"]
                report["operations"] = [
                    {"name": "load", "duration_seconds": 1, "succeeded": True}, bad]
                side = f"case-{index}"
                self.add_run(side=side, report=report)
                metric = self.metrics(summarize.summarize_directories([self.root / side]))[
                    "operations.load.duration_seconds"]["A"]
                self.assertEqual(("unknown", None), (metric["status"], metric["median"]))

    def test_memory_reports_final_and_peak_per_run_for_each_fixed_counter(self):
        first = self.report()
        first["memory"] = [{"working_set_bytes": 100, "private_bytes": 80, "peak_working_set_bytes": 150,
                            "godot_static_bytes": 40, "video_memory_bytes": 50},
                           {"working_set_bytes": 20, "private_bytes": 90, "peak_working_set_bytes": 160,
                            "godot_static_bytes": 30, "video_memory_bytes": 60}]
        second = self.report()
        second["memory"] = [{"working_set_bytes": 60, "private_bytes": 100, "peak_working_set_bytes": 110,
                             "godot_static_bytes": 10, "video_memory_bytes": 20}]
        self.add_run(report=first)
        self.add_run(report=second)
        metrics = self.metrics(summarize.summarize_directories([self.root / "A"]))
        self.assertEqual(40, metrics["memory.working_set_bytes.final"]["A"]["median"])
        self.assertEqual(80, metrics["memory.working_set_bytes.peak"]["A"]["median"])
        self.assertEqual(95, metrics["memory.private_bytes.final"]["A"]["median"])
        self.assertEqual(135, metrics["memory.peak_working_set_bytes.peak"]["A"]["median"])
        self.assertEqual(25, metrics["memory.godot_static_bytes.peak"]["A"]["median"])
        self.assertEqual(40, metrics["memory.video_memory_bytes.final"]["A"]["median"])

    def test_unavailable_memory_makes_affected_metric_unknown(self):
        for index, invalid in enumerate(("unavailable", None, True)):
            with self.subTest(invalid=invalid):
                report = self.report()
                report["memory"] = [{"working_set_bytes": invalid, "private_bytes": 1},
                                    {"working_set_bytes": 20, "private_bytes": invalid}]
                side = f"memory-{index}"
                self.add_run(side=side, report=report)
                metrics = self.metrics(summarize.summarize_directories([self.root / side]))
                self.assertEqual(20, metrics["memory.working_set_bytes.final"]["A"]["median"])
                for name in ("working_set_bytes.peak", "private_bytes.final", "private_bytes.peak"):
                    metric = metrics[f"memory.{name}"]["A"]
                    self.assertEqual(("unknown", None, 1),
                                     (metric["status"], metric["median"], metric["unknown_runs"]))

    def test_failed_runs_never_contribute_reported_values(self):
        for index, condition in enumerate(("run_failed", "report_failed", "exit", "timeout", "cleanup", "missing_process")):
            with self.subTest(condition=condition):
                side = f"failure-{index}"
                report = self.report(100)
                report["operations"] = [{"name": "load", "duration_seconds": 100, "succeeded": True}]
                report["memory"] = [{"working_set_bytes": 100}]
                if condition == "report_failed":
                    report["outcome"] = "failed"
                path = self.add_run(side=side, report=report, succeeded=condition != "run_failed")
                process_path = path.parent / "process.json"
                process = json.loads(process_path.read_text())
                if condition == "missing_process":
                    process_path.unlink()
                else:
                    process.update({"exit_code": 2} if condition == "exit" else
                                   {"timed_out": True} if condition == "timeout" else
                                   {"cleanup_succeeded": False} if condition == "cleanup" else {})
                    process_path.write_text(json.dumps(process))
                metrics = self.metrics(summarize.summarize_directories([self.root / side]))
                for name in ("timing.elapsed_seconds", "operations.load.duration_seconds", "memory.working_set_bytes.final"):
                    self.assertEqual((None, 0, 1), tuple(metrics[name]["A"][key]
                                                         for key in ("median", "valid_runs", "unknown_runs")))
                    self.assertTrue(metrics[name]["A"]["unknown_reasons"])

    def test_bad_or_missing_report_is_counted_as_unknown_alongside_valid_runs(self):
        self.add_run(report=self.report(6))
        missing = self.add_run()
        missing.unlink()
        truncated = self.add_run()
        truncated.write_text('{"timing":')
        metric = self.metrics(summarize.summarize_directories([self.root / "A"]))["timing.elapsed_seconds"]["A"]
        self.assertEqual((6, 1, 2), tuple(metric[key] for key in ("median", "valid_runs", "unknown_runs")))
        self.assertEqual({"report_missing_file": 1, "report_unreadable_json": 1}, metric["unknown_reasons"])

    def test_two_sides_compare_run_medians_as_b_minus_a_and_percent_of_a(self):
        self.add_run(report=self.report(2))
        self.add_run(report=self.report(8))
        self.add_run(side="B", report=self.report(4))
        self.add_run(side="B", report=self.report(16))
        metrics = self.metrics(summarize.summarize_directories([self.root / "A", self.root / "B"]))
        metric = metrics["timing.elapsed_seconds"]
        self.assertEqual((5, 10), (metric["A"]["median"], metric["B"]["median"]))
        self.assertEqual({"value": 5, "status": "known", "reason": None}, metric["difference"])
        self.assertEqual({"value": 100, "status": "known", "reason": None}, metric["percent_change"])

    def test_zero_baseline_keeps_absolute_difference_and_marks_percent_unknown(self):
        self.add_run(report=self.report(0))
        self.add_run(side="B", report=self.report(4))
        metric = self.metrics(summarize.summarize_directories([self.root / "A", self.root / "B"]))[
            "timing.elapsed_seconds"]
        self.assertEqual(4, metric["difference"]["value"])
        self.assertEqual({"value": None, "status": "unknown", "reason": "zero_baseline"}, metric["percent_change"])

    def test_scenario_missing_on_one_side_has_unknown_comparison(self):
        self.add_run(scenario="only-a")
        self.add_run(side="B", scenario="only-b")
        result = summarize.summarize_directories([self.root / "A", self.root / "B"])
        for scenario, missing in (("only-a", "B"), ("only-b", "A")):
            with self.subTest(scenario=scenario):
                metric = self.metrics(result, scenario)["timing.elapsed_seconds"]
                self.assertEqual((None, 0, 0), tuple(metric[missing][key]
                                                     for key in ("median", "valid_runs", "unknown_runs")))
                self.assertEqual("unknown", metric["difference"]["status"])
                self.assertIsNone(metric["percent_change"]["value"])
                self.assertEqual(0, result["scenarios"][scenario]["runs"][missing])

    def test_missing_or_truncated_summary_marks_input_unknown(self):
        self.add_run()
        for index, content in enumerate((None, '{"runs":', '{}', '{"runs": null}')):
            with self.subTest(content=content):
                directory = self.root / f"broken-{index}"
                directory.mkdir()
                if content is not None:
                    (directory / "summary.json").write_text(content)
                result = summarize.summarize_directories([self.root / "A", directory])
                self.assertEqual("unknown", result["input_status"]["B"]["status"])
                self.assertTrue(result["input_status"]["B"]["reason"])
                self.assertEqual(2, self.metrics(result)["timing.elapsed_seconds"]["A"]["median"])
                self.assertEqual("unknown", self.metrics(result)["timing.elapsed_seconds"]["B"]["status"])

    def test_cli_writes_requested_json_and_leaves_inputs_unchanged(self):
        self.add_run()
        self.add_run(side="B", report=self.report(5))
        before = {path: path.read_bytes() for path in self.root.rglob("*.json")}
        for sides in (("A",), ("A", "B")):
            with self.subTest(sides=sides):
                output = self.root / ("result-" + "".join(sides) + ".json")
                result = subprocess.run([sys.executable, str(Path(summarize.__file__)),
                                         *(str(self.root / side) for side in sides), "--output", str(output)],
                                        capture_output=True, text=True, timeout=10)
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertTrue(output.is_file())
                content = json.loads(output.read_text(encoding="utf-8"))
                self.assertEqual(2, self.metrics(content)["timing.elapsed_seconds"]["A"]["median"])
                self.assertEqual(set(sides), set(content["inputs"]))
        self.assertEqual(before, {path: path.read_bytes() for path in before})

    def test_cli_refuses_to_overwrite_an_input_report(self):
        report = self.add_run()
        original = report.read_bytes()
        result = subprocess.run([sys.executable, str(Path(summarize.__file__)), str(self.root / "A"),
                                 "--output", str(report)], capture_output=True, timeout=10)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(original, report.read_bytes())

    def test_null_sections_produce_unknown_metrics(self):
        self.add_run(report={"outcome": "completed", "timing": None, "memory": None, "operations": None})
        metrics = self.metrics(summarize.summarize_directories([self.root / "A"]))
        self.assertEqual(len(summarize.TIMING_METRICS + summarize.MEMORY_METRICS), len(metrics))
        for metric in metrics.values():
            self.assertEqual((None, 1, "unknown"), tuple(metric["A"][key]
                                                       for key in ("median", "unknown_runs", "status")))

    def test_precheck_rejected_slot_remains_an_unknown_run(self):
        path = self.add_run(succeeded=False, precheck={"status": "rejected"})
        path.unlink()
        path.parent.joinpath("process.json").unlink()
        metric = self.metrics(summarize.summarize_directories([self.root / "A"]))["timing.actual_speed"]["A"]
        self.assertEqual((None, 0, 1), tuple(metric[key] for key in ("median", "valid_runs", "unknown_runs")))
        self.assertEqual({"run_failed": 1}, metric["unknown_reasons"])

    def test_incomplete_or_non_numeric_process_metadata_cannot_certify_a_run(self):
        for index, process in enumerate(({}, {"exit_code": False, "timed_out": False, "cleanup_succeeded": True},
                                        {"exit_code": 0, "timed_out": 0, "cleanup_succeeded": True},
                                        {"exit_code": 0, "timed_out": False, "cleanup_succeeded": 1})):
            with self.subTest(process=process):
                side = f"process-{index}"
                path = self.add_run(side=side)
                path.parent.joinpath("process.json").write_text(json.dumps(process))
                metric = self.metrics(summarize.summarize_directories([self.root / side]))["timing.actual_speed"]["A"]
                self.assertEqual((None, 1), (metric["median"], metric["unknown_runs"]))

    def test_partial_reports_with_malformed_collections_do_not_abort_other_runs(self):
        for index, (memory, operations) in enumerate(((12, {}), ([None, 12], [False, {}, {"name": 1}]), ([], 12))):
            with self.subTest(index=index):
                side = f"partial-{index}"
                self.add_run(side=side, report=self.report(6))
                self.add_run(side=side, report={"outcome": "failed", "timing": {"elapsed_seconds": 100},
                                                "memory": memory, "operations": operations})
                metric = self.metrics(summarize.summarize_directories([self.root / side]))["timing.elapsed_seconds"]["A"]
                self.assertEqual((6, 1, 1), tuple(metric[key] for key in ("median", "valid_runs", "unknown_runs")))


class StatisticsTests(unittest.TestCase):
    def test_no_valid_values_produces_null_with_reason(self):
        for samples in ([], [(None, "missing")]):
            with self.subTest(samples=samples):
                result = summarize.summarize_values(samples)
                self.assertEqual("unknown", result["status"])
                self.assertTrue(result["reason"])
                self.assertEqual((None, None, None),
                                 (result["median"], result["min"], result["max"]))
                self.assertEqual((0, len(samples)), (result["valid_runs"], result["unknown_runs"]))

    def test_repeated_values_report_median_and_range(self):
        result = summarize.summarize_values([(9, None), (1, None), (3, None), (5, None)])
        self.assertEqual((4, 1, 9), (result["median"], result["min"], result["max"]))
        self.assertEqual((4, 0, "known"),
                         (result["valid_runs"], result["unknown_runs"], result["status"]))

    def test_non_numbers_are_unknown_instead_of_zero(self):
        for invalid in (None, "unavailable", "12", True, False, float("nan"), float("inf")):
            with self.subTest(value=invalid):
                result = summarize.summarize_values([(2, None), (invalid, "invalid")])
                self.assertEqual((2, 2, 2), (result["median"], result["min"], result["max"]))
                self.assertEqual((1, 1, {"invalid": 1}),
                                 (result["valid_runs"], result["unknown_runs"], result["unknown_reasons"]))


if __name__ == "__main__":
    unittest.main()
