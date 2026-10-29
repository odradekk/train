"""按场景汇总测量输出；原始报告只读。"""
# Decision note: 2026-10-26-performance-measurement-system

import argparse
from collections import Counter
import json
from math import ceil, isfinite
from pathlib import Path, PureWindowsPath
from statistics import median

TIMING_METRICS = tuple(
    f"timing.{distribution}.{statistic}"
    for distribution in ("frame_seconds", "simulation_seconds")
    for statistic in ("p50", "p95", "p99", "maximum")
) + tuple(f"timing.{name}" for name in (
    "actual_speed", "elapsed_seconds"))


def is_number(value):
    return type(value) in (int, float) and isfinite(value)


def summarize_values(samples):
    values = [value for value, _ in samples if is_number(value)]
    unknown = Counter(reason or "non_numeric" for value, reason in samples if not is_number(value))
    return {"status": "known" if values else "unknown", "reason": None if values else "no_valid_runs",
            "valid_runs": len(values), "unknown_runs": sum(unknown.values()),
            "median": median(values) if values else None, "min": min(values) if values else None,
            "max": max(values) if values else None, "unknown_reasons": dict(unknown)}


def report_metrics(report):
    metrics = {}
    for metric in TIMING_METRICS:
        value = report
        for key in metric.split("."):
            value = value.get(key) if isinstance(value, dict) else None
        metrics[metric] = (value, "missing_or_non_numeric")
    assessment = report.get("assessment")
    items = assessment.get("items") if isinstance(assessment, dict) else None
    metrics["scene_preparation"] = next((item for item in items
        if isinstance(item, dict) and item.get("metric") == "scene_preparation_maximum"), {}) if isinstance(items, list) else {}
    return metrics


def summarize_preparation(reports):
    samples, statuses = [], []
    raw_maxima, gc_pause_maxima = [], []
    for report, _, reason in reports:
        item = report.get("scene_preparation", {})
        status, value = item.get("status"), item.get("measured")
        raw, gc_pause = item.get("raw_maximum"), item.get("gc_pause_maximum")
        valid = (reason is None and status in ("passed", "failed") and item.get("unit") == "seconds"
                 and all(is_number(number) and number >= 0 for number in (value, raw, gc_pause)))
        samples.append((value if valid else None, reason or item.get("reason") or "missing_or_invalid_assessment"))
        raw_maxima.append(raw if valid else None)
        gc_pause_maxima.append(gc_pause if valid else None)
        statuses.append(status if valid else "unknown")
    summary = summarize_values(samples)
    return {"status": "failed" if "failed" in statuses else
            "unknown" if summary["unknown_runs"] or not summary["valid_runs"] else "passed",
            "unit": "seconds", "run_maxima": [value for value, _ in samples],
            "raw_run_maxima": raw_maxima, "raw_maximum": max((x for x in raw_maxima if x is not None), default=None),
            "gc_pause_run_maxima": gc_pause_maxima,
            "gc_pause_maximum": max((x for x in gc_pause_maxima if x is not None), default=None),
            "maximum": summary["max"], "valid_runs": summary["valid_runs"],
            "unknown_runs": summary["unknown_runs"], "unknown_reasons": summary["unknown_reasons"]}


def read_json(path):
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
        return (value, None) if isinstance(value, dict) else ({}, "invalid_json_shape")
    except FileNotFoundError:
        return {}, "missing_file"
    except (OSError, ValueError):
        return {}, "unreadable_json"


