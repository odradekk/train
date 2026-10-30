"""Temporary Issue 873 heap collection; never publishes raw process output or dumps."""

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
sys.path.insert(0, str(Path(__file__).resolve().parent))
from issue_873_diagnostics import process_states, restore_game, safe_log, publish_sources
from issue_873_processes import launch, stop, stop_games, stop_capture
from issue_873_progress import ObservationProgress, emit_progress


class ChildFailure(Exception):
    def __init__(self, log, code):
        self.step = log.stem
        self.code = code
        self.exception_types = [line for line in log.read_text(encoding="utf-8", errors="replace").splitlines()
                                if re.fullmatch(r"(?:System|Microsoft)\.[A-Za-z0-9.]+Exception", line)]
        self.diagnostics = []
        for line in log.read_text(encoding="utf-8", errors="replace").splitlines():
            if line.startswith("DIAGNOSTIC_FAILURE "):
                diagnostic = json.loads(line.removeprefix("DIAGNOSTIC_FAILURE "))
                self.diagnostics.append({key: diagnostic[key] for key in ("exception_type", "kind", "kind_available", "hresult", "locations")})


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")


def release_marker(path, value):
    partial = path.with_suffix(path.suffix + ".partial")
    partial.write_text(value)
    partial.replace(path)


def run(arguments, log, timeout=180):
    process = launch(arguments, log)
    try:
        deadline = time.monotonic() + timeout
        while process.poll() is None:
            if time.monotonic() >= deadline:
                raise TimeoutError("child deadline")
            if sys.platform == "linux" and os.environ.get("RUNNER_TEMP") and available_memory() < 2 * 1024**3:
                raise RuntimeError("collection memory reserve exhausted")
            time.sleep(0.2)
        code = process.returncode
        if code != 0:
            raise ChildFailure(log, code)
    finally:
        stop(process)


def wait_file(path, process, deadline, progress=None):
    if progress is not None:
        progress.poll()
    while not path.exists():
        if progress is not None:
            progress.poll()
        if process.poll() is not None:
            raise RuntimeError("child exited before marker")
        if time.monotonic() >= deadline:
            raise TimeoutError("marker deadline")
        if sys.platform == "linux" and available_memory() < 2 * 1024**3:
            raise RuntimeError("collection memory reserve exhausted")
        time.sleep(0.1)


def matching_dac(process_id):
    if sys.platform == "linux":
        for line in Path(f"/proc/{process_id}/maps").read_text().splitlines():
            fields = line.split(maxsplit=5)
            if len(fields) == 6 and fields[5].endswith("/libcoreclr.so"):
                path = Path(fields[5]).with_name("libmscordaccore.so")
                if path.is_file():
                    return path
        raise RuntimeError("matching DAC unavailable")
    runtimes = subprocess.check_output(["dotnet", "--list-runtimes"], text=True)
    candidates = []
    for line in runtimes.splitlines():
        name, version, directory = line.split(maxsplit=2)
        if name == "Microsoft.NETCore.App" and version.startswith("10."):
            candidates.append((tuple(int(v) for v in version.split(".")),
                               Path(directory.strip("[]")) / version / "mscordaccore.dll"))
    path = max(candidates)[1]
    if not path.is_file():
        raise RuntimeError("matching DAC unavailable")
    return path


