"""Safe diagnostics for the temporary Issue 873 controller."""
from collections import Counter
import json
import os
from pathlib import Path
import re
import socket
import signal
import getpass
import time


def safe_log(raw, scratch):
    text = raw.replace(str(scratch), "<scratch>").replace(str(scratch).replace("\\", "/"), "<scratch>")
    # Paths are removed before identity strings so a username cannot leave a partial path.
    text = re.sub(r'''(["'])(?:[A-Za-z]:[\\/]|\\\\|/)[^\r\n]*?\1''', "<path>", text)
    text = re.sub(r"[A-Za-z]:[\\/][^\r\n\"'<>]*|\\\\[^\r\n\"'<>]+|(?<![\w<])/[^\s\"'<>]*", "<path>", text)
    identities = {socket.gethostname(), getpass.getuser(), *[os.environ.get(key, "") for key in
                  ("USER", "USERNAME", "LOGNAME", "HOSTNAME", "COMPUTERNAME")]}
    if os.name != "nt":
        import pwd
        identities.add(pwd.getpwuid(os.getuid()).pw_name)
    for identity in sorted(identities - {""}, key=len, reverse=True):
        text = re.sub(re.escape(identity), "<identity>", text, flags=re.IGNORECASE)
    return "\n".join(line for line in text.splitlines()
                     if not re.search(r"sudo|environment|environ|\b[A-Z_][A-Z_0-9]*=", line, re.I))


def proc_stat(text):
    # comm may contain spaces or ')'; never return it or the raw stat line.
    fields = text[text.rfind(")") + 2:].split()
    if len(fields) < 2 or not re.fullmatch(r"[RSDZTWXxKPIt]", fields[0]):
        raise ValueError("invalid proc state")
    return fields[0], int(fields[1])


def process_states(collector_pid, game_pid, proc=Path("/proc")):
    processes = {}
    for directory in proc.iterdir():
        if not directory.name.isdigit():
            continue
        try:
            state, parent = proc_stat((directory / "stat").read_text())
            processes[int(directory.name)] = (state, parent, (directory / "comm").read_text().strip() == "createdump")
        except (OSError, ValueError):
            continue
    groups = {}
    for ancestor, root in (("game", game_pid), ("collector", collector_pid)):
        descendants = {root}
        while True:
            added = {pid for pid, (_, parent, _) in processes.items() if parent in descendants} - descendants
            if not added:
                break
            descendants.update(added)
        groups[ancestor] = [{"pid": pid, "state": processes[pid][0], "ancestor": ancestor}
                            for pid in sorted(descendants) if pid in processes and processes[pid][2]]
    states = Counter()
    try:
        tasks = list((proc / str(game_pid) / "task").glob("*/stat"))
        for task in tasks:
            try:
                states[proc_stat(task.read_text())[0]] += 1
            except (OSError, ValueError):
                continue
    except OSError:
        pass
    return {"game_createdump_exists": bool(groups["game"]), "game_createdump": groups["game"],
            "collector_createdump_exists": bool(groups["collector"]), "collector_createdump": groups["collector"],
            "game_task_states": dict(sorted(states.items()))}


def restore_game(game_pid, scratch, proc=Path("/proc")):
    result = {"ownership_verified": False, "createdump_sigkill_sent": [], "kill_errors": [], "sigcont_sent": False}
    try:
        values = (proc / str(game_pid) / "environ").read_bytes().split(b"\0")
        if ("TRAIN_HEAP_DIAGNOSIS=" + str(scratch)).encode() not in values:
            return result
        result["ownership_verified"] = True
        # Recheck the direct parent and comm immediately before signaling an owned game's child.
        state = process_states(game_pid, game_pid, proc)
        for child in state["game_createdump"]:
            try:
                directory = proc / str(child["pid"])
                _, parent = proc_stat((directory / "stat").read_text())
                if parent != game_pid or (directory / "comm").read_text().strip() != "createdump":
                    continue
                os.kill(child["pid"], signal.SIGKILL)
                result["createdump_sigkill_sent"].append(child["pid"])
            except (ProcessLookupError, FileNotFoundError):
                pass
            except (OSError, ValueError) as error:
                result["kill_errors"].append({"pid": child["pid"], "error_type": type(error).__name__})
        deadline = time.monotonic() + 2
        remaining = state["game_createdump"]
        while time.monotonic() < deadline:
            remaining = [child for child in process_states(game_pid, game_pid, proc)["game_createdump"]
                         if child["pid"] in result["createdump_sigkill_sent"] and child["state"] not in ("Z", "X", "x")]
            if not remaining:
                break
            time.sleep(0.05)
        result["createdump_still_running"] = remaining
        os.kill(game_pid, signal.SIGCONT)
        result["sigcont_sent"] = True
        result["after_recovery"] = process_states(game_pid, game_pid, proc)
    except OSError as error:
        result["error_type"] = type(error).__name__
    return result