def read_run(directory, run):
    report_name = Path(run["report"])
    if report_name.anchor or PureWindowsPath(run["report"]).anchor:
        return {}, None, "report_absolute_path"
    try:
        root = directory.resolve()
        report_path = (root / report_name).resolve()
    except (OSError, ValueError):
        return {}, None, "report_invalid_path"
    if not report_path.is_relative_to(root):
        return {}, None, "report_outside_directory"
    report, report_problem = read_json(report_path)
    process, process_problem = read_json(report_path.parent / "process.json")
    reason = None
    if run.get("succeeded") is not True:
        reason = "run_failed"
    elif report_problem:
        reason = f"report_{report_problem}"
    elif report.get("outcome") != "completed":
        reason = "report_not_completed"
    elif process_problem:
        reason = f"process_{process_problem}"
    elif (type(process.get("exit_code")) is not int or process["exit_code"] != 0 or
          process.get("timed_out") is not False or process.get("cleanup_succeeded") is not True):
        reason = "process_failed_or_incomplete"
    return report_metrics(report), report_path, reason


def distribution(values):
    known = sorted(value for value in values if is_number(value) and value >= 0)
    return {"samples": len(known), "unknown_samples": len(values) - len(known),
            **{key: known[ceil(fraction * len(known)) - 1] if known else None
               for key, fraction in (("p50", 0.5), ("p95", 0.95), ("p99", 0.99), ("maximum", 1))}}


def read_operations(path):
    try:
        records = []
        with path.open(encoding="utf-8-sig") as source:
            for line in source:
                if not line.strip():
                    continue
                record = json.loads(line)
                if not isinstance(record, dict):
                    return [], "operations_invalid_record"
                records.append(record)
        return records, None
    except FileNotFoundError:
        return [], "operations_missing_file"
    except (OSError, ValueError):
        return [], "operations_unreadable_jsonl"


def summarize_openings(runs):
    groups, problems = {}, Counter()
    for records, reason in runs:
        if reason:
            problems[reason] += 1
            continue
        for record in records:
            if record.get("kind") == "open" and record.get("name"):
                groups.setdefault(record["name"], []).append(record)
    operations = {}
    for name, records in sorted(groups.items()):
        valid = [record for record in records if record.get("sample_valid") is True]
        operations[name] = {"valid_samples": len(valid), "invalid_samples": len(records) - len(valid),
                            **{metric: distribution([record.get(metric) for record in valid
                                if metric != "load_button_to_operable_seconds" or record.get("load_button") is not None]) for metric in (
                                "click_to_operable_seconds", "operable_to_ready_seconds",
                                "load_button_to_operable_seconds")}}
    return {"unknown_runs": sum(problems.values()), "unknown_reasons": dict(problems),
            "by_operation": operations}


def read_frames(path):
    with path.open(encoding="utf-8-sig") as source:
        for line in source:
            if not line.strip():
                continue
            frame = json.loads(line)
            if not isinstance(frame, dict) or not is_number(frame.get("at")):
                raise TypeError("invalid frame record")
            yield frame


