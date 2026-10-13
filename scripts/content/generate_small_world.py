"""生成基础贯通小世界的正式内置内容（不是完整 Demo 默认开局）。

python scripts/content/generate_small_world.py
python scripts/content/generate_small_world.py --check
python scripts/content/generate_small_world.py --output-dir TestResults/reproduction

脚本独占输出目录 resources/；校验模式拒绝缺失、多余或字节不同的文件，
并按仓库 Schema 检查 JSON（校验需要 jsonschema）。生成只用 Python 标准库。
内容数值与占位美术尚待配平和人工确认；不生成流程、生物聚落或存档。
"""

import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys
import zlib


ROOT = Path(__file__).resolve().parents[2]
JSON_LIMIT = 1024 * 1024
VEHICLES = (
    ("small-locomotive", "小型机车", "small", 52, 7, 9, 3, 1, 2, 4),
    ("medium-locomotive", "中型机车", "medium", 64, 10, 13, 7, 2, 3, 6),
    ("extra-large-locomotive", "超大型机车", "extra_large", 104, 20, 26, 15, 4, 5, 10),
)


def png(size, color):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    rows = (b"\0" + bytes(color) * size) * size
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def keep_import(path):
    # Godot 4.7.2 ResourceUID 的兼容编码使用 a-y、0-8，共 34 个字符。
    alphabet = "abcdefghijklmnopqrstuvwxy012345678"
    number = int.from_bytes(hashlib.sha256(("train:core:" + path).encode()).digest()[:8], "big") & ((1 << 63) - 1)
    encoded = ""
    while number:
        number, remainder = divmod(number, len(alphabet))
        encoded = alphabet[remainder] + encoded
    return f'[remap]\n\nimporter="keep"\nuid="uid://{encoded or "a"}"\n'.encode()


def blueprint(identity, name, category, palette, cells, simulation=None, appearance=None):
    value = {"schema_version": 3, "id": identity, "category": category,
             "layout": {"palette": [{"voxel": voxel} for voxel in palette],
                        "cells": [[x, y, z, index] for (x, y, z), index in sorted(cells.items())]},
             "display": {"name": name, "description": f"基础贯通正式内容：{name}，占位外观待人工确认。"}}
    if simulation is not None:
        value["simulation"] = simulation
    if appearance is not None:
        value["appearance"] = appearance
    return value


