import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import summarize


class MemoryStabilityTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.run = self.root / "scene-node-first-1"
        self.run.mkdir()
        (self.root / "summary.json").write_text(json.dumps({"runs": [{
            "scenario": "scene-node-first", "report": "scene-node-first-1/report.json", "succeeded": True}]}), encoding="utf-8")
        (self.run / "report.json").write_text('{"outcome":"completed"}', encoding="utf-8")
        (self.run / "process.json").write_text(
            '{"exit_code":0,"timed_out":false,"cleanup_succeeded":true}', encoding="utf-8")

    def observe(self, samples, ready=0, end=10, valid=True):
        operation = {"kind": "open", "name": "map-node", "sample_valid": valid,
                     "ready": ready, "observation_ended": end}
        (self.run / "scene-operations.jsonl").write_text(json.dumps(operation) + "\n", encoding="utf-8")
        (self.run / "scene-frames.jsonl").write_text("".join(json.dumps({
            "at": at, "private_bytes": value, "working_set_bytes": 200,
            "buffer_bytes": 20, "texture_bytes": 50, "video_bytes": 70}) + "\n"
            for at, value in samples), encoding="utf-8")
        return self.result()

    def result(self):
        return summarize.summarize_directories([self.root])["scenarios"]["scene-node-first"]["memory_stability"]["A"][0]

    def test_stable_tail_preserves_values_at_the_one_percent_boundary(self):
        result = self.observe([(0, 100), (5, 101), (10, 100)])
        self.assertEqual(("stable", None, 10, 3, 0.01), tuple(result[k] for k in (
            "status", "reason", "observed_seconds", "samples", "range_ratio")))
        self.assertEqual((100, 100, 101), tuple(result["memory"]["private_bytes"][k]
                                              for k in ("minimum", "p50", "maximum")))
        self.assertEqual(200, result["memory"]["working_set_bytes"]["p50"])

    def test_rising_tail_remains_unknown_and_keeps_its_observed_range(self):
        result = self.observe([(0, 100), (5, 101), (10, 102)])
        self.assertEqual(("unknown", "still_changing", 0.02), tuple(result[k] for k in (
            "status", "reason", "range_ratio")))
        self.assertEqual((100, 101, 102), tuple(result["memory"]["private_bytes"][k]
                                              for k in ("minimum", "p50", "maximum")))

    def test_short_observation_is_unknown_even_when_every_value_matches(self):
        result = self.observe([(0, 100), (5, 100)], end=5)
        self.assertEqual(("unknown", "insufficient_observation", 5, 0), tuple(result[k] for k in (
            "status", "reason", "observed_seconds", "range_ratio")))
        self.assertEqual(100, result["memory"]["private_bytes"]["p50"])

    def test_only_the_last_ten_seconds_affect_stability(self):
        result = self.observe([(-1, 9000), (0, 8000), (49, 500), (50, 100),
                               (55, 101), (60, 100), (61, 7000)], end=60)
        self.assertEqual(("stable", 50, 60, 10, 3), tuple(result[k] for k in (
            "status", "window_start_at", "window_end_at", "sample_span_seconds", "samples")))
        self.assertEqual(0.01, result["range_ratio"])

    def test_one_percent_is_inclusive_with_values_on_both_sides(self):
        for maximum, status, ratio in ((10099, "stable", .0099), (10100, "stable", .01),
                                       (10101, "unknown", .0101)):
            with self.subTest(maximum=maximum):
                result = self.observe([(0, 10000), (5, maximum), (10, 10000)])
                self.assertEqual(status, result["status"])
                self.assertAlmostEqual(ratio, result["range_ratio"])

    def test_missing_or_truncated_frames_do_not_publish_a_stable_prefix(self):
        self.observe([(0, 100), (5, 100), (10, 100)])
        frames = self.run / "scene-frames.jsonl"
        with frames.open("a", encoding="utf-8") as stream:
            stream.write('{"at":')
        result = self.result()
        self.assertEqual(("unknown", "frames_unreadable_jsonl", {}), tuple(
            result[k] for k in ("status", "reason", "memory")))
        frames.unlink()
        self.assertEqual("frames_missing_file", self.result()["reason"])

    def test_missing_invalid_or_zero_private_counter_is_unknown(self):
        for value in (None, True, -1, float("nan"), 0):
            with self.subTest(value=value):
                result = self.observe([(0, value), (5, value), (10, value)])
                self.assertEqual(("unknown", "invalid_private_bytes", None), tuple(
                    result[k] for k in ("status", "reason", "range_ratio")))
                json.dumps(result, allow_nan=False)

    def test_a_missing_private_sample_does_not_make_the_remaining_flat_values_stable(self):
        result = self.observe([(0, 100), (5, None), (10, 100)])
        self.assertEqual(("unknown", "invalid_private_bytes", 0), tuple(
            result[k] for k in ("status", "reason", "range_ratio")))
        self.assertEqual((2, 1), tuple(result["memory"]["private_bytes"][k]
                                       for k in ("samples", "unknown_samples")))

    def test_coverage_and_distinct_sample_times_are_required(self):
        result = self.observe([(5, 100), (10, 100)])
        self.assertEqual("incomplete_frame_coverage", result["reason"])
        result = self.observe([(0, 100), (11, 100)])
        self.assertEqual("insufficient_samples", result["reason"])

    def test_a_failed_process_is_unknown_before_reading_its_frames(self):
        self.observe([(0, 100), (5, 100), (10, 100)])
        (self.run / "process.json").write_text(
            '{"exit_code":1,"timed_out":false,"cleanup_succeeded":true}', encoding="utf-8")
        (self.run / "scene-frames.jsonl").unlink()
        self.assertEqual("process_failed_or_incomplete", self.result()["reason"])

    def test_an_invalid_opening_keeps_values_but_cannot_be_stable(self):
        result = self.observe([(0, 100), (5, 100), (10, 100)], valid=False)
        self.assertEqual(("unknown", "invalid_opening", 0), tuple(result[k] for k in (
            "status", "reason", "range_ratio")))
        self.assertEqual(100, result["memory"]["private_bytes"]["p50"])


if __name__ == "__main__":
    unittest.main()
