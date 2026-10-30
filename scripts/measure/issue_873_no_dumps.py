"""Three-cycle GC observation run: deliberately no heap tooling or capacity probes."""
import json
import os
import time
from issue_873_diagnostics import publish_sources
from issue_873_processes import launch, stop_capture
from issue_873_progress import ObservationProgress, emit_progress


def release(path, value):
    partial = path.with_suffix(path.suffix + ".partial")
    partial.write_text(value, encoding="utf-8")
    partial.replace(path)


def run_no_dumps(options, root, scratch, output, started, started_utc):
    report = {"diagnosis_only": True, "no_dumps": True, "expected_cycles": 3, "reached_cycles": [],
              "succeeded": False, "gc_data_succeeded": False, "stage": "measurement",
              "acquisition_timeout_seconds": 3000, "captures_succeeded": None, "analyses": [], "samples": []}
    game_ids = set()
    progress = ObservationProgress(scratch, game_ids, started, 3)
    measurement = None
    active_cycle = None
    try:
        if options.cache is None:
            raise ValueError("cache required")
        environment = os.environ.copy()
        environment["TRAIN_HEAP_DIAGNOSIS"] = str(scratch)
        measurement = launch(["pwsh", "-NoProfile", "-File", str(root / "scripts/measure/run-measurement.ps1"),
                              "-CacheDirectory", str(options.cache.resolve()), "-Scenario", "scene-soak",
                              "-InputPath", "examples/standard-world/save.json", "-Repeat", "1",
                              "-DurationSeconds", "9000", "-Root", str(root)], scratch / "measurement-process.log", environment)
        deadline = started + 50 * 60
        for cycle in range(1, 4):
            active_cycle = cycle
            marker = scratch / f"reset-{cycle}.ready"
            while True:
                progress.poll()
                if marker.is_file():
                    break
                if measurement.poll() is not None:
                    raise ChildProcessError("measurement exited before sync")
                if time.monotonic() >= deadline:
                    raise TimeoutError("sync deadline")
                time.sleep(0.1)
            process_id = int(marker.read_text(encoding="utf-8"))
            if process_id <= 0:
                raise ValueError("invalid process id")
            game_ids.add(process_id)
            progress.sync(cycle)
            release(scratch / f"reset-{cycle}.release", "continue")
            report["reached_cycles"].append(cycle)
            report["samples"].append({"cycle": cycle, "process_id": process_id, "released": True})
        report["stage"] = "completed"
    except Exception as error:
        report["failure_type"] = type(error).__name__
        report["failed_cycle"] = active_cycle
        emit_progress("failure", cycle=active_cycle, seconds=time.monotonic() - started, failure_type=type(error).__name__)
    finally:
        try:
            progress.poll()
        except Exception as error:
            report["progress_error_type"] = type(error).__name__
            emit_progress("failure", seconds=time.monotonic() - started, failure_type=type(error).__name__)
        if measurement is not None:
            stop_capture(measurement, game_ids, scratch, report)
        cleanup_errors = [report[key] for key in ("measurement_stop_error", "game_stop_error") if key in report]
        for failure in cleanup_errors:
            emit_progress("failure", seconds=time.monotonic() - started, failure_type=failure)
        report["cleanup_error_types"] = cleanup_errors
        try:
            report["gc_data_succeeded"] = publish_sources(root, scratch, output, game_ids, started_utc, report, expected_cycles=3)
        except Exception as error:
            report["source_publish_error_type"] = type(error).__name__
            emit_progress("failure", seconds=time.monotonic() - started, failure_type=type(error).__name__)
        report["succeeded"] = (report["gc_data_succeeded"] and report["reached_cycles"] == [1, 2, 3]
                               and not progress.errors and not cleanup_errors and "failure_type" not in report
                               and "measurement_stop_error" not in report and "game_stop_error" not in report)
        report["game_process_ids"] = sorted(game_ids)
        (output / "acquisition.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
        emit_progress("summary", seconds=time.monotonic() - started, succeeded=report["succeeded"])
    return 0 if report["succeeded"] else 1
