"""Temporary Issue 873 heap collection; never publishes raw process output or dumps."""

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import time


class ChildFailure(Exception):
    def __init__(self, log, code):
        self.step = log.stem
        self.code = code
        self.exception_types = [line for line in log.read_text(encoding="utf-8", errors="replace").splitlines()
                                if re.fullmatch(r"(?:System|Microsoft)\.[A-Za-z0-9.]+Exception", line)]


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")


def release_marker(path, value):
    partial = path.with_suffix(path.suffix + ".partial")
    partial.write_text(value)
    partial.replace(path)


def launch(arguments, log, environment=None):
    stream = log.open("w", encoding="utf-8")
    try:
        process = subprocess.Popen(
            arguments, stdout=stream, stderr=subprocess.STDOUT, env=environment,
            start_new_session=os.name != "nt",
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
    finally:
        stream.close()
    return process


def stop(process):
    if process is None:
        return
    if os.name != "nt":
        # Every process launched here owns a fresh process group, including descendants.
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            return
        time.sleep(1)
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    elif process.poll() is None:
        process.kill()
    process.wait(timeout=10)


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


def wait_file(path, process, deadline):
    while not path.exists():
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


def collect(tool, process_id, dump, scratch, label):
    started = time.monotonic()
    run([str(tool), "collect", "--type", "Heap", "--process-id", str(process_id),
         "--output", str(dump)], scratch / f"{label}-collect.log", timeout=180)
    if not dump.is_file() or dump.stat().st_size == 0:
        raise RuntimeError("empty dump")
    return {"collection_seconds": time.monotonic() - started, "dump_bytes": dump.stat().st_size}


def available_memory():
    values = dict(line.split(":", 1) for line in Path("/proc/meminfo").read_text().splitlines())
    return int(values["MemAvailable"].split()[0]) * 1024


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--scratch", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cache", type=Path)
    parser.add_argument("--preflight-only", action="store_true")
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
    report = {"diagnosis_only": True, "stage": "prepare", "succeeded": False, "samples": []}
    measurement = preflight = None
    game_ids = set()
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
        deadline = time.monotonic() + 40 * 60
        game_dac = None
        for cycle in (1, 2):
            marker = scratch / f"reset-{cycle}.ready"
            wait_file(marker, measurement, deadline)
            process_id = int(marker.read_text())
            game_ids.add(process_id)
            sample = {"cycle": cycle, "gate_observed_utc": datetime.now(timezone.utc).isoformat()}
            gate_started = time.monotonic()
            try:
                if available_memory() < 2 * 1024**3:
                    raise RuntimeError("collection memory reserve exhausted")
                current_dac = matching_dac(process_id)
                if game_dac is not None and game_dac != current_dac:
                    raise RuntimeError("game runtime changed")
                game_dac = current_dac
                sample.update(collect(tool, process_id, scratch / f"heap-{cycle}.dmp", scratch, f"reset-{cycle}"))
                release_marker(scratch / f"reset-{cycle}.release", "continue")
                sample["released"] = True
            finally:
                release = scratch / f"reset-{cycle}.release"
                if not release.exists():
                    release_marker(release, "abort")
                sample["gate_seconds"] = time.monotonic() - gate_started
                report["samples"].append(sample)
                write_json(output / "acquisition.json", report)
        if measurement.wait(timeout=180) != 0:
            raise RuntimeError("diagnostic soak failed")
        stop(measurement)
        measurement = None
        report["stage"] = "analysis"
        run(["dotnet", str(analyzer), "analyze", str(scratch / "heap-1.dmp"),
             str(scratch / "heap-2.dmp"), str(game_dac), str(output)],
            scratch / "heap-analysis.log", timeout=300)
        report.update(stage="completed", succeeded=True)
        return 0
    except Exception as error:
        report["failure_type"] = type(error).__name__
        if isinstance(error, ChildFailure):
            report.update(failed_step=error.step, child_exit_code=error.code, exception_types=error.exception_types)
        print("heap diagnosis failed", report["stage"], report["failure_type"])
        return 1
    finally:
        for cycle in (1, 2):
            release_marker(scratch / f"reset-{cycle}.release", "abort")
        stop(preflight)
        stop(measurement)
        if sys.platform == "linux":
            for process_id in game_ids:
                environ = Path(f"/proc/{process_id}/environ")
                if environ.exists() and ("TRAIN_HEAP_DIAGNOSIS=" + str(scratch)).encode() in environ.read_bytes().split(b"\0"):
                    try:
                        os.kill(process_id, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
        for dump in scratch.glob("*.dmp"):
            dump.unlink()
        if (scratch / "recording.jsonl").is_file():
            shutil.copyfile(scratch / "recording.jsonl", output / "recording.jsonl")
        for path in root.glob("TestResults/measure-*/scenarios/scene-soak-1/scene-collections.jsonl"):
            shutil.copyfile(path, output / "collections.jsonl")
        write_json(output / "acquisition.json", report)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print("heap controller failed", type(error).__name__)
        sys.exit(1)
