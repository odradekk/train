"""Temporary EventPipe GC graph acquisition; raw graphs never leave scratch."""
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timezone
from issue_873_diagnostics import publish_sources, safe_log
from issue_873_processes import launch, stop, stop_capture
from issue_873_progress import ObservationProgress, emit_progress


def save(path, value):
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")


def collect(host, tool, pid, path, scratch, label, deadline, entry):
    started = time.monotonic()
    log = scratch / f"{label}-collect.log"
    entry.update(succeeded=False, timeout_seconds=max(0, min(180, deadline-started)), dropped_events="unknown")
    try:
        host.run([str(tool), "collect", "--process-id", str(pid), "--output", str(path),
                  "--timeout", "180"], log, timeout=entry["timeout_seconds"])
        if not path.is_file() or not path.stat().st_size:
            raise RuntimeError("empty GC graph")
        entry.update(succeeded=True, graph_bytes=path.stat().st_size)
    except Exception as error:
        host.record_failure(entry, error)
        path.unlink(missing_ok=True)
    finally:
        entry["collection_seconds"] = time.monotonic()-started
        raw = log.read_text(encoding="utf-8", errors="replace") if log.is_file() else ""
        entry["output"] = safe_log(raw, scratch)
        entry["lossy_buffering_reported"] = bool(re.search(r"falling back.*lossy buffering", raw, re.I))
        # Absence of a loss message does not prove that the producer lost no events.
        counts = re.findall(r"\b(\d+)\s+(?:events?\s+(?:were\s+)?)?(?:dropped|lost)\b|\b(?:dropped|lost)\s+(\d+)\s+events?\b", raw, re.I)
        observed = [int(a or b) for a, b in counts]
        entry["observed_event_loss_count"] = max(observed) if observed else "unknown"
        entry["dropped_events"] = entry["observed_event_loss_count"]
        save(scratch / f"{label}-telemetry.json", entry)
    return entry["succeeded"]


def analyze(host, analyzer, scratch, output, captures, report):
    entry = {"succeeded": False, "timeout_seconds": 360}
    report["analyses"].append(entry)
    if not captures:
        entry["not_started"] = True
        return False
    started = time.monotonic()
    log = scratch / "gc-analysis.log"
    try:
        if len(captures) == 2:
            a, b = captures
            arguments = ["analyze-gc-pair", str(a[1]), str(b[1]), str(a[2]), str(b[2]), str(output)]
        else:
            a = captures[0]
            arguments = ["analyze-gc-one", str(a[1]), str(a[2]), str(output / f"snapshot-{a[0]}")]
        host.run(["dotnet", str(analyzer), *arguments], log, timeout=360)
        expected = ([output / "snapshot-1/gc-heap.json", output / "snapshot-3/gc-heap.json", output / "gc-difference.json"]
                    if len(captures) == 2 else [output / f"snapshot-{a[0]}/gc-heap.json"])
        for path in expected:
            json.loads(path.read_text(encoding="utf-8"))
        entry["succeeded"] = True
    except Exception as error:
        host.record_failure(entry, error)
    finally:
        entry["seconds"] = time.monotonic()-started
        entry["output"] = safe_log(log.read_text(encoding="utf-8", errors="replace"), scratch) if log.is_file() else ""
    return entry["succeeded"]


def windows_stop(process, game_ids, report):
    # taskkill /T operates only on these owned roots, including descendants spawned by pwsh.
    errors = []
    for pid in [*(sorted(game_ids)), *([process.pid] if process is not None else [])]:
        result = subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"],
                                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode not in (0, 128):
            errors.append("ProcessLookupError")
    if process is not None:
        try:
            process.wait(timeout=10)
        except Exception as error:
            errors.append(type(error).__name__)
    report["cleanup_error_types"] = errors


