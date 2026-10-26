"""按场景汇总测量输出；原始报告只读。"""
# Decision note: 2026-10-26-performance-measurement-system

import argparse
from collections import Counter
import json
from math import isfinite
from pathlib import Path, PureWindowsPath
from statistics import median

TIMING_METRICS = tuple(
    f"timing.{distribution}.{statistic}"
    for distribution in ("frame_seconds", "simulation_seconds")
    for statistic in ("p50", "p95", "p99", "maximum")
) + tuple(f"timing.{name}" for name in (
    "actual_speed", "window_median_actual_speed", "window_minimum_actual_speed", "elapsed_seconds"))
MEMORY_COUNTERS = ("working_set_bytes", "private_bytes", "peak_working_set_bytes",
                   "godot_static_bytes", "video_memory_bytes")
MEMORY_METRICS = tuple(f"memory.{counter}.{statistic}"
                       for counter in MEMORY_COUNTERS for statistic in ("final", "peak"))


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
    memory = report.get("memory")
    memory = memory if isinstance(memory, list) else []
    for counter in MEMORY_COUNTERS:
        values = [sample.get(counter) if isinstance(sample, dict) else None for sample in memory]
        metrics[f"memory.{counter}.final"] = (values[-1] if values else None, "memory_missing_or_non_numeric")
        peak = max(values) if values and all(is_number(value) for value in values) else None
        metrics[f"memory.{counter}.peak"] = (peak, "memory_missing_or_non_numeric")
    operations = {}
    entries = report.get("operations")
    for operation in entries if isinstance(entries, list) else []:
        if isinstance(operation, dict) and isinstance(operation.get("name"), str):
            operations.setdefault(operation["name"], []).append(operation)
    for name, entries in operations.items():
        durations = [entry.get("duration_seconds") for entry in entries]
        reason = "operation_failed" if any(entry.get("succeeded") is not True for entry in entries) else None
        if reason is None and not all(is_number(value) for value in durations):
            reason = "operation_non_numeric"
        metrics[f"operations.{name}.duration_seconds"] = (None, reason) if reason else (median(durations), None)
    return metrics


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
        return {}, "report_absolute_path"
    try:
        root = directory.resolve()
        report_path = (root / report_name).resolve()
    except (OSError, ValueError):
        return {}, "report_invalid_path"
    if not report_path.is_relative_to(root):
        return {}, "report_outside_directory"
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
    return report_metrics(report), reason


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
        metric_names = set(TIMING_METRICS + MEMORY_METRICS).union(
            *(report for reports in reports_by_side.values() for report, _ in reports))
        for metric in sorted(metric_names):
            metrics[metric] = {}
            for side, reports in reports_by_side.items():
                samples = []
                for report, reason in reports:
                    samples.append((None, reason) if reason else report.get(metric, (None, "metric_missing")))
                metrics[metric][side] = summarize_values(samples)
            if len(sides) == 2:
                metrics[metric].update(compare(metrics[metric]["A"]["median"], metrics[metric]["B"]["median"]))
        scenarios[scenario] = {"runs": {side: len(reports) for side, reports in reports_by_side.items()},
                               "metrics": metrics}
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