def memory_window(path, records, reason, frame_phase=None):
    """Classify the last ten seconds of a node opening or a selected frame phase."""
    result = {"status": "unknown", "reason": reason, "window_seconds": 10,
              "window_start_at": None, "window_end_at": None, "observed_seconds": None,
              "samples": 0, "sample_span_seconds": None, "range_ratio": None, "memory": {}}
    if reason:
        return result
    valid = True
    if frame_phase is None:
        openings = [record for record in records if record.get("kind") == "open" and record.get("name") == "map-node"]
        if len(openings) != 1:
            return {**result, "reason": "node_opening_missing_or_ambiguous"}
        opening = openings[0]
        ready, end = opening.get("ready"), opening.get("observation_ended")
        valid = opening.get("sample_valid") is True
    frames_path = path.with_name("scene-frames.jsonl")
    try:
        if frame_phase is not None:
            ready = end = None
            for frame in read_frames(frames_path):
                if frame.get("phase") == frame_phase:
                    ready = frame["at"] if ready is None else ready
                    end = frame["at"]
            if ready is None:
                return {**result, "reason": "frames_phase_missing"}
        if not is_number(ready) or not is_number(end) or ready < 0 or end < ready:
            return {**result, "reason": "invalid_observation_times"}
        start = max(ready, end - 10)
        result.update(window_start_at=start, window_end_at=end, observed_seconds=end - start)
        values = {key: [] for key in ("private_bytes", "working_set_bytes", "buffer_bytes", "texture_bytes", "video_bytes")}
        times, before, after = [], False, False
        for frame in read_frames(frames_path):
            at = frame["at"]
            before |= at <= start
            after |= at >= end
            if start <= at <= end and (frame_phase is None or frame.get("phase") == frame_phase):
                times.append(at)
                for key in values:
                    values[key].append(frame.get(key))
    except FileNotFoundError:
        return {**result, "reason": "frames_missing_file"}
    except TypeError:
        return {**result, "reason": "frames_invalid_record"}
    except (OSError, ValueError):
        return {**result, "reason": "frames_unreadable_jsonl"}
    result["samples"] = len(times)
    result["sample_span_seconds"] = max(times) - min(times) if times else None
    for key, samples in values.items():
        known = [value for value in samples if is_number(value) and value >= 0]
        result["memory"][key] = {**distribution(samples), "minimum": min(known) if known else None}
    private = result["memory"]["private_bytes"]
    if private["minimum"] is not None and private["minimum"] > 0:
        result["range_ratio"] = (private["maximum"] - private["minimum"]) / private["minimum"]
    if not valid:
        result["reason"] = "invalid_opening"
    elif end - ready < 10:
        result["reason"] = "insufficient_observation"
    elif not before or not after:
        result["reason"] = "incomplete_frame_coverage"
    elif len(set(times)) < 2:
        result["reason"] = "insufficient_samples"
    elif private["unknown_samples"] or result["range_ratio"] is None:
        result["reason"] = "invalid_private_bytes"
    elif result["range_ratio"] > 0.01:
        result["reason"] = "still_changing"
    else:
        result.update(status="stable", reason=None)
    return result


MEMORY_COUNTERS = ("private_bytes", "working_set_bytes", "video_bytes")


def hourly_memory(records):
    maps = [record for record in records if record.get("kind") == "natural-map"]
    if not maps:
        return [], "map_samples_missing"
    if any(not is_number(record.get("at")) or not isinstance(record.get("memory"), dict) or
           not is_number(record["memory"].get("samples")) or record["memory"]["samples"] <= 0 for record in maps):
        return [], "map_sample_invalid"
    maps.sort(key=lambda record: record["at"])
    origin, end = maps[0]["at"], maps[-1]["at"]
    hours = []
    for index in range(max(1, ceil((end - origin) / 3600))):
        start, stop = origin + index * 3600, origin + (index + 1) * 3600
        observed_start = max(record["at"] for record in maps if record["at"] <= start)
        observed_end = next((record["at"] for record in maps if record["at"] >= stop), end)
        points = [record for record in maps if observed_start <= record["at"] <= observed_end]
        result = {"hour": index + 1, "start_at": start, "end_at": stop, "samples": len(points),
                  "observed_start_at": observed_start, "observed_end_at": observed_end,
                  "observed_seconds": observed_end - observed_start}
        result["reason"] = "partial_hour" if end < stop else None
        for counter in MEMORY_COUNTERS:
            values = [record["memory"].get(counter) for record in points]
            slope = None
            if result["reason"] is None and len(points) >= 2 and all(is_number(value) and value >= 0 for value in values):
                times = [(record["at"] - start) / 3600 for record in points]
                mean_time, mean_value = sum(times) / len(times), sum(values) / len(values)
                denominator = sum((time - mean_time) ** 2 for time in times)
                if denominator > 0:
                    slope = sum((time - mean_time) * (value - mean_value)
                                for time, value in zip(times, values)) / denominator
            result[counter + "_per_hour"] = slope
        hours.append(result)
    return hours, None