def measurement(host, options, root, scratch, environment, deadline):
    if options.first_idle_only:
        directory = root / "TestResults" / ("measure-gcdump-local-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%f"))
        common = ["pwsh", "-NoProfile", "-File", str(root / "scripts/measure/measure.ps1"),
                  "-RepositoryPath", str(root), "-ExecutablePath", str(options.executable.resolve()),
                  "-InputPath", str(root / "examples/standard-world/save.json"), "-Repeat", "1"]
        reference = directory / "reference"
        host.run([*common, "-OutputDirectory", str(reference), "-Scenario", "scene-reference"],
                 scratch / "reference-process.log", timeout=max(0, min(600, deadline-time.monotonic())))
        return launch([*common, "-OutputDirectory", str(directory / "scenarios"), "-Scenario", "scene-soak",
                       "-DurationSeconds", "9000", "-ReferencePath", str(reference / "scene-reference-1/scene-reference.json")],
                      scratch / "measurement-process.log", environment)
    if options.cache is None:
        raise ValueError("cache required")
    return launch(["pwsh", "-NoProfile", "-File", str(root / "scripts/measure/run-measurement.ps1"),
                   "-CacheDirectory", str(options.cache.resolve()), "-Scenario", "scene-soak",
                   "-InputPath", "examples/standard-world/save.json", "-Repeat", "1", "-DurationSeconds", "9000",
                   "-Root", str(root)], scratch / "measurement-process.log", environment)


