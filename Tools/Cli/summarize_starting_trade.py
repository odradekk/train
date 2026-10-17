#!/usr/bin/env python3
"""Summarize one four-game-hour starting-trade CLI run; Python standard library only.

Usage: python Tools/Cli/summarize_starting_trade.py input.json output-directory
Reads public samples/events only. Does not read checkpoint payloads or calculate prices.
"""
import csv
import json
import sys
from collections import defaultdict
from pathlib import Path

HOUR = 216000


def position(value):
    return value["completed_steps"], value["sequence"]


def good(value):
    return value["kind"] + ":" + (value.get("definition") or value.get("composition") or "")


def facility(value):
    return value["building"], value["capability"]


def check(condition, message):
    if not condition:
        raise ValueError(message)


def write_csv(directory, name, rows):
    check(bool(rows), f"没有可写入的 {name} 数据")
    with (directory / name).open("w", encoding="utf-8-sig", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def main(source, destination):
    data = json.loads(Path(source).read_text(encoding="utf-8-sig"))
    samples = sorted(data["samples"], key=lambda s: position(s["position"]))
    check(samples and position(samples[0]["position"]) == (0, 0), "需要从 (0,0) 开始的完整 starting-trade 输出")
    check(position(samples[-1]["position"]) == (4 * HOUR, 0), "需要恰好四游戏小时的完整输出")
    boundaries = []
    for hour in range(5):
        found = [s for s in samples if position(s["position"]) == (hour * HOUR, 0)]
        check(len(found) == 1, f"小时 {hour} 边界样本缺失或重复")
        boundaries.append(found[0])
    transfers = data["goods_transfers"]
    completions = data["production_completions"]
    changes = data["production_state_changes"]
    for name, events in (("goods_transfers", transfers), ("production_completions", completions),
                         ("production_state_changes", changes)):
        keys = [(position(e["position"]), e["ordinal"]) for e in events]
        check(len(keys) == len(set(keys)), f"{name} 存在重复事件")
        check(all((0, 0) < p <= (4 * HOUR, 0) for p, _ in keys), f"{name} 存在范围外事件")

    def in_hour(event, hour):
        return ((hour - 1) * HOUR, 0) < position(event["position"]) <= (hour * HOUR, 0)

    def event_hour(event):
        p = position(event["position"])
        return p[0] // HOUR + (p[0] % HOUR != 0 or p[1] > 0)

    train_ids = sorted({t["id"] for s in samples for t in s["trains"]})
    check(train_ids, "没有列车")
    check(all(t["action"] in ("Buy", "Sell", "Load", "Unload") for t in transfers), "未知成交方向")
    check(all(t["train"] in train_ids for t in transfers), "成交引用未采样列车")
    # Reports are retained only in a bounded recent list. Collect across every sample,
    # and require an unbroken run chain rather than silently accepting missing loops.
    reports = {train: {} for train in train_ids}
    first_start = {}
    report_checks = []
    previous_totals = {train: (0, 0) for train in train_ids}
    for sample in samples:
        at = position(sample["position"])
        present = {t["id"]: t for t in sample["trains"]}
        for train_id, train in present.items():
            current = train["current_run"]
            if current:
                first_start[train_id] = min(first_start.get(train_id, position(current["started_at"])),
                                            position(current["started_at"]))
            for report in train["recent_reports"]:
                check(report["train"] == train_id, "报告列车身份不符")
                key = (position(report["totals"]["started_at"]), position(report["ended_at"]))
                check(key[0] <= key[1] <= at, f"列车 {train_id} 报告位置无效")
                previous = reports[train_id].get(key)
                check(previous is None or previous == report, f"列车 {train_id} 同一运行报告内容不一致")
                reports[train_id][key] = report
        if at not in {(h * HOUR, 0) for h in range(1, 5)}:
            continue
        hour = at[0] // HOUR
        for train_id in train_ids:
            train = present.get(train_id)
            check(train is not None, f"小时 {hour} 未覆盖列车 {train_id}")
            chain = sorted(reports[train_id].items())
            current = train["current_run"]
            check(train_id in first_start, f"列车 {train_id} 缺少初始运行累计样本，无法排除报告丢失")
            cursor = first_start[train_id]
            sales = purchases = 0
            for (started, ended), report in chain:
                check(started == cursor, f"列车 {train_id} 报告丢失或断档：{cursor} -> {started}")
                cursor = ended
                sales += report["totals"]["sales_receipts"]
                purchases += report["totals"]["purchase_payments"]
            if current:
                check(position(current["started_at"]) == cursor, f"列车 {train_id} 当前运行前存在报告丢失或断档")
                sales += current["sales_receipts"]
                purchases += current["purchase_payments"]
            observed = [e for e in transfers if e["train"] == train_id and position(e["position"]) <= at]
            event_sales = sum(e["actual_amount"] for e in observed if e["action"] == "Sell")
            event_purchases = sum(e["actual_amount"] for e in observed if e["action"] == "Buy")
            check((sales, purchases) == (event_sales, event_purchases),
                  f"小时 {hour} 列车 {train_id} 报告累计与成交不符：报告 {(sales, purchases)}，成交 {(event_sales, event_purchases)}")
            old_sales, old_purchases = previous_totals[train_id]
            hour_events = [e for e in observed if in_hour(e, hour)]
            hour_sales = sum(e["actual_amount"] for e in hour_events if e["action"] == "Sell")
            hour_purchases = sum(e["actual_amount"] for e in hour_events if e["action"] == "Buy")
            check((sales - old_sales, purchases - old_purchases) == (hour_sales, hour_purchases),
                  f"小时 {hour} 列车 {train_id} 报告小时增量与成交不符")
            previous_totals[train_id] = sales, purchases
            report_checks.append({"hour": hour, "train": train_id, "sales_receipts": sales,
                                  "purchase_payments": purchases, "hour_sales_receipts": hour_sales,
                                  "hour_purchase_payments": hour_purchases, "reports": len(chain), "matched": True})

    trade_rows = []
    for event in sorted(transfers, key=lambda e: (position(e["position"]), e["ordinal"])):
        trade_rows.append({"hour": event_hour(event), "completed_steps": position(event["position"])[0],
                           "sequence": position(event["position"])[1], "ordinal": event["ordinal"],
                           **{k: event[k] for k in ("train", "settlement", "action", "quantity", "actual_amount", "source", "destination")},
                           "good": good(event["good"])})
    hourly = []
    market_rows = []
    facility_rows = []
    needs_rows = []
    state_rows = []
    for event in changes:
        state_rows.append({"hour": event_hour(event), "completed_steps": position(event["position"])[0],
                           "sequence": position(event["position"])[1], "ordinal": event["ordinal"],
                           "building": facility(event["facility"])[0], "capability": facility(event["facility"])[1],
                           "before": event["before"], "after": event["after"],
                           "reasons": json.dumps(event["reasons"], ensure_ascii=False)})
    wallets = []
    for hour in range(1, 5):
        start, end = boundaries[hour - 1:hour + 1]
        trades = [e for e in transfers if in_hour(e, hour)]
        produced = [e for e in completions if in_hour(e, hour)]
        sales = sum(e["actual_amount"] for e in trades if e["action"] == "Sell")
        purchases = sum(e["actual_amount"] for e in trades if e["action"] == "Buy")
        delta = end["wallet"] - start["wallet"]
        check(delta == sales - purchases, f"小时 {hour} 钱包变化 {delta} != 成交净额 {sales - purchases}")
        wallets.append({"hour": hour, "wallet_start": start["wallet"], "wallet_end": end["wallet"],
                        "sales_receipts": sales, "purchase_payments": purchases, "net": delta, "matched": True})
        for train in train_ids:
            selected = [e for e in trades if e["train"] == train]
            sell = [e for e in selected if e["action"] == "Sell"]
            buy = [e for e in selected if e["action"] == "Buy"]
            hourly.append({"hour": hour, "train": train, "wallet_start": start["wallet"], "wallet_end": end["wallet"],
                           "sell_quantity": sum(e["quantity"] for e in sell), "sell_amount": sum(e["actual_amount"] for e in sell),
                           "buy_quantity": sum(e["quantity"] for e in buy), "buy_amount": sum(e["actual_amount"] for e in buy),
                           "net": sum(e["actual_amount"] for e in sell) - sum(e["actual_amount"] for e in buy)})
        start_settlements = {s["id"]: s for s in start["settlements"]}
        end_settlements = {s["id"]: s for s in end["settlements"]}
        check(start_settlements.keys() == end_settlements.keys(), "聚居地集合变化，超出 starting-trade 分析范围")
        check(all(e["settlement"] in end_settlements for e in trades), "成交引用未采样聚居地")
        facility_keys = {facility(f["key"]) for s in end_settlements.values() for f in s["facilities"]}
        check(all(facility(e["facility"]) in facility_keys for e in produced), "生产事件引用未采样设施")
        for settlement_id, after in end_settlements.items():
            before = start_settlements[settlement_id]
            bf = {facility(f["key"]): f for f in before["facilities"]}
            af = {facility(f["key"]): f for f in after["facilities"]}
            check(bf.keys() == af.keys(), "设施集合变化，超出 starting-trade 分析范围")
            output = defaultdict(int)
            for key, f in af.items():
                completed = [e for e in produced if facility(e["facility"]) == key]
                quantities = defaultdict(int)
                for event in completed:
                    for item in event["outputs"]:
                        output[good(item["good"])] += item["quantity"]
                        quantities[good(item["good"])] += item["quantity"]
                facility_row = {"hour": hour, "settlement": settlement_id, "building": key[0], "capability": key[1],
                                      "state_start": bf[key]["state"], "state_end": f["state"],
                                      "completed_batches": len(completed), "outputs": json.dumps(dict(quantities), ensure_ascii=False),
                                      "buffer_start": json.dumps({good(g["good"]): g["quantity"] for g in bf[key]["output_buffer"]}, ensure_ascii=False),
                                      "buffer_end": json.dumps({good(g["good"]): g["quantity"] for g in f["output_buffer"]}, ensure_ascii=False)}
                for label, state in (("start", bf[key]), ("end", f)):
                    facility_row["actual_work_per_second_" + label] = state["actual_work_per_second"]
                    for field in ("process", "limits", "blockers", "potential_consumption"):
                        facility_row[field + "_" + label] = json.dumps(state[field], ensure_ascii=False)
                facility_rows.append(facility_row)
            bm = {good(g["good"]): g for g in before["market_goods"]}
            am = {good(g["good"]): g for g in after["market_goods"]}
            check(bm.keys() == am.keys(), "市场货物集合变化，超出 starting-trade 分析范围")
            buffers = []
            for fs in (bf, af):
                totals = defaultdict(int)
                for f in fs.values():
                    for item in f["output_buffer"]:
                        totals[good(item["good"])] += item["quantity"]
                buffers.append(totals)
            check(set(output) <= set(am) and all(set(b) <= set(am) for b in buffers), "生产或缓冲货物缺少市场库存样本")
            for name, end_good in sorted(am.items()):
                begin_good = bm[name]
                relevant = [e for e in trades if e["settlement"] == settlement_id and good(e["good"]) == name]
                check(all((e["source"] == after["inventory"]) != (e["destination"] == after["inventory"])
                          for e in relevant), "成交库存方向与聚居地不符")
                net_transfers = sum(e["quantity"] * (1 if e["destination"] == after["inventory"] else -1)
                                    for e in relevant)
                consumed = begin_good["stock"] + buffers[0][name] + output[name] + net_transfers - end_good["stock"] - buffers[1][name]
                check(consumed >= 0, f"小时 {hour} 聚居地 {settlement_id} {name} 推导消耗为负：{consumed}")
                for action in ("Buy", "Sell"):
                    deals = [e for e in relevant if e["action"] == action]
                    quantity = sum(e["quantity"] for e in deals)
                    amount = sum(e["actual_amount"] for e in deals)
                    row = {"hour": hour, "settlement": settlement_id, "good": name, "action": action,
                           "trade_quantity": quantity, "trade_amount": amount, "actual_average_price": amount / quantity if quantity else "无成交",
                           "stock_start": begin_good["stock"], "stock_end": end_good["stock"],
                           "buffer_start": buffers[0][name], "buffer_end": buffers[1][name],
                           "buffer_change": buffers[1][name] - buffers[0][name], "completed_outputs": output[name],
                           "net_transfers": net_transfers, "derived_consumption": consumed}
                    for field in ("restock_target", "reserved_for_use", "sellable_quantity", "receivable_quantity"):
                        row[field + "_start"] = begin_good[field]
                        row[field + "_end"] = end_good[field]
                    market_rows.append(row)
            for label, settlement in (("start", before), ("end", after)):
                for need in settlement["population_needs"]:
                    needs_rows.append({"hour": hour, "boundary": label, "settlement": settlement_id,
                                       "residents": settlement["residents"], **need})
    directory = Path(destination)
    directory.mkdir(parents=True, exist_ok=True)
    summary = {"scenario": "starting-trade", "hours": 4, "steps_per_hour": HOUR,
               "consumption_basis": "库存起值+产出缓冲起值+本小时完成产量+净转入-库存末值-产出缓冲末值；包含批次投入、居民与燃料总消耗，不拆分。",
               "hour_assignment": "完整 (completed_steps, sequence) 比较；区间 (小时起点, 小时终点]；边界 sequence=0 归刚结束小时，sequence>0 归下一小时。",
               "market_rows": "同一货物的库存、产量、消耗列在 Buy/Sell 行重复，不能跨方向相加；方向为玩家列车视角。",
               "report_reconciliation": "跨所有样本按列车、started_at、ended_at 去重已结束报告，加当前运行累计；检查运行链无断档，并与各小时边界自起点成交累计核对。",
               "wallets": wallets, "trains": train_ids, "train_report_checks": report_checks,
               "trade_count": len(transfers), "production_completion_count": len(completions),
               "production_state_change_count": len(changes)}
    (directory / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for name, rows in (("trades.csv", trade_rows), ("hourly_trains.csv", hourly), ("hourly_markets.csv", market_rows),
                       ("hourly_facilities.csv", facility_rows), ("population_needs.csv", needs_rows), ("facility_state_changes.csv", state_rows)):
        if rows:
            write_csv(directory, name, rows)
    print(f"已汇总四小时：{len(transfers)} 笔转运，{len(train_ids)} 列车；钱包与运行报告对账通过。{directory}")


if __name__ == "__main__":
    try:
        check(len(sys.argv) == 3, __doc__)
        main(sys.argv[1], sys.argv[2])
    except (ValueError, KeyError, TypeError, OSError) as error:
        print(f"汇总失败：{error}", file=sys.stderr)
        sys.exit(1)