def collect(tool, process_id, dump, scratch, label, deadline=None, diagnostics=None):
    started = time.monotonic()
    limit = 180 if label == "preflight" else 300
    end = min(started + limit, deadline) if deadline is not None else started + limit
    log = scratch / f"{label}-collect.log"
    telemetry = diagnostics if diagnostics is not None else {}
    telemetry.update(timeout_seconds=max(0, end - started), progress=[], succeeded=False)
    process = launch([str(tool), "collect", "--type", "Heap", "--process-id", str(process_id),
                      "--output", str(dump)], log)
    next_sample = started
    try:
        while True:
            now = time.monotonic()
            if now >= next_sample or process.poll() is not None or now >= end:
                telemetry["progress"].append({"utc": datetime.now(timezone.utc).isoformat(),
                    "seconds": now - started, "dump_bytes": dump.stat().st_size if dump.is_file() else 0})
                next_sample = now + 5
                write_json(scratch / f"{label}-telemetry.json", telemetry)
            if process.poll() is not None:
                if process.returncode != 0:
                    raise ChildFailure(log, process.returncode)
                if not dump.is_file() or dump.stat().st_size == 0:
                    raise RuntimeError("empty dump")
                telemetry["succeeded"] = True
                return {"collection_seconds": now - started, "dump_bytes": dump.stat().st_size}
            if now >= end:
                raise TimeoutError("collection deadline")
            if sys.platform == "linux" and available_memory() < 2 * 1024**3:
                raise RuntimeError("collection memory reserve exhausted")
            time.sleep(0.2)
    except Exception as error:
        record_failure(telemetry, error)
        if sys.platform == "linux":
            try:
                telemetry["before_collector_stop"] = process_states(process.pid, process_id)
            except OSError as diagnostic_error:
                telemetry["proc_error_type"] = type(diagnostic_error).__name__
        raise
    finally:
        stop_error = None
        try:
            stop(process)
        except Exception as error:
            stop_error = error
            telemetry["collector_stop_error_type"] = type(error).__name__
        if not telemetry["succeeded"] and label != "preflight" and sys.platform == "linux":
            try:
                telemetry["recovery"] = restore_game(process_id, scratch)
            except Exception as error:
                telemetry["recovery_error_type"] = type(error).__name__
        telemetry["collection_seconds"] = time.monotonic() - started
        try:
            telemetry["output"] = safe_log(log.read_text(encoding="utf-8", errors="replace"), scratch) if log.is_file() else ""
        except Exception as error:
            telemetry["output_error_type"] = type(error).__name__
        cleanup_failed_after_success = stop_error is not None and telemetry["succeeded"]
        if cleanup_failed_after_success:
            telemetry["succeeded"] = False
        write_json(scratch / f"{label}-telemetry.json", telemetry)
        if cleanup_failed_after_success:
            raise RuntimeError("collector cleanup failed") from stop_error


def available_memory():
    values = dict(line.split(":", 1) for line in Path("/proc/meminfo").read_text().splitlines())
    return int(values["MemAvailable"].split()[0]) * 1024


def record_failure(report, error):
    emit_progress("failure", cycle=report.get("cycle"), failure_type=type(error).__name__)
    report["failure_type"] = type(error).__name__
    if isinstance(error, ChildFailure):
        report.update(failed_step=error.step, child_exit_code=error.code,
                      exception_types=error.exception_types, diagnostics=error.diagnostics)


def analyze_captures(analyzer, dac, scratch, output, report):
    reports = []
    report["analyses"] = []
    for cycle in (1, 2):
        entry = {"cycle": cycle, "succeeded": False, "timeout_seconds": 360}
        report["analyses"].append(entry)
        dump = scratch / f"heap-{cycle}.dmp"
        directory = output / f"snapshot-{cycle}"
        if not dump.is_file() or dump.stat().st_size == 0 or dac is None:
            entry["not_started"] = True
            continue
        started = time.monotonic()
        try:
            run(["dotnet", str(analyzer), "analyze-one", str(dump), str(dac), str(directory)],
                scratch / f"heap-{cycle}-analysis.log", timeout=360)
            heap_report = json.loads((directory / "heap.json").read_text(encoding="utf-8"))
            reports.append(heap_report)
            entry["succeeded"] = True
            entry["issue_count"] = sum(heap_report["Issues"].values())
        except Exception as error:
            record_failure(entry, error)
        entry["seconds"] = time.monotonic() - started
        write_json(output / "acquisition.json", report)
    succeeded = all(entry["succeeded"] for entry in report["analyses"])
    if succeeded:
        before, after = (item["Types"] for item in reports)
        difference = []
        for name in sorted(before.keys() | after.keys()):
            a, b = before.get(name, {}), after.get(name, {})
            difference.append({"Type": name, "Count": b.get("Count", 0) - a.get("Count", 0),
                               "Bytes": b.get("Bytes", 0) - a.get("Bytes", 0)})
        write_json(output / "difference.json", {"partial": any(item["Issues"] for item in reports), "types": difference})
    return succeeded


