"""Small, flushed progress records from complete observation lines only."""
import json
import time
import math

STATES = ("PendingIsNull", "SnapshotBindingEqualsCurrent", "WorkIsCompleted")
PHASES = {"reset-map", "reset-map-idle", "sync", "failure", "summary"}
STARTED = time.monotonic()


def valid_observation(row, expected_cycles):
    return (isinstance(row, dict) and type(row.get("process_id")) is int and row["process_id"] > 0
            and type(row.get("cycle")) is int and 1 <= row["cycle"] <= expected_cycles
            and row.get("phase") in ("reset-map", "reset-map-idle")
            and type(row.get("managed_live_bytes")) is int and row["managed_live_bytes"] >= 0
            and type(row.get("timestamp")) is int and type(row.get("stopwatch_frequency")) is int and row["stopwatch_frequency"] > 0
            and all(type(row.get(key)) in (int, float) and math.isfinite(row[key]) for key in ("at", "collection_at"))
            and isinstance(row.get("recording"), dict)
            and all(key in row["recording"] and (row["recording"][key] is None or type(row["recording"][key]) is bool) for key in STATES))


def emit_progress(phase, *, cycle=None, seconds=None, live=None, recording=None, failure_type=None, succeeded=None):
    if phase not in PHASES:
        raise ValueError("unknown progress phase")
    if seconds is None:
        seconds = max(0, time.monotonic() - STARTED)
    value = {"phase": phase}
    for key, item in (("cycle", cycle), ("seconds", seconds), ("managed_live_bytes", live)):
        if type(item) in (int, float):
            value[key] = round(item, 3) if key == "seconds" else item
    if recording is not None:
        value["recording"] = {key: recording.get(key) if type(recording.get(key)) is bool else None for key in STATES}
    if failure_type is not None:
        # Exception class names only; never Message, log text, or a source path.
        value["failure_type"] = failure_type if failure_type.isidentifier() else "InvalidFailureType"
    if type(succeeded) is bool:
        value["succeeded"] = succeeded
    print(json.dumps(value, separators=(",", ":")), flush=True)


class ObservationProgress:
    def __init__(self, scratch, game_ids, started, expected_cycles):
        self.path = scratch / "heap-observations.jsonl"
        self.game_ids = game_ids
        self.started = started
        self.expected_cycles = expected_cycles
        self.offset = 0
        self.line = 0
        self.latest = {}
        self.errors = []

    def poll(self):
        try:
            with self.path.open("rb") as stream:
                stream.seek(self.offset)
                data = stream.read()
        except FileNotFoundError:
            return
        complete = data.rfind(b"\n") + 1
        self.offset += complete
        for line in data[:complete].splitlines():
            self.line += 1
            if not line.strip():
                continue
            try:
                row = json.loads(line)
                if not valid_observation(row, self.expected_cycles):
                    raise ValueError("invalid observation")
                self.game_ids.add(row["process_id"])
                self.latest[row["cycle"]] = row
                emit_progress(row["phase"], cycle=row["cycle"], seconds=time.monotonic() - self.started,
                              live=row.get("managed_live_bytes"), recording=row.get("recording") if isinstance(row.get("recording"), dict) else {})
            except (ValueError, TypeError, UnicodeError) as error:
                self.errors.append({"source": "heap-observations.jsonl", "line": self.line, "failure_type": type(error).__name__})
                emit_progress("failure", seconds=time.monotonic() - self.started, failure_type=type(error).__name__)

    def sync(self, cycle):
        self.poll()
        row = self.latest.get(cycle, {})
        if row.get("phase") != "reset-map-idle":
            row = {}
        emit_progress("sync", cycle=cycle, seconds=time.monotonic() - self.started,
                      live=row.get("managed_live_bytes"), recording=row.get("recording") if isinstance(row.get("recording"), dict) else {})