def locomotive(spec):
    identity, name, size, length, width, height, gauge, frame_height, axle_spacing, end_margin = spec
    x0, z0 = -(width // 2), -(length // 2)
    x1, z1 = x0 + width - 1, z0 + length - 1
    cells = {(x, y, z): 0 for x in range(x0, x1 + 1) for y in range(frame_height, height)
             for z in range(z0, z1 + 1)
             if x in (x0, x1) or y in (frame_height, height - 1) or z in (z0, z1)}
    left, right = -(gauge + 1) // 2, (gauge - 1) // 2
    for first, last in ((z0 + end_margin, z0 + end_margin + axle_spacing),
                        (z1 - end_margin - axle_spacing, z1 - end_margin)):
        for z in range(first, last + 1):
            for y in range(frame_height):
                for x in (left, right):
                    cells[x, y, z] = 1 if z in (first, last) else 0
            for x in range(left, right + 1):
                cells[x, frame_height - 1, z] = 1 if z in (first, last) else 0
    for z in (-2, 0, 2):
        cells[0, frame_height + 1, z] = 2
    cells[0, frame_height + 1, z1 - 1] = 3
    cells[1, frame_height + 1, 0] = 4
    return blueprint(identity, name, "vehicle",
                     ["structural-metal", "bogie", "propulsion-equipment", "control-equipment", "storage-equipment"],
                     cells, {"size_class": size})


def content_files():
    files = {}

    def document(directory, kind, identity, value):
        data = (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        if len(data) > JSON_LIMIT:
            raise ValueError(f"{identity}: {len(data)} bytes exceeds {JSON_LIMIT}")
        files[f"{directory}/{identity}.{kind}.json"] = data

    colors = {"metal": (134, 148, 163, 255), "soil": (125, 107, 76, 255),
              "organic": (99, 143, 81, 255), "equipment": (194, 148, 60, 255)}
    for material, color in colors.items():
        files[f"voxels/shared/{material}-face.png"] = png(32, color)
        files[f"voxels/shared/{material}-icon.png"] = png(128, color)
    files["settlement-types/shared/agricultural-town.png"] = png(48, (226, 236, 220, 255))

    def voxel(identity, name, color, material, simulation=None, wheelset=False):
        value = {"schema_version": 3, "id": identity,
                 "appearance": {"allowed_orientations": "four_way",
                                "presentation": {"kind": "cube", "material": material,
                                                 "faces": {face: f"shared/{color}-face.png" for face in ("top", "side", "bottom")}}},
                 "display": {"name": name, "description": f"{name}的第一版正式定义；数值待配平，占位贴图待美术制作。",
                             "icon": f"shared/{color}-icon.png"}}
        if simulation is not None:
            value["simulation"] = simulation
        if wheelset:
            value["appearance"]["behaviors"] = {"wheelset": {}}
        document("voxels", "voxel", identity, value)

    def good(value, mass, durability=180, behaviors=None, raw=False):
        simulation = {"value": value, "mass": mass, "max_durability": durability, "tags": ["raw_good"] if raw else []}
        if behaviors is not None:
            simulation["behaviors"] = behaviors
        return simulation

    voxel("structural-metal", "结构金属", "metal", "bare_metal", good(64, 0.028, 260))
    voxel("construction-material", "建材", "soil", "concrete", good(38, 0.046, 220))
    voxel("bogie", "转向架", "metal", "bare_metal", good(156, 0.034, 240), wheelset=True)
    voxel("platform-equipment", "站台设备", "equipment", "painted_metal", good(218, 0.024, behaviors={"platform": {}}))
    voxel("propulsion-equipment", "柴油机组", "equipment", "painted_metal",
          good(860, 1.12, behaviors={"propulsion": {"traction": 240, "rated_speed": 22}}))
    voxel("control-equipment", "驾驶台", "equipment", "painted_metal", good(420, 0.018, behaviors={"control": {}}))
    voxel("storage-equipment", "货舱", "equipment", "painted_metal", good(126, 0.016, behaviors={"storage": {"capacity": 180}}))
    goods = (("staple-food", "主食", 12, 0.7), ("clothing", "衣物", 46, 0.8),
             ("prepared-food", "加工食品", 34, 0.6), ("consumer-goods", "日用品", 58, 1.4),
             ("consumer-electronics", "消费电子", 176, 1.6), ("medical-supplies", "医疗用品", 94, 0.4))
    for identity, name, value, mass in goods:
        voxel(identity, name, "organic", "organic", good(value, mass))
    voxel("stone", "石料", "soil", "stone_soil", good(18, 0.042, raw=True))
    voxel("ground", "地表", "organic", "stone_soil")
    voxel("subsoil", "土层", "soil", "stone_soil")
    voxel("rock", "岩层", "soil", "stone_soil")

    for spec in VEHICLES:
        value = locomotive(spec)
        document("blueprints", "blueprint", value["id"], value)
    station = {(x, 0, z): 0 for x in range(-30, 30) for z in range(-10, 10)}
    station[-20, 1, 0] = station[20, 1, 0] = 1
    document("blueprints", "blueprint", "small-station",
             blueprint("small-station", "小站点", "building", ["construction-material", "platform-equipment"],
                       station, {"plot_size": "station", "building_type": "station", "facility_tags": []}))
    for tracks in (1, 2):
        half_width = 16 + 17 * (tracks - 1)
        bridge = {(x, 0, z): 0 for x in range(-half_width, half_width + 1) for z in range(-3, 4)}
        portal_half_width = 17 * tracks
        tunnel = {(x, y, 0): 0 for x in range(-portal_half_width - 2, portal_half_width + 2) for y in range(44)
                  if x < -portal_half_width or x >= portal_half_width or y >= 42}
        for kind, name, cells in (("bridge", "桥梁", bridge), ("tunnel-portal", "隧道口", tunnel)):
            identity = f"{kind}-{tracks}"
            document("blueprints", "blueprint", identity, blueprint(identity, f"{tracks}轨{name}", "scene_model", ["rock"], cells))
    ground = {(x, 0, z): 0 for x in range(-64, 64) for z in range(-48, 48)}
    document("blueprints", "blueprint", "agricultural-town-ground",
             blueprint("agricultural-town-ground", "农业镇地面", "scene_model", ["ground"], ground,
                       appearance={"flatten": {"from": [-64, -48], "to": [63, 47], "height": 1}}))
    for identity, name, local_amplitude in (("plains", "平原", 0.7), ("hills", "丘陵", 2.8)):
        document("terrain-types", "terrain-type", identity,
                 {"schema_version": 1, "id": identity, "display": {"name": name, "description": f"基础贯通{name}，提供局部起伏与铁路模型。"},
                  "surface_voxel": "ground", "subsurface_voxel": "subsoil", "rock_voxel": "rock",
                  "local_height_noise": [{"scale_meters": 140, "amplitude": local_amplitude}],
                  "decorations": [], "decoration_target_per_square_meter": 0,
                  "railway_models": [{"tracks": n, "bridge": f"bridge-{n}", "tunnel_portal": f"tunnel-portal-{n}"} for n in (1, 2)]})
    document("settlement-types", "settlement-type", "agricultural-town",
             {"schema_version": 3, "id": "agricultural-town", "simulation": {"preferred_terrain": ["core:plains", "core:hills"],
              "weight": 3, "population_min": 120, "population_max": 360},
              "display": {"name": "农业镇", "description": "基础贯通的农业镇类型；生产链和服务设施随后接入。",
                          "map_symbol": "shared/agricultural-town.png", "major": False}})
    plots = [{"id": 1, "size": "station", "at": [-32, 1, -28], "building": "small-station"},
             {"id": 2, "size": "station", "at": [32, 1, -28]}]
    plots += [{"id": n, "size": "small", "at": [x, 1, z]} for n, (x, z) in enumerate(
        ((-40, 0), (-16, 0), (16, 0), (40, 0), (-16, 28), (16, 28)), 3)]
    document("settlement-templates", "settlement-template", "agricultural-town",
             {"schema_version": 2, "id": "agricultural-town", "settlement_type": "agricultural-town", "weight": 3,
              "scene_model": "agricultural-town-ground", "plots": plots,
              "display": {"name": "农业镇基础布局", "description": "含真实小站点建筑的八地块布局；尚不启用生产计划。"}})

    needs = ("food", "clothing", "improved_diet", "consumer_goods", "consumer_electronics", "medical_supplies")
    consumption = []
    for order, (identity, _, _, _) in enumerate(goods):
        roles = ("food", "improved_diet") if identity == "prepared-food" else (needs[order],)
        consumption.append({"good": identity, "selection_order": order,
                            "satisfies": [{"need": role, "amount_per_good_unit": 1.4 if role == "food" else 1.1} for role in roles]})
    document("rules", "population-rule", "basic-population",
             {"schema_version": 1, "id": "basic-population", "simulation": {
                 "needs": {need: {"class": "basic" if need in ("food", "clothing") else "improvement",
                                  "amount_per_person_second": 0.00016, "weight": 1.2} for need in needs},
                 "consumption_goods": consumption, "labor_ratio": 0.58, "consumption_interval_ticks": 120,
                 "statistics_period_ticks": 720, "base_growth_per_second": 0.000002,
                 "improvement_growth_per_second": 0.000001, "maximum_loss_per_second": 0.000004}})
    document("rules", "market-rule", "basic-market", {"schema_version": 1, "id": "basic-market", "simulation": {
        "basic_reserve_hours": 12, "production_reserve_hours": 6, "improvement_reserve_hours": 4,
        "default_goods_prices": {"sensitivity_basis_points": 1800, "minimum_multiplier_basis_points": 5500,
                                 "maximum_multiplier_basis_points": 23000, "spread_basis_points": 1200},
        "good_prices": [], "vehicle_buy_multiplier_basis_points": 8500, "vehicle_sell_multiplier_basis_points": 11500,
        "vehicle_purchase_limits": [{"size": size, "maximum_for_sale": 3} for size in ("small", "medium", "large", "extra_large", "titan")],
        "automatic_evaluation_interval_ticks": 240, "recent_report_limit": 24, "commission_money_per_work": 2,
        "rent_money_per_capacity_hour": 1, "rentable_capacity_ratio_basis_points": 4500, "lease_duration_ticks": 3600}})
    document("rules", "construction-rule", "basic-construction", {"schema_version": 1, "id": "basic-construction", "simulation": {
        "founding_duration_ticks": 2400, "construction_work_per_item": 8, "refit_work_per_changed_item": 5,
        "deconstruction_work_per_item": 3, "repair_work_per_damaged_part": 4, "power_per_work": 0.4, "labor_per_work": 0.25}})
    document("railway-rules", "railway-rule", "basic-railway", {"schema_version": 1, "id": "basic-railway",
        "braking_deceleration": 0.7, "minimum_acceleration": 0.15, "minimum_gap_meters": 28,
        "rail_units_per_meter": 0.7, "build_seconds_per_meter": 3, "repair_seconds_per_break": 48,
        "rail_good": "structural-metal", "repair_rail_units_per_break": 18,
        "appearance": {"minimum_curve_radius_meters": 80, "maximum_grade": 0.08}})
    document("combat-rules", "combat-rule", "basic-combat", {"schema_version": 1, "id": "basic-combat",
        "retreat_seconds": 24, "morale_damage_coefficient": 0.015, "weapon_destroyed_morale_loss": 0.18,
        "morale_recovery_per_second": 0.012, "surrender_threshold": 0.12, "new_lair_interval_seconds": 5400})
    bands = [{"terrain": terrain, "minimum_elevation": -1, "maximum_elevation": 30,
              "minimum_moisture": -1, "maximum_moisture": 2, "maximum_relief": relief,
              "detour_multiplier": detour, "maximum_tracks": 2}
             for terrain, relief, detour in (("plains", 0.75, 1.12), ("hills", 100, 1.35))]
    document("generation-profiles", "generation-profile", "basic-through-world", {
        "schema_version": 2, "id": "basic-through-world",
        "display": {"name": "基础贯通小世界", "description": "真实三轨距机车、站点、非零噪声与环路；不是完整 Demo 默认开局，经济条件与生物聚落尚未验收。"},
        "configuration": {"width_meters": 1800, "height_meters": 1200, "minimum_node_spacing_meters": 300,
            "candidate_probability": 0.9, "elevation_noise": [{"scale_meters": 780, "amplitude": 14}, {"scale_meters": 260, "amplitude": 4}],
            "moisture_noise": [{"scale_meters": 600, "amplitude": 1}], "river_noise": [{"scale_meters": 900, "amplitude": 1}],
            "lake_elevation_threshold": 4, "river_band_half_width": 0.045, "terrain_bands": bands,
            "terrain_sample_spacing_meters": 60, "settlement_ratio": 0.45, "lair_ratio": 0,
            "hostile_settlement_ratio": 0, "lair_weights": [],
            "resources": [{"good": "stone", "allowed_terrain_types": ["plains", "hills"], "probability": 0.55,
                           "minimum_reserve": 1800, "maximum_reserve": 5600, "renewable": False,
                           "recovery_units_per_second": 0, "regeneration_interval_seconds": 7200,
                           "global_regeneration_remaining_reserve_ratio": 0.3}],
            "initial_railway": {"maximum_tracks": 2, "minimum_initial_tracks": 1, "maximum_initial_tracks": 2, "additional_cycle_edges": 1},
            "budget": {"maximum_stage_retries": 4, "maximum_world_retries": 4, "maximum_candidates": 50000, "maximum_search_work": 4000000},
            "npc_initialization": {"fill_inputs_and_living_targets": False, "output_reserve_hours": 0,
                                   "minimum_for_sale_vehicles": 0, "maximum_for_sale_vehicles": 0, "for_sale_vehicle_candidates": []},
            "start_constraints": {"minimum_reachable_non_hostile_settlements": 2, "minimum_reachable_empty_nodes": 1,
                                  "require_station": True, "exclude_adjacent_lairs": True, "fully_loaded_purchase_rounds_per_train": 1,
                                  "required_affordable_commission_design": "small-locomotive"},
            "rules": {"population": "basic-population", "market": "basic-market", "construction": "basic-construction",
                      "railway": "basic-railway", "combat": "basic-combat"}},
        "start": {"initial_money": 600000, "trains": [{"vehicles": [spec[0]], "inventory": []} for spec in VEHICLES]}})
    for path in list(files):
        if path.endswith(".png"):
            files[path + ".import"] = keep_import(path)
    return files


def check_schemas(files):
    from jsonschema import Draft202012Validator
    from referencing import Registry, Resource

    schemas = {path.name: json.loads(path.read_text(encoding="utf-8"))
               for path in (ROOT / "docs/content/schemas").glob("*.schema.json")}
    registry = Registry().with_resources((schema["$id"], Resource.from_contents(schema)) for schema in schemas.values())
    for path, data in files.items():
        if path.endswith(".json"):
            kind = path.rsplit("/", 1)[1].split(".")[-2]
            Draft202012Validator(schemas[kind + ".schema.json"], registry=registry).validate(json.loads(data))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "builtin/core")
    args = parser.parse_args()
    files = content_files()
    resources = args.output_dir / "resources"
    actual = {path.relative_to(resources).as_posix() for path in resources.rglob("*") if path.is_file()}
    extra = actual - files.keys()
    missing = files.keys() - actual
    if extra or (args.check and missing):
        for label, paths in (("多余", extra), ("缺少", missing if args.check else set())):
            for path in sorted(paths):
                print(f"{label}文件：{path}", file=sys.stderr)
        return 1
    if args.check:
        changed = [path for path, data in files.items() if (resources / path).read_bytes() != data]
        if changed:
            for path in changed:
                print(f"字节不同：{path}", file=sys.stderr)
            return 1
        check_schemas(files)
    else:
        for path, data in files.items():
            target = resources / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
    print(f"{'校验' if args.check else '生成'} {len(files)} 个文件；超大型蓝图 {len(files['blueprints/extra-large-locomotive.blueprint.json'])} 字节。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