def summarize_hourly_memory(runs):
    results, groups = [], {}
    for index, (records, reason) in enumerate(runs, 1):
        hours, problem = ([], reason) if reason else hourly_memory(records)
        results.append({"run": index, "hours": hours, "reason": problem})
        for hour in hours:
            groups.setdefault(str(hour["hour"]), {})[index] = hour
    return {"origin": "first_natural_map", "runs": results,
            "by_hour": {hour: {counter + "_per_hour": summarize_values([
                (values.get(result["run"], {}).get(counter + "_per_hour"),
                 result["reason"] or values.get(result["run"], {}).get("reason") or
                 "hour_missing_or_insufficient_samples") for result in results])
                for counter in MEMORY_COUNTERS} for hour, values in groups.items()}}


def compare(a, b):
    if a is None or b is None:
        unknown = {"value": None, "status": "unknown", "reason": "missing_side_median"}
        return {"difference": unknown, "percent_change": unknown.copy()}
    difference = b - a
    percent = ({"value": None, "status": "unknown", "reason": "zero_baseline"} if a == 0 else
               {"value": difference / a * 100, "status": "known", "reason": None})
    return {"difference": {"value": difference, "status": "known", "reason": None},
            "percent_change": percent}


def summarize_directories(directories):
    sides = dict(zip(("A", "B"), map(Path, directories)))
    groups = {}
    input_status = {}
    for side, directory in sides.items():
        summary, reason = read_json(directory / "summary.json")
        if reason is None and not isinstance(summary.get("runs"), list):
            reason = "invalid_runs"
        input_status[side] = {"status": "unknown" if reason else "known", "reason": reason}
        if reason:
            continue
        for run in summary["runs"]:
            groups.setdefault(run["scenario"], {}).setdefault(side, []).append(read_run(directory, run))
    scenarios = {}
    for scenario, reports_by_side in sorted(groups.items()):
        for side in sides:
            reports_by_side.setdefault(side, [])
        metrics = {}
        for metric in sorted(TIMING_METRICS):
            metrics[metric] = {}
            for side, reports in reports_by_side.items():
                samples = []
                for report, _, reason in reports:
                    samples.append((None, reason) if reason else report[metric])
                metrics[metric][side] = summarize_values(samples)
            if len(sides) == 2:
                metrics[metric].update(compare(metrics[metric]["A"]["median"], metrics[metric]["B"]["median"]))
        operation_runs = {side: [([], reason) if reason else read_operations(path.with_name("scene-operations.jsonl"))
                                 for _, path, reason in reports] for side, reports in reports_by_side.items()}
        scenarios[scenario] = {"runs": {side: len(reports) for side, reports in reports_by_side.items()},
                               "metrics": metrics,
                               "openings": {side: summarize_openings(runs) for side, runs in operation_runs.items()}}
        if (scenario in ("scene-node-first", "scene-edge", "scene-follow", "scene-node") or
                scenario.startswith("scene-route-")) and "capture" not in scenario:
            scenarios[scenario]["scene_preparation"] = {
                side: summarize_preparation(reports) for side, reports in reports_by_side.items()}
        if scenario in ("scene-node-first", "scene-map"):
            scenarios[scenario]["memory_stability"] = {
                side: [{"run": index, **memory_window(path, records, reason or problem,
                                                    "final-map" if scenario == "scene-map" else None)}
                       for index, ((_, path, reason), (records, problem)) in enumerate(
                           zip(reports, operation_runs[side]), 1)]
                for side, reports in reports_by_side.items()}
        if scenario == "scene-soak":
            scenarios[scenario]["hourly_memory"] = {
                side: summarize_hourly_memory(runs) for side, runs in operation_runs.items()}
    return {"inputs": {side: str(directory) for side, directory in sides.items()},
            "input_status": input_status, "scenarios": scenarios}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("a", type=Path)
    parser.add_argument("b", type=Path, nargs="?")
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    directories = [arguments.a] + ([arguments.b] if arguments.b is not None else [])
    result = summarize_directories(directories)
    content = json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
    try:
        with arguments.output.open("x", encoding="utf-8") as output:
            output.write(content)
    except OSError as error:
        parser.error(f"无法创建输出文件（须使用新文件）：{error}")


if __name__ == "__main__":
    main()