def run_gcdump(host, options, root, scratch, output, started, started_utc):
    first = options.first_idle_only
    cycles = 1 if first else 3
    report = {"gcdump": True, "diagnosis_only": True, "expected_cycles": cycles, "reached_cycles": [],
              "stage": "prepare", "samples": [], "analyses": [], "succeeded": False, "gc_data_succeeded": False,
              "acquisition_timeout_seconds": 540 if first else 2640}
    process = preflight = None
    game_ids = set()
    captures = []
    progress = ObservationProgress(scratch, game_ids, started, cycles)
    deadline = started + report["acquisition_timeout_seconds"]
    try:
        host.run(["dotnet", "build", str(root / "Tools/HeapDiagnosis/Train.HeapDiagnosis.csproj"),
                  "-c", "Release", "--nologo", "-o", str(scratch / "analyzer")], scratch / "analyzer-build.log")
        analyzer = scratch / "analyzer/Train.HeapDiagnosis.dll"
        host.run(["dotnet", "tool", "install", "dotnet-gcdump", "--version", "10.0.750501",
                  "--tool-path", str(scratch / "tools")], scratch / "tool-install.log")
        tool = scratch / "tools" / ("dotnet-gcdump.exe" if os.name == "nt" else "dotnet-gcdump")
        if not first:
            report["stage"] = "preflight"
            ready, release = scratch / "preflight.ready", scratch / "preflight.release"
            preflight = launch(["dotnet", str(analyzer), "preflight-gc", str(ready), str(release), "600"], scratch / "preflight-process.log")
            host.wait_file(ready, preflight, time.monotonic()+30)
            pid = int(ready.read_text())
            live = int(ready.with_suffix(ready.suffix + ".live").read_text())
            if live <= 0:
                raise ValueError("preflight live bytes unavailable")
            report["preflight_live_bytes_available"] = True
            for cycle in (1, 3):
                path = scratch / f"preflight-{cycle}.gcdump"
                entry = {"cycle": cycle}
                report["samples"].append(entry)
                if not collect(host, tool, pid, path, scratch, f"preflight-{cycle}", deadline, entry):
                    break
                captures.append((cycle, path, live))
            for cycle, path, live_bytes in captures:
                host.run(["dotnet", str(analyzer), "verify-gc-preflight", str(path), str(live_bytes),
                          str(output / "preflight" / f"validation-{cycle}")],
                         scratch / f"preflight-{cycle}-validation.log", timeout=120)
            host.release_marker(release, "continue")
            preflight.wait(timeout=15)
            report["preflight_succeeded"] = len(captures) == 2 and analyze(host, analyzer, scratch, output / "preflight", captures, report)
            for _, path, _ in captures:
                path.unlink(missing_ok=True)
            captures = []
            if not report["preflight_succeeded"]:
                raise RuntimeError("GC graph preflight failed")
            report["succeeded"] = options.gcdump_preflight_only
        if not options.gcdump_preflight_only:
            report["stage"] = "measurement"
            environment = os.environ.copy()
            environment["TRAIN_HEAP_DIAGNOSIS"] = str(scratch)
            process = measurement(host, options, root, scratch, environment, deadline)
            failed = False
            try:
                for cycle in range(1, cycles+1):
                    host.wait_file(scratch / f"reset-{cycle}.ready", process, deadline, progress)
                    pid = int((scratch / f"reset-{cycle}.ready").read_text())
                    if pid <= 0:
                        raise ValueError("invalid process id")
                    game_ids.add(pid)
                    progress.sync(cycle)
                    report["reached_cycles"].append(cycle)
                    entry = {"cycle": cycle, "process_id": pid}
                    report["samples"].append(entry)
                    try:
                        if cycle == 2 or failed:
                            entry["collection_skipped"] = True
                        else:
                            row = progress.latest.get(cycle, {})
                            if row.get("phase") != "reset-map-idle" or type(row.get("managed_live_bytes")) is not int:
                                raise ValueError("idle live bytes unavailable")
                            path = scratch / f"heap-{cycle}.gcdump"
                            if collect(host, tool, pid, path, scratch, f"reset-{cycle}", deadline, entry):
                                captures.append((cycle, path, row["managed_live_bytes"]))
                            else:
                                failed = True
                    except Exception as error:
                        failed = True
                        host.record_failure(entry, error)
                    finally:
                        host.release_marker(scratch / f"reset-{cycle}.release", "continue")
                        entry["released"] = True
                        save(output / "acquisition.json", report)
            finally:
                if sys.platform == "win32":
                    windows_stop(process, game_ids, report)
                else:
                    stop_capture(process, game_ids, scratch, report)
                process = None
            report["gc_data_succeeded"] = publish_sources(root, scratch, output, game_ids, started_utc, report, expected_cycles=cycles)
            report["stage"] = "analysis"
            analyzed = analyze(host, analyzer, scratch, output, captures, report)
            report["succeeded"] = (analyzed and not failed and report["gc_data_succeeded"]
                                   and report["reached_cycles"] == list(range(1, cycles+1)) and not progress.errors)
        report["stage"] = "completed"
    except Exception as error:
        host.record_failure(report, error)
    finally:
        for cycle in range(1, cycles+1):
            marker = scratch / f"reset-{cycle}.release"
            if not marker.exists():
                host.release_marker(marker, "abort")
        host.release_marker(scratch / "preflight.release", "continue")
        cleanup_errors = report.setdefault("cleanup_error_types", [])
        for child in (preflight, process):
            try:
                if sys.platform == "win32" and child is process and child is not None:
                    windows_stop(child, game_ids, report)
                else:
                    stop(child)
            except Exception as error:
                cleanup_errors.append(type(error).__name__)
        for path in scratch.glob("*.gcdump"):
            try:
                path.unlink()
            except Exception as error:
                cleanup_errors.append(type(error).__name__)
        try:
            if options.gcdump_preflight_only:
                for path in scratch.glob("*-telemetry.json"):
                    (output / path.name).write_text(path.read_text(encoding="utf-8"), encoding="utf-8")
            else:
                report["gc_data_succeeded"] = publish_sources(root, scratch, output, game_ids, started_utc, report, expected_cycles=cycles)
        except Exception as error:
            host.record_failure(report, error)
            report["succeeded"] = False
        if cleanup_errors or any(key in report for key in ("measurement_stop_error", "game_stop_error")):
            report["succeeded"] = False
        report["seconds"] = time.monotonic()-started
        save(output / "acquisition.json", report)
        emit_progress("summary", succeeded=report["succeeded"])
    return 0 if report["succeeded"] else 1