def main():
    started = time.monotonic()
    started_utc = time.time()
    parser = argparse.ArgumentParser(prog="issue-873-heap")
    parser.add_argument("--scratch", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cache", type=Path)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--preflight-only", action="store_true")
    modes.add_argument("--no-dumps", action="store_true")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    scratch = options.scratch.resolve()
    output = options.output.resolve()
    if scratch == root or root in scratch.parents:
        raise ValueError("dumps must be outside the checkout")
    if not options.preflight_only:
        if sys.platform != "linux" or not os.environ.get("RUNNER_TEMP"):
            raise ValueError("game collection requires Linux runner")
        if Path(os.environ["RUNNER_TEMP"]).resolve() not in scratch.parents:
            raise ValueError("dumps must be in RUNNER_TEMP")
    scratch.mkdir(parents=True, exist_ok=True)
    output.mkdir(parents=True, exist_ok=True)
    if options.no_dumps:
        from issue_873_no_dumps import run_no_dumps
        return run_no_dumps(options, root, scratch, output, started, started_utc)
    report = {"no_dumps": False, "expected_cycles": 2, "reached_cycles": [], "diagnosis_only": True, "stage": "prepare", "succeeded": False, "samples": []}
    measurement = preflight = None
    game_ids = set()
    sources_published = False
    progress = ObservationProgress(scratch, game_ids, started, 2)
    try:
        run(["dotnet", "build", str(root / "Tools/HeapDiagnosis/Train.HeapDiagnosis.csproj"),
             "-c", "Release", "--nologo", "-o", str(scratch / "analyzer")], scratch / "analyzer-build.log")
        analyzer = scratch / "analyzer/Train.HeapDiagnosis.dll"
        tools = scratch / "tools"
        run(["dotnet", "tool", "install", "dotnet-dump", "--version", "10.0.750501",
             "--tool-path", str(tools)], scratch / "tool-install.log")
        tool = tools / ("dotnet-dump.exe" if os.name == "nt" else "dotnet-dump")
        report["stage"] = "preflight"
        ready, release = scratch / "preflight.ready", scratch / "preflight.release"
        preflight = launch(["dotnet", str(analyzer), "preflight", str(ready), str(release)],
                           scratch / "preflight-process.log")
        try:
            wait_file(ready, preflight, time.monotonic() + 30)
            probe_id = int(ready.read_text())
            dac = matching_dac(probe_id)
            collect(tool, probe_id, scratch / "preflight.dmp", scratch, "preflight")
            run(["dotnet", str(analyzer), "analyze-preflight", str(scratch / "preflight.dmp"),
                 str(dac), str(output)], scratch / "preflight-analysis.log", timeout=120)
        finally:
            release_marker(release, "continue")
            if preflight.poll() is None:
                preflight.wait(timeout=15)
            stop(preflight)
            preflight = None
            (scratch / "preflight.dmp").unlink(missing_ok=True)
        if options.preflight_only:
            report.update(stage="completed", succeeded=True)
            return 0

        report["stage"] = "capacity"
        if available_memory() < 10 * 1024**3 or shutil.disk_usage(scratch).free < 20 * 1024**3:
            raise RuntimeError("insufficient collection capacity")
        if options.cache is None:
            raise ValueError("cache required")
        report["stage"] = "measurement"
        environment = os.environ.copy()
        environment["TRAIN_HEAP_DIAGNOSIS"] = str(scratch)
        measurement = launch([
            "pwsh", "-NoProfile", "-File", str(root / "scripts/measure/run-measurement.ps1"),
            "-CacheDirectory", str(options.cache.resolve()), "-Scenario", "scene-soak",
            "-InputPath", "examples/standard-world/save.json", "-Repeat", "1",
            "-DurationSeconds", "9000", "-Root", str(root),
        ], scratch / "measurement-process.log", environment)
        # Forty minutes of acquisition plus two six-minute analyses reserves job cleanup time.
        deadline = started + 40 * 60
        game_dac = None
        game_dac_source = None
        capture_succeeded = True
        report["gc_data_succeeded"] = False
        try:
            for cycle in (1, 2):
                marker = scratch / f"reset-{cycle}.ready"
                wait_file(marker, measurement, deadline, progress)
                process_id = int(marker.read_text())
                game_ids.add(process_id)
                report["reached_cycles"].append(cycle)
                progress.sync(cycle)
                sample = {"cycle": cycle, "gate_observed_utc": datetime.now(timezone.utc).isoformat()}
                gate_started = time.monotonic()
                try:
                    sample["process_id"] = process_id
                    sample["idle_gate_observed"] = True
                    if not capture_succeeded:
                        sample["collection_skipped"] = True
                        continue
                    current_dac = matching_dac(process_id)
                    if game_dac_source is not None and game_dac_source != current_dac:
                        raise RuntimeError("game runtime changed")
                    game_dac_source = current_dac
                    if game_dac is None:
                        # The measurement runner may delete its export when stopped; retain the exact DAC first.
                        game_dac = scratch / current_dac.name
                        shutil.copyfile(current_dac, game_dac)
                    sample["collection_diagnostics"] = {}
                    sample.update(collect(tool, process_id, scratch / f"heap-{cycle}.dmp", scratch,
                                          f"reset-{cycle}", deadline, sample["collection_diagnostics"]))
                    sample["collection_succeeded"] = True
                except Exception as error:
                    capture_succeeded = False
                    sample["collection_succeeded"] = False
                    record_failure(sample, error)
                    # A partial dump is not a completed capture and must never be analyzed.
                    (scratch / f"heap-{cycle}.dmp").unlink(missing_ok=True)
                finally:
                    if cycle == 1:
                        release_marker(scratch / f"reset-{cycle}.release", "continue")
                    sample["released"] = cycle == 1
                    sample["gate_seconds"] = time.monotonic() - gate_started
                    report["samples"].append(sample)
                    write_json(output / "acquisition.json", report)
            report["second_idle_gate_observed"] = True
        except Exception as error:
            capture_succeeded = False
            record_failure(report, error)
        finally:
            stop_capture(measurement, game_ids, scratch, report)
        report["gc_data_succeeded"] = publish_sources(root, scratch, output, game_ids, started_utc, report)
        sources_published = True
        report["stage"] = "analysis"
        analyses_succeeded = analyze_captures(analyzer, game_dac, scratch, output, report)
        report["captures_succeeded"] = capture_succeeded
        report.update(stage="completed", succeeded=capture_succeeded and analyses_succeeded and report["gc_data_succeeded"])
        return 0 if report["succeeded"] else 1
    except Exception as error:
        record_failure(report, error)
        return 1
    finally:
        for cycle in (1, 2):
            release_marker(scratch / f"reset-{cycle}.release", "abort")
        cleanup_errors = []
        for cleanup in (lambda: stop(preflight), lambda: stop(measurement), lambda: stop_games(game_ids, scratch)):
            try:
                cleanup()
            except Exception as error:
                cleanup_errors.append(type(error).__name__)
        for dump in scratch.glob("*.dmp"):
            try:
                dump.unlink()
            except Exception as error:
                cleanup_errors.append(type(error).__name__)
        report["cleanup_error_types"] = cleanup_errors
        if cleanup_errors:
            report["succeeded"] = False
        if not sources_published:
            try:
                if options.preflight_only:
                    for path in scratch.glob("*-telemetry.json"):
                        shutil.copyfile(path, output / path.name)
                else:
                    report["gc_data_succeeded"] = publish_sources(root, scratch, output, game_ids, started_utc, report)
            except Exception as error:
                report["source_publish_error_type"] = type(error).__name__
                report["succeeded"] = False
        write_json(output / "acquisition.json", report)
        emit_progress("summary", succeeded=report["succeeded"])
        if cleanup_errors:
            raise RuntimeError("diagnostic cleanup failed")


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        emit_progress("failure", failure_type=type(error).__name__)
        sys.exit(1)
