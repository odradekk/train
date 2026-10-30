"""Owned diagnostic process lifecycle; no collection or analysis."""
import os
from pathlib import Path
import signal
import subprocess
import sys
import time


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

def stop_games(game_ids, scratch):
    if sys.platform != "linux":
        return
    for process_id in game_ids:
        try:
            values = Path(f"/proc/{process_id}/environ").read_bytes().split(b"\0")
            if ("TRAIN_HEAP_DIAGNOSIS=" + str(scratch)).encode() in values:
                os.kill(process_id, signal.SIGKILL)
        except ProcessLookupError:
            pass
        except FileNotFoundError:
            pass

def stop_capture(measurement, game_ids, scratch, report):
    report["measurement_exit_before_stop"] = measurement.poll()
    try:
        stop(measurement)
    except Exception as error:
        report["measurement_stop_error"] = type(error).__name__
    try:
        stop_games(game_ids, scratch)
    except Exception as error:
        report["game_stop_error"] = type(error).__name__
    report["measurement_exit_after_stop"] = measurement.poll()
