"""ArrayPool event counts; hash correlations are observations, not object identities."""
from collections import Counter
import json


EVENTS = ("BufferRented", "BufferAllocated", "BufferReturned", "BufferTrimmed", "BufferDropped")


def call_site(stack):
    return tuple(frame for frame in stack if not frame.startswith(
        ("System.", "Microsoft.", "Train.App.Measurement.ArrayPoolDiagnosis.")))[:4]


def summarize(rows, observations, collections, game_ids, expected_cycles):
    result = {"status": "observed", "long_game_process_ids": sorted(game_ids),
              "reference_process_ids": sorted({r["process_id"] for r in rows
                  if type(r.get("process_id")) is int and r["process_id"] not in game_ids}),
              "processes": [], "caveat": "buffer_id 是 hash，可能复用；同 pool/id 关联不能证明对象身份或 live 留存。归还线程关联记在此前候选租借的区间/调用点，归还事件数记在发生区间。大小分布分别统计 Rented/Allocated 事件；事件次数不是唯一数组数。未知元素大小仅计元素数，不折算字节。"}
    for pid in sorted(game_ids):
        own = [r for r in rows if r.get("process_id") == pid]
        starts = [r for r in own if r.get("event") == "listener-started"]
        frequency = starts[0].get("stopwatch_frequency") if starts else None
        boundaries = []
        for cycle in range(1, expected_cycles + 1):
            matching = [o for o in observations if o.get("process_id") == pid
                        and o.get("cycle") == cycle and o.get("phase") == "reset-map"
                        and o.get("stopwatch_frequency") == frequency
                        and any((c.get("cycle"), c.get("phase"), c.get("at")) ==
                                (cycle, "reset-map", o.get("collection_at")) for c in collections)]
            boundaries.append(matching[0]["collection_at"] * frequency if len(matching) == 1 else None)
        complete = (bool(frequency) and all(b is not None for b in boundaries)
                    and all(a < b for a, b in zip(boundaries, boundaries[1:])))
        process = {"process_id": pid, "status": "observed" if starts and complete else "unknown",
                   "listener_errors": [r for r in own if r.get("event") == "listener-error"],
                   "reset_gc_timestamps": boundaries, "intervals": []}
        if process["listener_errors"]:
            process["status"] = "unknown"
        buckets = {}
        rentals = {}
        for row in sorted(own, key=lambda r: r.get("timestamp", 0)):
            event = row.get("event")
            if event not in EVENTS:
                continue
            stamp = row.get("timestamp")
            clock_ok = complete and row.get("stopwatch_frequency") == frequency and type(stamp) in (int, float)
            if clock_ok:
                index = next((i for i, end in enumerate(boundaries) if stamp < end), expected_cycles)
                interval = (f"startup→reset1" if index == 0 else
                            f"reset{index}→reset{index + 1}" if index < expected_cycles else
                            f"reset{expected_cycles}→tail")
            else:
                interval = "unknown-clock"
                process["status"] = "unknown"
            bucket = buckets.setdefault(interval, {"interval": interval, "events": Counter(), "sites": {}})
            bucket["events"][event] += 1
            key = (row.get("pool_id"), row.get("buffer_id"))
            if event in ("BufferRented", "BufferAllocated"):
                site = call_site(row.get("stack", []))
                group = bucket["sites"].setdefault(site, {"call_site": list(site),
                    "representative_stack": row.get("stack", []), "events": Counter(),
                    "size_by_event": {},
                    "rent_threads": set(), "return_threads": set(), "cross_thread_return_associations": 0})
                group["events"][event] += 1
                size = row.get("size_bytes")
                distribution = "exact_size_bytes" if type(size) is int else "unknown_buffer_lengths"
                sizes = group["size_by_event"].setdefault(event, {
                    "exact_size_bytes": Counter(), "unknown_buffer_lengths": Counter()})
                sizes[distribution][str(size if type(size) is int else row.get("buffer_length"))] += 1
                if event == "BufferRented":
                    group["rent_threads"].add(row.get("thread_id"))
                    rentals.setdefault(key, []).append((group, row.get("thread_id")))
            elif event == "BufferReturned":
                # All prior matching rents are candidates: repeated hashes cannot be disambiguated.
                for group, thread in rentals.pop(key, []):
                    group["return_threads"].add(row.get("thread_id"))
                    group["cross_thread_return_associations"] += int(thread != row.get("thread_id"))
        for bucket in buckets.values():
            bucket["sites"] = list(bucket["sites"].values())
            for group in bucket["sites"]:
                for name in ("rent_threads", "return_threads"):
                    group[name] = sorted(group[name], key=str)
                    group[name.replace("threads", "thread_count")] = len(group[name])
            process["intervals"].append(bucket)
        result["processes"].append(process)
    if not result["processes"] or any(p["status"] == "unknown" for p in result["processes"]):
        result["status"] = "unknown"
    return result


def write_summary(output, rows, observations, collections, game_ids, expected_cycles, source_errors=()):
    result = summarize(rows, observations, collections, game_ids, expected_cycles)
    result["source_errors"] = list(source_errors)
    if source_errors:
        result["status"] = "unknown"
    (output / "array-pool-summary.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    lines = ["# ArrayPool 观测", "", f"状态：{result['status']}。reference PID：{result['reference_process_ids']}（不计入下表）。",
             result["caveat"], "", "区间右端不含边界；边界后的 idle 归入下一段，末轮后归 tail。调用点去除 System/Microsoft/诊断帧后取前四帧；完整代表栈见 JSON。",
             "", "| PID | 区间 | Rented | Allocated | Returned | Trimmed | Dropped |", "| --- | --- | --- | --- | --- | --- | --- |"]
    for process in result["processes"]:
        for interval in process["intervals"]:
            counts = interval["events"]
            lines.append(f"| {process['process_id']} | {interval['interval']} | " + " | ".join(str(counts.get(e, 0)) for e in EVENTS) + " |")
    lines += ["", "| PID / 区间 | 调用点（前四帧） | Rented | Allocated | Rented 大小分布 | 租借线程数 | 归还线程数 |",
              "| --- | --- | --- | --- | --- | --- | --- |"]
    for process in result["processes"]:
        for interval in process["intervals"]:
            for site in interval["sites"]:
                frames = " → ".join(site["call_site"]).replace("|", "&#124;") or "unknown"
                sizes = json.dumps(site["size_by_event"].get("BufferRented", {}), ensure_ascii=False)
                counts = site["events"]
                lines.append(f"| {process['process_id']} / {interval['interval']} | {frames} | "
                             f"{counts.get('BufferRented', 0)} | {counts.get('BufferAllocated', 0)} | "
                             f"{sizes} | {site['rent_thread_count']} | {site['return_thread_count']} |")
    lines += ["", "按调用点的大小分布、租借线程及跨线程归还候选关联见 array-pool-summary.json。缺 listener、GC 配对或同一频率时为 unknown；没有事件不等于不存在池保留。"]
    (output / "array-pool-summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return result["status"]