def read_rows(path, errors=None):
    rows = []
    if not path.is_file():
        return rows
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeError) as error:
        if errors is not None:
            errors.append({"source": path.name, "failure_type": type(error).__name__})
        return rows
    for number, line in enumerate(lines, 1):
        if not line.strip():
            continue
        try:
            row = json.loads(line)
            if not isinstance(row, dict):
                raise ValueError("record must be object")
            rows.append(row)
        except (ValueError, TypeError) as error:
            if errors is not None:
                errors.append({"source": path.name, "line": number, "failure_type": type(error).__name__})
    return rows


def write_timeline(output, game_ids):
    observations = [r for r in read_rows(output / "heap-observations.jsonl") if r.get("process_id") in game_ids]
    collections = []
    for row in read_rows(output / "collections.jsonl"):
        for observed in observations:
            if (row.get("cycle"), row.get("phase"), row.get("at")) == (observed.get("cycle"), observed.get("phase"), observed.get("collection_at")):
                collections.append({**row, "process_id": observed["process_id"],
                    "timestamp": row["at"] * observed["stopwatch_frequency"],
                    "stopwatch_frequency": observed["stopwatch_frequency"]})
    recording = [r for r in read_rows(output / "recording.jsonl") if r.get("process_id") in game_ids]
    lines = ["# GC 与录制时间线", "", "来源：collections.jsonl、heap-observations.jsonl、recording.jsonl（已脱敏）。",
             "只关联本次门控的游戏 PID；不混入 scene-reference。时间差仅用同一 PID 的 Stopwatch timestamp/frequency。",
             "collections 按 cycle、phase、at 与 observation.collection_at 精确配对；collection.at × 配对频率换算时间。",
             "状态读数发生在原 GC 之后，不能代表 GC 当时的 Task 状态。idle GC 是另一次完整 GC。", "",
             "| PID | cycle | 来源/阶段 | 相对原 GC 秒 | 关键数据 |", "| --- | --- | --- | --- | --- |"]
    for pid in sorted(game_ids):
        for cycle in (1, 2):
            original = next((r for r in collections if r.get("process_id") == pid and r.get("cycle") == cycle and r.get("phase") == "reset-map"), None)
            idle = next((r for r in collections if r.get("process_id") == pid and r.get("cycle") == cycle and r.get("phase") == "reset-map-idle"), None)
            observed = [r for r in observations if r.get("process_id") == pid and r.get("cycle") == cycle]
            frequency = (original or {}).get("stopwatch_frequency") or next((r.get("stopwatch_frequency") for r in observed if r.get("stopwatch_frequency")), None)
            base = (original or {}).get("timestamp")
            events = [("collections", r) for r in (original, idle) if r]
            events += [("observations", r) for r in observed]
            # Show all writes against each original boundary, including writes completed before that GC.
            for r in recording:
                timestamp = r.get("timestamp")
                if r.get("process_id") == pid and timestamp is not None:
                    events.append(("recording", r))
            for source, row in sorted(events, key=lambda item: item[1].get("timestamp", 0)):
                delta = (row["timestamp"] - base) / frequency if base is not None and frequency and "timestamp" in row else None
                values = {key: value for key, value in row.items() if key not in ("timestamp", "stopwatch_frequency", "process_id", "cycle", "phase", "utc")}
                detail = json.dumps(values, ensure_ascii=False).replace("|", "&#124;")
                lines.append(f"| {pid} | {cycle} | {source}/{row.get('phase', '?')} | {delta if delta is not None else 'unknown'} | {detail} |")
    lines += ["", "缺失记录或频率显示 unknown，不用 UTC 推算 Stopwatch 时间。同一 PID 的全部录制阶段分别对照两次原 GC；负时间表示原 GC 前，超过 idle 行的时间表示 idle GC 后。操作 id 不是 cycle。"]
    (output / "timeline.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def publish_sources(root, scratch, output, game_ids, started_utc, report=None):
    errors = []
    observations = read_rows(scratch / "heap-observations.jsonl", errors)
    observations = [row for row in observations if row.get("process_id") in game_ids]
    for name in ("heap-observations.jsonl", "recording.jsonl"):
        rows = observations if name.startswith("heap-") else read_rows(scratch / name, errors)
        (output / name).write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
    collections = []
    paired = set()
    for path in root.glob("TestResults/measure-*/scenarios/scene-soak-1/scene-collections.jsonl"):
        if path.stat().st_mtime < started_utc:
            continue
        for row in read_rows(path, errors):
            for observation in observations:
                if (row.get("cycle"), row.get("phase"), row.get("at")) == (observation.get("cycle"), observation.get("phase"), observation.get("collection_at")):
                    # Original source schema is unchanged; PID/clock come from the exact paired observation.
                    collections.append(row)
                    paired.add((observation["process_id"], row["cycle"], row["phase"]))
    (output / "collections.jsonl").write_text("".join(json.dumps(r) + "\n" for r in collections), encoding="utf-8")
    for path in scratch.glob("*-telemetry.json"):
        (output / path.name).write_text(path.read_text(encoding="utf-8"), encoding="utf-8")
    write_timeline(output, game_ids)
    if report is not None:
        report["source_errors"] = errors
    return not errors and any(all((pid, cycle, phase) in paired for cycle in (1, 2)
                   for phase in ("reset-map", "reset-map-idle")) for pid in game_ids)
