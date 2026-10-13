"""生成基础贯通小世界的正式内置内容（不是完整 Demo 默认开局）。

python scripts/content/generate_small_world.py
python scripts/content/generate_small_world.py --check
python scripts/content/generate_small_world.py --output-dir TestResults/reproduction

脚本独占输出目录 resources/；校验模式拒绝缺失、多余或字节不同的文件，
并按仓库 Schema 检查 JSON（校验需要 jsonschema）。生成只用 Python 标准库。
地形与小型机车使用第一阶段风格样稿，其余美术仍为占位；数值待配平，
本批画面待用户阶段试玩确认。不生成流程、生物聚落或存档。
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

    rows = (b"\0" + bytes(color) * size) * size if not callable(color) else b"".join(
        b"\0" + b"".join(bytes(color(x, y)) for x in range(size)) for y in range(size))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


BLUE = (0x5a, 0x6b, 0x78)
GRAPHITE = (0x33, 0x37, 0x3b)
GLASS = (0x1e, 0x28, 0x30)
RED = (0x7b, 0x3a, 0x2c)
SOIL = (0x76, 0x59, 0x3f)
ROCK = (0x7f, 0x7c, 0x76)
GRASS = (0x6f, 0x8f, 0x4a)
STEEL = (0x8e, 0x93, 0x98)
YELLOW = (0xd9, 0xa6, 0x2b)


def tint(color, change=0):
    return tuple(max(0, min(255, value + change)) for value in color) + (255,)


def grain(x, y, size):
    # 四角采样绕回同一张图，低频固有色纹样在平铺边界连续。
    def sample(dx, dy):
        ax, ay = (x // 8 + dx) % (size // 8), (y // 8 + dy) % (size // 8)
        value = ax * 73856093 ^ ay * 19349663 ^ 0x374
        value = (value ^ (value >> 13)) * 1274126177
        return ((value ^ (value >> 16)) & 255) / 255 - .5
    fx, fy = x % 8 / 8, y % 8 / 8
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    return round(12 * ((sample(0, 0) * (1 - fx) + sample(1, 0) * fx) * (1 - fy)
                      + (sample(0, 1) * (1 - fx) + sample(1, 1) * fx) * fy))


def soil_texture(x, y):
    return tint(SOIL, grain(x, y, 128))


def grass_texture(x, y):
    return tint(GRASS, grain(x, y, 128))


def roots_texture(x, y):
    variation = grain(x, y, 128)
    return tint(SOIL if variation < 3 else tuple((a + b) // 2 for a, b in zip(SOIL, GRASS)), variation)


def rock_texture(x, y):
    return tint(ROCK, grain(x, y, 128) * 2)


def plate_texture(x, y):
    seam = x % 128 in (0, 64)
    rivet = x % 64 == 3 and y % 16 in (4, 5)
    return tint(BLUE, -10 if seam else -6 if rivet else grain(x, y, 128))


def tread_texture(x, y):
    return tint(BLUE, 6 if (x + y) % 16 in (2, 3) or (x - y) % 16 in (2, 3) else grain(x, y, 128))


def underside_texture(x, y):
    return tint((0x2b, 0x2e, 0x31))


def bogie_texture(x, y):
    return tint(GRAPHITE, 8 if y % 32 in range(12, 20) else grain(x, y, 64))


def engine_texture(x, y):
    return tint(BLUE, -22 if 4 <= x < 28 and y % 6 in (2, 3) else grain(x, y, 32))


def control_texture(x, y):
    return tint(GLASS if 3 <= x < 29 and 4 <= y < 25 else BLUE, grain(x, y, 32))


def storage_texture(x, y):
    return tint(RED, -12 if x in (2, 3, 28, 29) else 8 if 23 <= x < 27 and 12 <= y < 18 else grain(x, y, 32))


def grille_texture(x, y):
    return tint(BLUE, -18 if x % 8 in (2, 3) and y % 8 in (2, 3) else grain(x, y, 32))


def equipment_roof_texture(x, y):
    return tint(BLUE, grain(x, y, 32))


def storage_roof_texture(x, y):
    return tint(RED, grain(x, y, 32))


def icon(identity):
    pixels = [[(0, 0, 0, 0) for _ in range(128)] for _ in range(128)]

    def polygon(points, color):
        for y in range(max(0, min(p[1] for p in points)), min(128, max(p[1] for p in points) + 1)):
            for x in range(max(0, min(p[0] for p in points)), min(128, max(p[0] for p in points) + 1)):
                inside = False
                for (ax, ay), (bx, by) in zip(points, points[1:] + points[:1]):
                    if (ay > y) != (by > y) and x < (bx - ax) * (y - ay) / (by - ay) + ax:
                        inside = not inside
                if inside:
                    pixels[y][x] = tint(color)
        for a, b in zip(points, points[1:] + points[:1]):
            line(a, b, GLASS, 2)

    def line(a, b, color, width=2):
        steps = max(abs(b[0] - a[0]), abs(b[1] - a[1]), 1)
        for step in range(steps + 1):
            x, y = (round(a[i] + (b[i] - a[i]) * step / steps) for i in (0, 1))
            for dy in range(-(width // 2), width // 2 + 1):
                for dx in range(-(width // 2), width // 2 + 1):
                    if 0 <= x + dx < 128 and 0 <= y + dy < 128:
                        pixels[y + dy][x + dx] = tint(color)

    def box(x, y, width, height, depth, color):
        polygon([(x,y),(x+depth,y-depth),(x+width+depth,y-depth),(x+width,y)], tint(color, 18)[:3])
        polygon([(x+width,y),(x+width+depth,y-depth),(x+width+depth,y+height-depth),(x+width,y+height)], tint(color, -16)[:3])
        polygon([(x,y),(x+width,y),(x+width,y+height),(x,y+height)], color)

    if identity in ("ground", "subsoil", "rock"):
        color = SOIL if identity != "rock" else ROCK
        box(22, 55, 62, 42, 22, color)
        polygon([(22,55),(44,33),(106,33),(84,55)], GRASS if identity == "ground" else tint(color, 18)[:3])
        for a, b in (((31,75),(42,71)), ((59,85),(70,81)), ((72,65),(80,65))):
            line(a, b, tint(color, -12)[:3], 3)
    elif identity == "structural-metal":
        box(22, 39, 64, 14, 19, BLUE)
        box(43, 53, 19, 39, 19, BLUE)
        box(22, 88, 64, 13, 19, BLUE)
    elif identity == "bogie":
        for x in (35, 79):
            for y in range(65, 104):
                for px in range(x - 16, x + 17):
                    radius = ((px - x) / 16) ** 2 + ((y - 84) / 19) ** 2
                    if radius <= 1:
                        pixels[y][px] = tint(GLASS if radius > .72 else STEEL if radius < .15 else GRAPHITE)
        box(20, 53, 75, 18, 12, GRAPHITE)
        line((27,62),(90,62), STEEL, 3)
    elif identity == "propulsion-equipment":
        box(25, 54, 61, 43, 19, BLUE)
        box(32, 39, 43, 15, 12, BLUE)
        for y in (65, 74, 83):
            line((34,y),(76,y), GLASS, 4)
        box(80, 39, 10, 16, 9, GRAPHITE)
        line((38,41),(48,41), YELLOW, 3)
    elif identity == "control-equipment":
        box(28, 70, 55, 29, 20, BLUE)
        polygon([(33,38),(77,38),(88,69),(43,69)], BLUE)
        polygon([(42,45),(69,45),(77,61),(48,61)], GLASS)
        line((43,80),(54,80), YELLOW, 3)
        line((70,81),(77,74), GRAPHITE, 4)
    elif identity == "storage-equipment":
        box(24, 44, 61, 53, 19, RED)
        for x in (34, 53, 72):
            line((x,50),(x,91), tint(RED, -18)[:3], 3)
        line((76,67),(76,77), STEEL, 3)
    else:
        raise ValueError(identity)
    return png(128, lambda x, y: pixels[y][x])


def style_assets(files):
    for name, size in (("vehicle-underside", 128), ("equipment-underside", 32), ("bogie-underside", 64)):
        files[f"voxels/shared/{name}.png"] = png(size, underside_texture)
    textures = {
        "ground": (4, "organic", grass_texture, roots_texture, "shared/subsoil.png"),
        "subsoil": (4, "stone_soil", soil_texture, None, None),
        "rock": (4, "stone_soil", rock_texture, None, None),
        "structural-metal": (4, "painted_metal", tread_texture, plate_texture, "shared/vehicle-underside.png"),
        "bogie": (2, "bare_metal", bogie_texture, None, "shared/bogie-underside.png"),
        "propulsion-equipment": (1, "painted_metal", grille_texture, engine_texture, "shared/equipment-underside.png"),
        "control-equipment": (1, "painted_metal", equipment_roof_texture, control_texture, "shared/equipment-underside.png"),
        "storage-equipment": (1, "painted_metal", storage_roof_texture, storage_texture, "shared/equipment-underside.png"),
    }
    for identity, (span, material, top, side, bottom) in textures.items():
        directory = f"voxels/{identity}"
        top_path = "shared/subsoil.png" if identity == "subsoil" else "top.png"
        files[f"voxels/{top_path}" if identity == "subsoil" else f"{directory}/{top_path}"] = png(32 * span, top)
        if side is not None:
            files[f"{directory}/side.png"] = png(32 * span, side)
        files[f"{directory}/icon.png"] = icon(identity)
        path = f"voxels/{identity}.voxel.json"
        value = json.loads(files[path])
        value["appearance"]["presentation"].update(
            material=material, tile_span=span,
            faces={"top": top_path, "side": "side.png" if side is not None else top_path,
                   "bottom": bottom if bottom is not None else top_path})
        value["display"].update(icon="icon.png", description=f"{value['display']['name']}的第一阶段风格样稿；数值待配平，画面待用户阶段试玩确认。")
        files[path] = (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")


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


def small_locomotive():
    volume = {(x, 1, z) for x in range(-3, 4) for z in range(-26, 26)}
    volume |= {(x, y, z) for x in range(-3, 4) for y in range(2, 9) for z in range(16, 24)}
    volume |= {(x, y, z) for x in range(-2, 3) for y in range(2, 8) for z in range(-24, 16)}
    directions = ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1))
    shell = {at for at in volume if any(tuple(at[i] + delta[i] for i in range(3)) not in volume for delta in directions)}
    cells = {at: 0 for at in shell}
    for z in (-22, -20, 19, 21):
        for x in range(-2, 2):
            cells[x, 0, z] = 1
    for z in (-21, 20):
        for x in range(-2, 2):
            cells[x, 0, z] = 0
    cells.update({(0, 6, 23): 3, (2, 5, 5): 2, (2, 5, 1): 2, (2, 5, -3): 2, (-2, 3, -20): 4})
    # 外形调整不改变本批的价值、质量或能力；差额从底部向上填入内部。
    fill = 1420 - sum(index == 0 for index in cells.values())
    interior = sorted(volume - shell, key=lambda at: (at[1], at[2], at[0]))
    if not 0 <= fill <= len(interior):
        raise ValueError(f"小型机车外形放不下既定体素数量：内部需要 {fill} 格，可用 {len(interior)} 格")
    cells.update((at, 0) for at in interior[:fill])
    value = blueprint("small-locomotive", "小型机车", "vehicle",
                      ["structural-metal", "bogie", "propulsion-equipment", "control-equipment", "storage-equipment"],
                      cells, {"size_class": "small"})
    value["display"]["description"] = "第一阶段铁路机车风格样稿：驾驶室、机器间、检修走廊和端部平台；画面待用户阶段试玩确认。"
    return value


def content_files():
    files = {}

    def document(directory, kind, identity, value):
        data = (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        if len(data) > JSON_LIMIT:
            raise ValueError(f"{identity}: {len(data)} bytes exceeds {JSON_LIMIT}")
        files[f"{directory}/{identity}.{kind}.json"] = data

    colors = {"soil": (125, 107, 76, 255),
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
    style_assets(files)

    for spec in VEHICLES:
        value = small_locomotive() if spec[0] == "small-locomotive" else locomotive(spec)
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
    retired = {f"voxels/shared/metal-{kind}.png{suffix}" for kind in ("face", "icon") for suffix in ("", ".import")}
    if not args.check:
        extra -= retired
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
        for path in actual & retired:
            (resources / path).unlink()
        for path, data in files.items():
            target = resources / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
    print(f"{'校验' if args.check else '生成'} {len(files)} 个文件；超大型蓝图 {len(files['blueprints/extra-large-locomotive.blueprint.json'])} 字节。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
