"""生成经济运输验证世界的正式内置内容。

python scripts/content/generate_small_world.py
python scripts/content/generate_small_world.py --check
python scripts/content/generate_small_world.py --output-dir TestResults/reproduction

脚本独占输出目录 resources/；校验模式拒绝缺失、多余或字节不同的文件，
并按仓库 Schema 检查 JSON（校验需要 jsonschema）。生成只用 Python 标准库。
地形与小型机车使用第一阶段风格样稿，其余美术仍为占位；数值待配平，
数值和画面待用户试玩确认；NPC 库存与现车初始化已启用，车队待 #340。
校验同时检查正式内容的经济关系；不生成生物聚落或存档。
"""

import argparse
import hashlib
import json
from math import ceil
from pathlib import Path
import struct
import subprocess
import sys
import zlib


ROOT = Path(__file__).resolve().parents[2]
JSON_LIMIT = 1024 * 1024
VEHICLES = (
    ("small-locomotive", "小型机车", "small", 52, 7, 9, 3, 1, 2, 4),
    ("medium-locomotive", "中型机车", "medium", 64, 10, 13, 7, 2, 3, 6),
    ("extra-large-locomotive", "超大型机车", "extra_large", 104, 20, 26, 15, 4, 5, 10),
)

# 这些是内容初值，不是报价、补货或运行规则；全部待用户试玩确认。
BATCH_UNITS = 4
SOURCE_UNIT_VALUE = 10
VALUE_MARKUP = 1.25
REFERENCE_WORK_RATE = 1
POWER_HEADROOM = 2
FUEL_UNIT_SECONDS = 60
RESOURCE_RESERVE_HOURS = (4, 8)
RESOURCE_PROBABILITY = 1
FOREST_RECOVERY_SHARE = 0.5
GLOBAL_CHECK_MINUTES = 10
GLOBAL_REGEN_RATIO = 0.25
SECONDS_PER_HOUR = 3600
HOURS_PER_DAY = 24
TICKS_PER_SECOND = 60
BASIC_RESERVE_HOURS = 8
PRODUCTION_RESERVE_HOURS = BASIC_RESERVE_HOURS / 2
NPC_FILL_INPUTS_AND_LIVING_TARGETS = True
NPC_MINIMUM_FOR_SALE_VEHICLES = 1
NPC_MAXIMUM_FOR_SALE_VEHICLES = 2
NPC_FOR_SALE_VEHICLE_CANDIDATES = tuple(spec[0] for spec in VEHICLES)
MARKET_MARGIN = 0.1
MARKET_SENSITIVITY = 0.25
MARKET_MULTIPLIER_LIMITS = (0.5, 2)
BASIS_POINTS = 10000
RENTABLE_CAPACITY_SHARE = 0.5
LEASE_DAYS = 1
MAXIMUM_LEASE_MULTIPLE = 7
AUTOMATIC_EVALUATION_SECONDS = 5
RECENT_REPORT_LIMIT = 24
VEHICLE_PURCHASE_LIMIT = 3
NEED_UNITS_PER_PERSON_HOUR = 1
LABOR_RATIO = 0.5
CONSUMPTION_SECONDS = 1
STATISTICS_SECONDS = 10
DAILY_GROWTH = 0.01
DAILY_MAXIMUM_LOSS = 0.02
START_BUDGET_MULTIPLIER = 2
INITIAL_PLAN_PRIORITY = 0
WEAPON_DAMAGE_SHARE = 0.1
MACHINE_GUN_INTERVAL_SECONDS = 1
CANNON_FACTOR = 4

# 每批基准秒数、电量/工作量、劳动力/(工作量/秒)。
PROCESS_PROFILES = {
    "agriculture": (60, 0, 1), "extraction": (30, 1, 1),
    "metallurgy": (30, 2, 1), "building_materials": (20, 1, 1),
    "light_industry": (20, 1, 1), "chemical": (30, 2, 1),
    "food_processing": (15, 1, 1), "assembly": (40, 1, 1),
    "electronics": (40, 1, 1), "recovery": (20, 1, 1),
}
FARM_TERRAINS = ("plains", "hills")
MINING_TERRAINS = ("mountains", "hills")
RESOURCE_PROFILES = (
    ("ferrous-mining", "ferrous-ore", MINING_TERRAINS),
    ("conductive-mining", "conductive-ore", MINING_TERRAINS),
    ("rare-mineral-mining", "rare-mineral", MINING_TERRAINS),
    ("stone-quarrying", "stone", MINING_TERRAINS),
    ("silica-mining", "silica", MINING_TERRAINS),
    ("coal-mining", "coal", MINING_TERRAINS),
    ("crude-oil-extraction", "crude-oil", ("desert",)),
    ("timber-harvesting", "timber", ("forest",)),
)
RAW_GOODS = {row[1] for row in RESOURCE_PROFILES} | {"grain", "produce", "animal-products", "natural-fiber"}
ALL_SHAPES = ["cube", "half", "plate", "wedge", "corner-wedge", "beam", "cylinder"]

# id、显示名、质量/耐久对照的已有货物、共享占位图族。
# 只复制这两个物理字段，不继承对照货物的功能或经济身份。
NEW_GOODS = (
    ("ferrous-ore", "铁矿石", "staple-food", "soil"),
    ("conductive-ore", "导电金属矿", "staple-food", "soil"),
    ("rare-mineral", "稀有矿物", "staple-food", "soil"),
    ("silica", "硅砂", "staple-food", "soil"),
    ("coal", "煤", "staple-food", "soil"),
    ("crude-oil", "原油", "staple-food", "organic"),
    ("grain", "谷物", "staple-food", "organic"),
    ("produce", "果蔬", "staple-food", "organic"),
    ("animal-products", "畜产品", "staple-food", "organic"),
    ("timber", "原木", "staple-food", "organic"),
    ("natural-fiber", "天然纤维", "staple-food", "organic"),
    ("conductive-metal", "导电金属", "consumer-goods", "equipment"),
    ("specialty-material", "特种材料", "consumer-goods", "equipment"),
    ("glass", "玻璃", "consumer-goods", "equipment"),
    ("chemical-feedstock", "化工原料", "consumer-goods", "organic"),
    ("polymer", "聚合物", "consumer-goods", "organic"),
    ("lumber", "木材", "consumer-goods", "organic"),
    ("textile", "纺织品", "consumer-goods", "organic"),
    ("mechanical-components", "机械部件", "consumer-electronics", "equipment"),
    ("electrical-components", "电气部件", "consumer-electronics", "equipment"),
    ("electronic-components", "电子部件", "consumer-electronics", "equipment"),
    ("structural-block", "结构块", "structural-metal", "equipment"),
    ("construction-block", "混凝土块", "structural-metal", "soil"),
    ("rail", "铁轨", "structural-metal", "equipment"),
    ("lamp", "灯具", "control-equipment", "equipment"),
    ("machine-gun", "机枪", "propulsion-equipment", "equipment"),
    ("cannon", "火炮", "propulsion-equipment", "equipment"),
)

# id、设施标签、投入种类、产出种类；批量只按 BATCH_UNITS 推导。
PRODUCTION = (
    ("grain-farming", "agriculture", (), ("grain",)),
    ("produce-farming", "agriculture", (), ("produce",)),
    ("livestock-production", "agriculture", ("grain",), ("animal-products",)),
    ("fiber-farming", "agriculture", (), ("natural-fiber",)),
    ("structural-metal-smelting", "metallurgy", ("ferrous-ore", "coal"), ("structural-metal",)),
    ("conductive-metal-smelting", "metallurgy", ("conductive-ore",), ("conductive-metal",)),
    ("specialty-material-processing", "metallurgy", ("rare-mineral",), ("specialty-material",)),
    ("construction-material-processing", "building_materials", ("stone",), ("construction-material",)),
    ("glass-making", "building_materials", ("silica",), ("glass",)),
    ("lumber-processing", "light_industry", ("timber",), ("lumber",)),
    ("textile-making", "light_industry", ("natural-fiber",), ("textile",)),
    ("oil-refining", "chemical", ("crude-oil",), ("fuel", "chemical-feedstock")),
    ("coal-fuel-production", "chemical", ("coal",), ("fuel",)),
    ("polymer-production", "chemical", ("chemical-feedstock",), ("polymer",)),
    ("mechanical-components-assembly", "assembly", ("structural-metal", "polymer"), ("mechanical-components",)),
    ("electrical-components-assembly", "assembly", ("conductive-metal", "structural-metal"), ("electrical-components",)),
    ("electronic-components-assembly", "electronics", ("electrical-components", "specialty-material", "glass", "polymer"), ("electronic-components",)),
    ("staple-food-processing", "food_processing", ("grain",), ("staple-food",)),
    ("prepared-food-processing", "food_processing", ("grain", "produce", "animal-products"), ("prepared-food",)),
    ("clothing-making", "light_industry", ("textile",), ("clothing",)),
    ("consumer-goods-making", "light_industry", ("lumber", "polymer", "structural-metal", "glass"), ("consumer-goods",)),
    ("consumer-electronics-assembly", "electronics", ("electronic-components", "polymer", "glass"), ("consumer-electronics",)),
    ("medical-supplies-production", "chemical", ("chemical-feedstock", "textile", "polymer"), ("medical-supplies",)),
    ("structural-block-making", "metallurgy", ("structural-metal",), ("structural-block",)),
    ("construction-block-making", "building_materials", ("construction-material",), ("construction-block",)),
    ("rail-making", "metallurgy", ("structural-metal",), ("rail",)),
    ("industrial-machinery-assembly", "assembly", ("structural-metal", "mechanical-components", "electrical-components"), ("industrial-machinery",)),
    ("storage-equipment-assembly", "assembly", ("structural-metal", "mechanical-components"), ("storage-equipment",)),
    ("power-equipment-assembly", "assembly", ("structural-metal", "mechanical-components", "electrical-components"), ("power-equipment",)),
    ("extraction-equipment-assembly", "assembly", ("structural-metal", "mechanical-components", "electrical-components"), ("extraction-equipment",)),
    ("construction-equipment-assembly", "assembly", ("structural-metal", "mechanical-components", "electrical-components"), ("construction-equipment",)),
    ("propulsion-equipment-assembly", "assembly", ("structural-metal", "mechanical-components", "electrical-components"), ("propulsion-equipment",)),
    ("control-equipment-assembly", "assembly", ("electronic-components", "electrical-components"), ("control-equipment",)),
    ("bogie-assembly", "assembly", ("structural-metal", "mechanical-components"), ("bogie",)),
    ("platform-equipment-assembly", "assembly", ("structural-metal", "electrical-components"), ("platform-equipment",)),
    ("lamp-assembly", "assembly", ("electrical-components", "glass"), ("lamp",)),
    ("machine-gun-assembly", "assembly", ("structural-metal", "mechanical-components"), ("machine-gun",)),
    ("cannon-assembly", "assembly", ("structural-metal", "mechanical-components", "electrical-components"), ("cannon",)),
)
RECOVERY = (
    ("structural-block", ("structural-metal",)),
    ("construction-block", ("construction-material",)),
    ("rail", ("structural-metal",)),
    ("mechanical-components", ("structural-metal", "polymer")),
    ("electrical-components", ("conductive-metal", "structural-metal")),
    ("electronic-components", ("electrical-components", "specialty-material", "polymer")),
    ("industrial-machinery", ("mechanical-components", "electrical-components", "structural-metal")),
    ("storage-equipment", ("mechanical-components", "structural-metal")),
    ("power-equipment", ("mechanical-components", "electrical-components", "structural-metal")),
    ("extraction-equipment", ("mechanical-components", "electrical-components", "structural-metal")),
    ("construction-equipment", ("mechanical-components", "electrical-components", "structural-metal")),
    ("propulsion-equipment", ("mechanical-components", "electrical-components", "structural-metal")),
    ("control-equipment", ("electronic-components", "electrical-components")),
    ("bogie", ("structural-metal", "mechanical-components")),
    ("platform-equipment", ("structural-metal", "electrical-components")),
    ("machine-gun", ("structural-metal", "mechanical-components")),
    ("cannon", ("structural-metal", "mechanical-components")),
)
TEMPLATE_PLANS = {
    "agricultural-town": ((6, "production", "grain-farming", True), (7, "production", "staple-food-processing", True)),
    "mining-town": ((6, "extraction", "stone-quarrying", True), (7, "production", "structural-metal-smelting", True),
                    (8, "production", "construction-material-processing", True)),
    "forestry-town": ((6, "extraction", "timber-harvesting", True), (7, "production", "lumber-processing", True)),
    "oilfield-town": ((6, "extraction", "crude-oil-extraction", True), (7, "production", "oil-refining", True)),
    "industrial-city": ((6, "production", "electrical-components-assembly", True),
                        (7, "production", "electronic-components-assembly", True),
                        (8, "production", "recover-electronic-components", False)),
}


def process_definitions():
    processes = {}

    def add(identity, kind, tag, inputs=(), outputs=(), extracts=None, terrains=()):
        seconds, power, labor = PROCESS_PROFILES[tag]
        simulation = {"kind": kind, "work_required": ceil(REFERENCE_WORK_RATE * seconds),
                      "power_per_work": power, "labor_per_work": labor, "facility_tag": tag}
        if kind == "extraction":
            simulation["extracts"] = {"voxel": extracts, "quantity": BATCH_UNITS}
        else:
            simulation["inputs"] = [{"voxel": good, "quantity": BATCH_UNITS} for good in inputs]
            output_quantity = 1 if kind == "recovery" else BATCH_UNITS * (2 if identity == "coal-fuel-production" else 1)
            simulation["outputs"] = [{"voxel": good, "quantity": output_quantity} for good in outputs]
        if terrains:
            simulation["environment"] = {"terrain_types": list(terrains)}
        processes[identity] = simulation

    for identity, tag, inputs, outputs in PRODUCTION:
        add(identity, "production", tag, inputs, outputs, terrains=FARM_TERRAINS if tag == "agriculture" else ())
    for identity, good, terrains in RESOURCE_PROFILES:
        add(identity, "extraction", "extraction", extracts=good, terrains=terrains)
    for good, outputs in RECOVERY:
        add("recover-" + good, "recovery", "recovery", (good,), outputs)
    return processes


def good_values(processes):
    values = {good: SOURCE_UNIT_VALUE for _, good, _ in RESOURCE_PROFILES}
    producers = {output["voxel"]: process for identity, process in processes.items()
                 if process["kind"] == "production" and identity != "coal-fuel-production"
                 for output in process["outputs"]}

    def value(good):
        if good not in values:
            process = producers[good]
            cost = sum(item["quantity"] * value(item["voxel"]) for item in process["inputs"])
            # 联产品的内容初价按总件数均分；报告成本仍保留整批，不宣称单品实际成本。
            values[good] = ceil(cost * VALUE_MARKUP / sum(item["quantity"] for item in process["outputs"])) if cost else SOURCE_UNIT_VALUE
        return values[good]

    for good in producers:
        value(good)
    return values


def png(size, pixel):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    rows = b"".join(b"\0" + b"".join(bytes(pixel(x, y)) for x in range(size)) for y in range(size))
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


def grain(x, y, size, seed):
    # 四角采样绕回同一张图，低频固有色纹样在平铺边界连续。
    def sample(dx, dy):
        ax, ay = (x // 8 + dx) % (size // 8), (y // 8 + dy) % (size // 8)
        value = ax * 73856093 ^ ay * 19349663 ^ seed
        value = (value ^ (value >> 13)) * 1274126177
        return ((value ^ (value >> 16)) & 255) / 255 - .5
    fx, fy = x % 8 / 8, y % 8 / 8
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    return round(12 * ((sample(0, 0) * (1 - fx) + sample(1, 0) * fx) * (1 - fy)
                      + (sample(0, 1) * (1 - fx) + sample(1, 1) * fx) * fy))


SOIL_PEBBLES = ((1, 24, 3), (15, 61, 2), (19, 109, 3), (29, 16, 2), (34, 85, 2),
                (47, 45, 3), (51, 122, 2), (64, 73, 2), (71, 7, 3), (80, 101, 2),
                (88, 36, 2), (100, 66, 3), (112, 114, 2), (120, 46, 2), (126, 91, 2))
ROCK_BLOCKS = ((7, 18, -8), (13, 85, 9), (37, 49, 3), (43, 117, -4),
               (61, 15, 11), (70, 84, -11), (87, 48, -3), (104, 113, 5), (120, 13, -6))


def soil_texture(x, y):
    for cx, cy, radius in SOIL_PEBBLES:
        dx, dy = (x - cx + 64) % 128 - 64, (y - cy + 64) % 128 - 64
        if dx * dx + 1.6 * dy * dy <= radius * radius:
            return tint(tuple((a + b) // 2 for a, b in zip(SOIL, ROCK)), grain(x, y, 128, 0x501))
    return tint(SOIL, grain(x, y, 128, 0x501))


def grass_texture(x, y):
    return tint(GRASS, grain(x, y, 128, 0x502))


def forest_ground_texture(x, y):
    leaves = (x % 32 - 8) ** 2 + 4 * (y % 32 - 12) ** 2 < 25
    return tint((92, 103, 56) if leaves else (43, 66, 54), grain(x, y, 128, 0x517))


def roots_texture(x, y):
    variation = grain(x, y, 128, 0x503)
    return tint(SOIL if variation < 3 else tuple((a + b) // 2 for a, b in zip(SOIL, GRASS)), variation)


def rock_texture(x, y):
    # 环面上的不规则石块边界形成裂纹；块面只改变固有色，不画方向性光影。
    px, py = x + grain(x, y, 128, 0x504) / 2, y + grain(x, y, 128, 0x505) / 2
    distances = sorted((((px - cx + 64) % 128 - 64) ** 2
                        + ((py - cy + 64) % 128 - 64) ** 2, shade)
                       for cx, cy, shade in ROCK_BLOCKS)
    change = -26 if distances[1][0] - distances[0][0] < 45 else distances[0][1]
    return tint(ROCK, change + grain(x, y, 128, 0x506) // 2)


def plate_texture(x, y):
    seam = x % 128 in (0, 64)
    rivet = x % 64 == 3 and y % 16 in (4, 5)
    return tint(BLUE, -10 if seam else -6 if rivet else grain(x, y, 128, 0x507))


def tread_texture(x, y):
    return tint(BLUE, 6 if (x + y) % 16 in (2, 3) or (x - y) % 16 in (2, 3) else grain(x, y, 128, 0x508))


def underside_texture(x, y):
    return tint((0x2b, 0x2e, 0x31))


def bogie_top_texture(x, y):
    ax, ay = min(x, 63 - x), min(y, 63 - y)
    seam = ax == 0 or ay == 0
    rivet = ax % 16 in (3, 12) and ay % 16 in (3, 12)
    return tint(GRAPHITE, -6 if seam else 8 if rivet else grain(min(ax, ay), max(ax, ay), 64, 0x509))


def bogie_side_texture(x, y):
    ax, ay = x % 32, y % 32
    axle_box = 10 <= ax < 22 and 9 <= ay < 25
    frame = 12 <= ay < 20 or (4 <= ax < 8 or 24 <= ax < 28) and 8 <= ay < 24
    if axle_box:
        return tint(GRAPHITE, 16 if ax in (10, 21) or ay in (9, 24) else 6)
    return tint(GRAPHITE, 10 if frame else grain(x, y, 64, 0x510))


def engine_texture(x, y):
    return tint(BLUE, -22 if 4 <= x < 28 and y % 8 in (2, 3) else grain(x, y, 32, 0x511))


def control_texture(x, y):
    return tint(GLASS if 3 <= x < 29 else BLUE, grain(x, y, 32, 0x512))


def storage_texture(x, y):
    return tint(RED, -12 if x in (2, 3, 28, 29) else 8 if 23 <= x < 27 and 12 <= y < 18 else grain(x, y, 32, 0x513))


def grille_texture(x, y):
    return tint(BLUE, -18 if x % 8 in (2, 3) and y % 8 in (2, 3) else grain(x, y, 32, 0x514))


def equipment_roof_texture(x, y):
    return tint(BLUE, grain(x, y, 32, 0x515))


def storage_roof_texture(x, y):
    return tint(RED, grain(x, y, 32, 0x516))


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
    files["voxels/shared/subsoil.png"] = png(128, soil_texture)
    for identity, size, top, side in (
        ("ground", 128, grass_texture, roots_texture),
        ("rock", 128, rock_texture, None),
        ("structural-metal", 128, tread_texture, plate_texture),
        ("bogie", 64, bogie_top_texture, bogie_side_texture),
        ("propulsion-equipment", 32, grille_texture, engine_texture),
        ("control-equipment", 32, equipment_roof_texture, control_texture),
        ("storage-equipment", 32, storage_roof_texture, storage_texture),
    ):
        files[f"voxels/{identity}/top.png"] = png(size, top)
        if side is not None:
            files[f"voxels/{identity}/side.png"] = png(size, side)
    for identity in ("ground", "subsoil", "rock", "structural-metal", "bogie", "propulsion-equipment",
                     "control-equipment", "storage-equipment"):
        files[f"voxels/{identity}/icon.png"] = icon(identity)


def faces(top, side=None, bottom=None):
    return {"top": top, "side": side or top, "bottom": bottom or top}


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


def building_shell(cells, origin, size):
    x0, y0, z0 = origin
    width, height, depth = size
    x1, y1, z1 = x0 + width - 1, y0 + height - 1, z0 + depth - 1
    for y in range(y0, y1 + 1):
        belt = y in (y0, y1) or (y - y0) % 12 < 2
        for x in range(x0, x1 + 1):
            if belt or x in (x0, x1) or (x - x0) % 12 < 2:
                for z in (z0, z1):
                    cells[x, y, z] = 1 if belt else 0
        for z in range(z0, z1 + 1):
            if belt or z in (z0, z1) or (z - z0) % 12 < 2:
                for x in (x0, x1):
                    cells[x, y, z] = 1 if belt else 0


def core_city_hub():
    cells = {}
    building_shell(cells, (-160, 0, -112), (320, 24, 224))
    for x0 in (-144, 48):
        building_shell(cells, (x0, 24, -32), (96, 48, 64))
        building_shell(cells, (x0 + 16, 72, -24), (64, 48, 48))
        cells.update({(x, 71, z): 1 for x in range(x0, x0 + 96) for z in range(-32, 32)})
        cells.update({(x, 119, z): 1 for x in range(x0 + 16, x0 + 80) for z in range(-24, 24)})
    building_shell(cells, (-28, 24, -24), (56, 144, 48))
    building_shell(cells, (-16, 168, -16), (32, 64, 32))
    cells.update({(x, 167, z): 1 for x in range(-28, 28) for z in range(-24, 24)})
    cells.update({(x, 231, z): 1 for x in range(-16, 16) for z in range(-16, 16)})
    # 基座的十字梁把塔楼、侧翼与外圈柱廊连成一体，不铺满巨型地块。
    for y in (0, 23):
        cells.update({(x, y, z): 1 for x in range(-160, 160) for z in range(-2, 2)})
        cells.update({(x, y, z): 1 for x in range(-2, 2) for z in range(-112, 112)})
    for x in range(-148, 141, 24):
        for z in (-111, 110):
            cells[x, 1, z] = 2
    value = blueprint("core-city-hub", "核心城市中枢", "building",
                      ["construction-material", "structural-metal", "storage-equipment"], cells,
                      {"plot_size": "giant", "building_type": "warehouse", "facility_tags": []})
    value["display"]["description"] = "320×224×232 格的城市仓储地标：长基座柱廊、两级侧翼与中央高塔；仓储来自实际货舱，外观待阶段试玩确认。"
    return value


def bastion_fortress():
    cells = {(x, y, z): 0 for y in range(20) for x in range(-176, 176) for z in (-160, 159)}
    cells.update({(x, y, z): 0 for y in range(20) for z in range(-160, 160) for x in (-176, 175)})
    for x0 in (-176, 144):
        for z0 in (-160, 128):
            building_shell(cells, (x0, 0, z0), (32, 112, 32))
            cells.update({(x, 111, z): 1 for x in range(x0, x0 + 32) for z in range(z0, z0 + 32)})
            cells[x0 + 1, 1, z0 + 1] = 2
            cells[x0 + 1, 2, z0 + 1] = 2
    building_shell(cells, (-32, 0, 128), (64, 80, 32))
    # 正面门洞通向内院；门楼两侧与城墙相连。
    for x in range(-12, 12):
        for y in range(16):
            for z in (128, 159):
                cells.pop((x, y, z), None)
    value = blueprint("bastion-fortress", "棱堡要塞", "building",
                      ["construction-material", "structural-metal", "storage-equipment"], cells,
                      {"plot_size": "giant", "building_type": "defense", "facility_tags": []})
    value["display"]["description"] = "352×320×112 格的围墙、四座角堡、门楼与内院；当前只由实际货舱提供仓储，不含武器，战斗用途待战斗阶段实现。"
    return value


def content_files():
    files = {}
    processes = process_definitions()
    values = good_values(processes)
    voxel_definitions = {}
    blueprint_definitions = {}
    template_definitions = {}

    def document(directory, kind, identity, value):
        data = (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        if len(data) > JSON_LIMIT:
            raise ValueError(f"{identity}: {len(data)} bytes exceeds {JSON_LIMIT}")
        files[f"{directory}/{identity}.{kind}.json"] = data
        if kind == "blueprint":
            blueprint_definitions[identity] = value
        elif kind == "settlement-template":
            template_definitions[identity] = value

    colors = {"soil": (125, 107, 76, 255),
              "organic": (99, 143, 81, 255), "equipment": (194, 148, 60, 255)}
    for material, color in colors.items():
        files[f"voxels/shared/{material}-face.png"] = png(32, lambda x, y: color)
        files[f"voxels/shared/{material}-icon.png"] = png(128, lambda x, y: color)
    files["settlement-types/shared/agricultural-town.png"] = png(48, lambda x, y: (226, 236, 220, 255))

    def voxel(identity, name, material, face_paths, icon_path, simulation=None, wheelset=False, light=False,
              tile_span=None, description=None):
        value = {"schema_version": 3, "id": identity,
                 "appearance": {"allowed_orientations": "four_way",
                                "presentation": {"kind": "cube", "material": material,
                                                 "faces": face_paths}},
                 "display": {"name": name, "description": description or f"{name}的第一版正式定义；数值待配平，占位贴图待美术制作。",
                             "icon": icon_path}}
        if tile_span is not None:
            value["appearance"]["presentation"]["tile_span"] = tile_span
        if simulation is not None:
            value["simulation"] = simulation
            value["appearance"]["presentation"]["shapes"] = (
                ALL_SHAPES if identity in ("structural-block", "construction-block") else
                ["cube", "plate"] if identity == "glass" else ["cube"])
        if wheelset:
            value["appearance"]["behaviors"] = {"wheelset": {}}
        if light:
            value["appearance"].setdefault("behaviors", {})["light"] = {}
        document("voxels", "voxel", identity, value)
        voxel_definitions[identity] = value

    def good(identity, mass, durability=180, behaviors=None):
        simulation = {"value": values[identity], "mass": mass, "max_durability": durability,
                      "tags": ["raw_good"] if identity in RAW_GOODS else []}
        if behaviors is not None:
            simulation["behaviors"] = behaviors
        return simulation

    sample_description = "{}的第一阶段风格样稿；数值待配平，画面待用户阶段试玩确认。"
    voxel("structural-metal", "结构金属", "painted_metal", faces("top.png", "side.png", "shared/vehicle-underside.png"),
          "icon.png", good("structural-metal", 0.028, 260), tile_span=4, description=sample_description.format("结构金属"))
    voxel("construction-material", "建筑材料", "concrete", faces("shared/soil-face.png"),
          "shared/soil-icon.png", good("construction-material", 0.046, 220))
    voxel("bogie", "转向架", "bare_metal", faces("top.png", "side.png", "shared/bogie-underside.png"),
          "icon.png", good("bogie", 0.034, 240), wheelset=True, tile_span=2, description=sample_description.format("转向架"))
    voxel("platform-equipment", "站台设备", "painted_metal", faces("shared/equipment-face.png"),
          "shared/equipment-icon.png", good("platform-equipment", 0.024, behaviors={"platform": {}}))
    voxel("propulsion-equipment", "柴油机组", "painted_metal", faces("top.png", "side.png", "shared/equipment-underside.png"),
          "icon.png", good("propulsion-equipment", 1.12, behaviors={"propulsion": {"traction": 240, "rated_speed": 22}}),
          tile_span=1, description=sample_description.format("柴油机组"))
    voxel("control-equipment", "驾驶台", "painted_metal", faces("top.png", "side.png", "shared/equipment-underside.png"),
          "icon.png", good("control-equipment", 0.018, behaviors={"control": {}}), tile_span=1, description=sample_description.format("驾驶台"))
    voxel("storage-equipment", "货舱", "painted_metal", faces("top.png", "side.png", "shared/equipment-underside.png"),
          "icon.png", good("storage-equipment", 0.016, behaviors={"storage": {"capacity": 180}}),
          tile_span=1, description=sample_description.format("货舱"))
    goods = (("staple-food", "主食", 0.7), ("clothing", "衣物", 0.8),
             ("prepared-food", "加工食品", 0.6), ("consumer-goods", "日用品", 1.4),
             ("consumer-electronics", "消费电子", 1.6), ("medical-supplies", "医疗用品", 0.4))
    for identity, name, mass in goods:
        voxel(identity, name, "organic", faces("shared/organic-face.png"), "shared/organic-icon.png", good(identity, mass))
    voxel("stone", "石料", "stone_soil", faces("shared/soil-face.png"), "shared/soil-icon.png", good("stone", 0.042))
    voxel("ground", "地表", "organic", faces("top.png", "side.png", "shared/subsoil.png"),
          "icon.png", tile_span=4, description=sample_description.format("地表"))
    voxel("forest-ground", "森林地表占位", "organic", faces("top.png"),
          "icon.png", tile_span=4, description="森林地表占位，正式地形美术归 #358。")
    files["voxels/forest-ground/top.png"] = png(128, forest_ground_texture)
    files["voxels/forest-ground/icon.png"] = png(128, forest_ground_texture)
    voxel("subsoil", "土层", "stone_soil", faces("shared/subsoil.png"),
          "icon.png", tile_span=4, description=sample_description.format("土层"))
    voxel("rock", "岩层", "stone_soil", faces("top.png"),
          "icon.png", tile_span=4, description=sample_description.format("岩层"))
    style_assets(files)

    for spec in VEHICLES:
        value = small_locomotive() if spec[0] == "small-locomotive" else locomotive(spec)
        document("blueprints", "blueprint", value["id"], value)
    station = {(x, 0, z): 0 for x in range(-96, 96)
               for z in (*range(-24, -20), *range(20, 24))}
    station.update({(x, 0, z): 0 for x in range(-2, 2) for z in range(-20, 20)})
    station[0, 1, -22] = station[0, 1, 22] = 1
    document("blueprints", "blueprint", "small-station",
             blueprint("small-station", "小站点", "building", ["construction-material", "platform-equipment"],
                       station, {"plot_size": "small_station", "building_type": "station", "facility_tags": []}))
    for tracks in (1, 2):
        half_width = 16 + 17 * (tracks - 1)
        bridge = {(x, 0, z): 0 for x in range(-half_width, half_width + 1) for z in range(-3, 4)}
        portal_half_width = 17 * tracks
        tunnel = {(x, y, 0): 0 for x in range(-portal_half_width - 2, portal_half_width + 2) for y in range(44)
                  if x < -portal_half_width or x >= portal_half_width or y >= 42}
        for kind, name, cells in (("bridge", "桥梁", bridge), ("tunnel-portal", "隧道口", tunnel)):
            identity = f"{kind}-{tracks}"
            document("blueprints", "blueprint", identity, blueprint(identity, f"{tracks}轨{name}", "scene_model", ["rock"], cells))
    for identity, name, surface, local_amplitude in (
        ("plains", "平原", "ground", 0.7), ("hills", "丘陵", "ground", 2.8),
        ("forest", "森林", "forest-ground", 0.7), ("desert", "荒漠", "subsoil", 0.7),
        ("mountains", "山地", "rock", 2.8),
    ):
        description = f"基础贯通{name}，提供局部起伏与铁路模型。"
        if identity in ("forest", "desert", "mountains"):
            description += "占位，正式地形美术归 #358。"
        document("terrain-types", "terrain-type", identity,
                 {"schema_version": 1, "id": identity, "display": {"name": name, "description": description},
                  "surface_voxel": surface, "subsurface_voxel": "subsoil", "rock_voxel": "rock",
                  "local_height_noise": [{"scale_meters": 140, "amplitude": local_amplitude}],
                  "decorations": [], "decoration_target_per_square_meter": 0,
                  "railway_models": [{"tracks": n, "bridge": f"bridge-{n}", "tunnel_portal": f"tunnel-portal-{n}"} for n in (1, 2)]})
    for identity, name, behavior in (
        ("industrial-machinery", "机床", {"production": {"work_rate": REFERENCE_WORK_RATE}}),
        ("extraction-equipment", "钻头", {"extraction": {"work_rate": REFERENCE_WORK_RATE}}),
        ("construction-equipment", "基础吊机", {"construction": {"work_rate": 1, "max_plot_size": "medium"}}),
        # 额定功率在模板和建筑布局就绪后统一推导。
        ("power-equipment", "发电机", {"power": {}}),
    ):
        voxel(identity, name, "painted_metal", faces("shared/equipment-face.png"),
              "shared/equipment-icon.png", good(identity, 0.08, behaviors=behavior))
    fuel = good("fuel", 0.7)
    voxel("fuel", "燃料", "organic", faces("shared/organic-face.png"), "shared/organic-icon.png", fuel)

    for identity, name, physical_source, texture in NEW_GOODS:
        source = voxel_definitions[physical_source]["simulation"]
        simulation = good(identity, source["mass"], source["max_durability"])
        if identity in ("machine-gun", "cannon"):
            factor = CANNON_FACTOR if identity == "cannon" else 1
            simulation["behaviors"] = {"weapon": {"damage": ceil(source["max_durability"] * WEAPON_DAMAGE_SHARE) * factor,
                                                   "interval": MACHINE_GUN_INTERVAL_SECONDS * factor}}
        material = "glass" if identity == "glass" else "concrete" if identity == "construction-block" else {
            "soil": "stone_soil", "organic": "organic", "equipment": "painted_metal"}[texture]
        voxel(identity, name, material, faces(f"shared/{texture}-face.png"), f"shared/{texture}-icon.png", simulation,
              light=identity == "lamp")

    for identity, simulation in processes.items():
        item = simulation["extracts"] if simulation["kind"] == "extraction" else (
            simulation["inputs"][0] if simulation["kind"] == "recovery" else simulation["outputs"][0])
        operation = {"production": "生产", "extraction": "开采", "recovery": "回收"}[simulation["kind"]]
        name = voxel_definitions[item["voxel"]]["display"]["name"]
        document("processes", "process", identity,
                 {"schema_version": 1, "id": identity, "simulation": simulation,
                  "display": {"name": operation + name,
                              "description": "正式经济流程；批量、价值与速率为待用户试玩确认的第一版。"}})

    # 吊机是独立货物；建筑只引用它，不手写汇总施工能力。
    for identity, name, span, plot_limit, vehicle_limit in (
        ("workshop-crane", "车间吊机", 8, "large", "large"),
        ("gantry-crane", "龙门吊", 16, "giant", "titan"),
    ):
        crane = {(x, y, 0): 0 for x in range(-span, span + 1) for y in range(8)
                 if x in (-span, span) or y == 7}
        document("blueprints", "blueprint", identity,
                 blueprint(identity, name, "large_equipment", ["structural-metal"], crane,
                           {"behaviors": {"construction": {"work_rate": 4, "max_plot_size": plot_limit,
                                                          "max_size_class": vehicle_limit}}}))

    buildings = (
        ("basic-shed", "基础工棚", "small", "construction", None, "construction-equipment"),
        ("vehicle-workshop", "车辆车间", "medium", "construction", None, "workshop-crane"),
        ("small-warehouse", "小仓库", "small", "warehouse", None, "storage-equipment"),
        ("power-station", "发电站", "medium", "power", None, "power-equipment"),
        ("farm", "农场", "medium", "production", "agriculture", "industrial-machinery"),
        ("food-factory", "食品厂", "small", "production", "food_processing", "industrial-machinery"),
        ("extraction-site", "开采场", "medium", "production", "extraction", "extraction-equipment"),
        ("metallurgy-factory", "冶金厂", "large", "production", "metallurgy", "industrial-machinery"),
        ("building-materials-factory", "建材厂", "medium", "production", "building_materials", "industrial-machinery"),
        ("light-industry-factory", "轻工厂", "medium", "production", "light_industry", "industrial-machinery"),
        ("chemical-factory", "化工厂", "large", "production", "chemical", "industrial-machinery"),
        ("assembly-factory", "装配厂", "large", "production", "assembly", "industrial-machinery"),
        ("electronics-factory", "电子厂", "medium", "production", "electronics", "industrial-machinery"),
        ("recovery-factory", "回收厂", "medium", "production", "recovery", "industrial-machinery"),
        ("heavy-vehicle-factory", "重型车辆工厂", "large", "construction", None, "gantry-crane"),
    )
    plot_sizes = {}
    for identity, name, size, kind, tag, equipment in buildings:
        plot_sizes[identity] = size
        half_x, half_z, height = (20, 40, 20) if identity == "vehicle-workshop" else (
            (32, 80, 44) if identity == "heavy-vehicle-factory" else (8, 10, 8))
        cells = {(x, 0, z): 0 for x in range(-half_x, half_x) for z in range(-half_z, half_z)}
        # 四角立柱和屋顶边框与地板相连，保留内部设备空间。
        cells.update({(x, y, z): 0 for x in (-half_x, half_x - 1) for z in (-half_z, half_z - 1)
                      for y in range(1, height)})
        cells.update({(x, height - 1, z): 0 for x in range(-half_x, half_x) for z in range(-half_z, half_z)
                      if x in (-half_x, half_x - 1) or z in (-half_z, half_z - 1)})
        nested = equipment in ("workshop-crane", "gantry-crane")
        palette = ["construction-material"] if nested else ["construction-material", equipment]
        if not nested:
            cells[0, 1, 0] = 1
        value = blueprint(identity, name, "building", palette, cells,
                          {"plot_size": size, "building_type": kind, "facility_tags": [tag] if tag else []})
        if nested:
            value["layout"]["composites"] = [{"at": [0, 1, 0], "blueprint": equipment}]
        document("blueprints", "blueprint", identity, value)

    settlements = (
        ("agricultural-town", "农业镇", ("plains", "hills"), ("farm", "food-factory")),
        ("mining-town", "矿业镇", ("mountains", "hills"), ("extraction-site", "metallurgy-factory", "building-materials-factory")),
        ("forestry-town", "林业镇", ("forest",), ("extraction-site", "light-industry-factory")),
        ("oilfield-town", "油田镇", ("desert",), ("extraction-site", "chemical-factory")),
        ("industrial-city", "工业城", ("plains",), ("assembly-factory", "electronics-factory", "recovery-factory", "heavy-vehicle-factory")),
    )
    for identity, name, preferred_terrain, major_buildings in settlements:
        document("settlement-types", "settlement-type", identity,
                 {"schema_version": 3, "id": identity,
                  "simulation": {"preferred_terrain": ["core:" + terrain for terrain in preferred_terrain], "weight": 3,
                                 "population_min": 120, "population_max": 360},
                  "display": {"name": name, "description": "按 Demo 目录选择偏好地貌；经济参数待用户试玩确认。",
                              "map_symbol": "shared/agricultural-town.png", "major": identity == "industrial-city"}})
        plots = [{"id": 1, "size": "small_station", "at": [-104, 1, -176], "orientation": "px_py", "building": "small-station"},
                 {"id": 2, "size": "small_station", "at": [104, 1, -176], "orientation": "px_py"}]
        prebuilt = ("vehicle-workshop", "small-warehouse", "power-station") + major_buildings
        # 各行列间隔足以容纳旋转后的大地块；第十地块留给建城工棚。
        positions = ((-224, 0), (0, 0), (224, 0), (-224, 224), (0, 224), (224, 224), (-224, 448), (0, 448))
        for index, (x, z) in enumerate(positions):
            plot = {"id": index + 3, "size": plot_sizes[prebuilt[index]] if index < len(prebuilt) else "small",
                    "at": [x, 1, z], "orientation": "px_py"}
            if index < len(prebuilt):
                plot["building"] = prebuilt[index]
            plots.append(plot)
        ground = {(x, 0, z): 0 for x in range(-328, 328) for z in range(-2, 2)}
        ground.update({(x, 0, z): 0 for x in range(-2, 2) for z in range(-232, 552)})
        for row in (224, 448):
            ground.update({(x, 0, z): 0 for x in range(-328, 328) for z in range(row - 2, row + 2)})
        document("blueprints", "blueprint", identity + "-ground",
                 blueprint(identity + "-ground", name + "地面", "scene_model", ["ground"], ground,
                           appearance={"flatten": {"from": [-328, -232], "to": [327, 551], "height": 1}}))
        document("settlement-templates", "settlement-template", identity,
                 {"schema_version": 2, "id": identity, "settlement_type": identity, "weight": 3,
                  "scene_model": identity + "-ground", "plots": plots,
                  "founding": [{"plot": 1, "building": "small-station"}, {"plot": 10, "building": "basic-shed"}],
                  "inventory": [],
                  "production_plans": [{"plot": plot, "capability": capability, "process": process,
                                        "enabled": enabled, "priority": INITIAL_PLAN_PRIORITY}
                                       for plot, capability, process, enabled in TEMPLATE_PLANS[identity]],
                  "display": {"name": name + "经济布局", "description": "十地块布局，含两个站点地块与真实生产计划；特殊库存为空，普通库存与现车由核心按生成配置初始化。"}})

    for value in (core_city_hub(), bastion_fortress()):
        document("blueprints", "blueprint", value["id"], value)
    city = json.loads(files["settlement-templates/industrial-city.settlement-template.json"])
    city.update(id="core-city", scene_model="core-city-ground")
    city["plots"].extend([
        {"id": 11, "size": "giant", "at": [-224, 1, 800], "orientation": "px_py", "building": "core-city-hub"},
        {"id": 12, "size": "giant", "at": [224, 1, 800], "orientation": "px_py", "building": "bastion-fortress"},
    ])
    city["display"] = {"name": "核心城市", "description": "工业城的十二地块布局；两座巨型仓储建筑、重型车辆工厂与原有工业和公共设施。要塞战斗用途待战斗阶段实现。"}
    document("settlement-templates", "settlement-template", "core-city", city)
    ground = {(x, 0, z): 0 for x in range(-432, 432) for z in range(-2, 2)}
    ground.update({(x, 0, z): 0 for x in range(-2, 2) for z in range(-232, 1000)})
    for row in (224, 448, 800):
        ground.update({(x, 0, z): 0 for x in range(-432, 432) for z in range(row - 2, row + 2)})
    document("blueprints", "blueprint", "core-city-ground",
             blueprint("core-city-ground", "核心城市地面", "scene_model", ["ground"], ground,
                       appearance={"flatten": {"from": [-432, -232], "to": [431, 999], "height": 1}}))

    def voxel_behaviors(blueprint_id, capability):
        # 本批生产建筑、发电站和起始机车的相应设备均是直接体素。
        # 这里只数制备中的实际格子，不求通用设计 BOM 或复制核心分配规则。
        layout = blueprint_definitions[blueprint_id]["layout"]
        return [behavior for *_, index in layout["cells"]
                if (behavior := voxel_definitions[layout["palette"][index]["voxel"]]
                    .get("simulation", {}).get("behaviors", {}).get(capability)) is not None]

    per_generator_outputs = []
    for template in template_definitions.values():
        buildings_by_plot = {plot["id"]: plot["building"] for plot in template["plots"] if "building" in plot}
        demand = sum(sum(behavior["work_rate"] for behavior in voxel_behaviors(buildings_by_plot[plan["plot"]], plan["capability"]))
                     * processes[plan["process"]]["power_per_work"]
                     for plan in template["production_plans"] if plan["enabled"])
        generator_count = sum(len(voxel_behaviors(building, "power")) for building in buildings_by_plot.values())
        per_generator_outputs.append(ceil(demand * POWER_HEADROOM / generator_count))
    output = max(per_generator_outputs)
    power = voxel_definitions["power-equipment"]
    power["simulation"]["behaviors"]["power"]["output"] = output
    document("voxels", "voxel", "power-equipment", power)
    fuel["fuel_energy"] = output * FUEL_UNIT_SECONDS
    document("voxels", "voxel", "fuel", voxel_definitions["fuel"])

    needs = ("food", "clothing", "improved_diet", "consumer_goods", "consumer_electronics", "medical_supplies")
    consumption = []
    for order, (identity, _, _) in enumerate(goods):
        roles = ("food", "improved_diet") if identity == "prepared-food" else (needs[order],)
        consumption.append({"good": identity, "selection_order": order,
                            "satisfies": [{"need": role, "amount_per_good_unit": 1} for role in roles]})
    document("rules", "population-rule", "basic-population",
             {"schema_version": 1, "id": "basic-population", "simulation": {
                 "needs": {need: {"class": "basic" if need in ("food", "clothing") else "improvement",
                                  "amount_per_person_second": NEED_UNITS_PER_PERSON_HOUR / SECONDS_PER_HOUR,
                                  "weight": 1} for need in needs},
                 "consumption_goods": consumption, "labor_ratio": LABOR_RATIO,
                 "consumption_interval_ticks": CONSUMPTION_SECONDS * TICKS_PER_SECOND,
                 "statistics_period_ticks": STATISTICS_SECONDS * TICKS_PER_SECOND,
                 "base_growth_per_second": DAILY_GROWTH / (HOURS_PER_DAY * SECONDS_PER_HOUR),
                 "improvement_growth_per_second": DAILY_GROWTH / (HOURS_PER_DAY * SECONDS_PER_HOUR),
                 "maximum_loss_per_second": DAILY_MAXIMUM_LOSS / (HOURS_PER_DAY * SECONDS_PER_HOUR)}})
    document("rules", "market-rule", "basic-market", {"schema_version": 2, "id": "basic-market", "simulation": {
        "basic_reserve_hours": BASIC_RESERVE_HOURS, "production_reserve_hours": PRODUCTION_RESERVE_HOURS,
        "improvement_reserve_hours": BASIC_RESERVE_HOURS / 4,
        "default_goods_prices": {"sensitivity_basis_points": round(MARKET_SENSITIVITY * BASIS_POINTS),
                                 "minimum_multiplier_basis_points": round(MARKET_MULTIPLIER_LIMITS[0] * BASIS_POINTS),
                                 "maximum_multiplier_basis_points": round(MARKET_MULTIPLIER_LIMITS[1] * BASIS_POINTS),
                                 "spread_basis_points": round(MARKET_MARGIN * BASIS_POINTS)},
        "good_prices": [], "vehicle_buy_multiplier_basis_points": round((1 - MARKET_MARGIN) * BASIS_POINTS),
        "vehicle_sell_multiplier_basis_points": round((1 + MARKET_MARGIN) * BASIS_POINTS),
        "vehicle_purchase_limits": [{"size": size, "maximum_for_sale": VEHICLE_PURCHASE_LIMIT}
                                    for size in ("small", "medium", "large", "extra_large", "titan")],
        "automatic_evaluation_interval_ticks": AUTOMATIC_EVALUATION_SECONDS * TICKS_PER_SECOND,
        "recent_report_limit": RECENT_REPORT_LIMIT, "commission_money_per_work": 2,
        "rent_money_per_capacity_hour": max(1, ceil(SOURCE_UNIT_VALUE / 10)),
        "rentable_capacity_ratio_basis_points": round(RENTABLE_CAPACITY_SHARE * BASIS_POINTS),
        "lease_duration_ticks": LEASE_DAYS * HOURS_PER_DAY * SECONDS_PER_HOUR * TICKS_PER_SECOND,
        "maximum_lease_duration_multiple": MAXIMUM_LEASE_MULTIPLE}})
    document("rules", "construction-rule", "basic-construction", {"schema_version": 1, "id": "basic-construction", "simulation": {
        "founding_duration_ticks": 2400, "construction_work_per_item": 8, "refit_work_per_changed_item": 5,
        "deconstruction_work_per_item": 3, "repair_work_per_damaged_part": 4, "power_per_work": 0.4, "labor_per_work": 0.25}})
    document("railway-rules", "railway-rule", "basic-railway", {"schema_version": 2, "id": "basic-railway",
        "braking_deceleration": 0.7, "minimum_acceleration": 0.15, "minimum_gap_meters": 28,
        "approach_length_meters": 80, "departure_length_meters": 80, "node_speed_limit_meters_per_second": 15,
        "rail_units_per_meter": 0.7, "build_seconds_per_meter": 3, "repair_seconds_per_break": 48,
        "rail_good": "rail", "repair_rail_units_per_break": 18,
        "appearance": {"minimum_curve_radius_meters": 80, "maximum_grade": 0.08}})
    document("combat-rules", "combat-rule", "basic-combat", {"schema_version": 1, "id": "basic-combat",
        "retreat_seconds": 24, "morale_damage_coefficient": 0.015, "weapon_destroyed_morale_loss": 0.18,
        "morale_recovery_per_second": 0.012, "surrender_threshold": 0.12, "new_lair_interval_seconds": 5400})
    # 阈值仅供示例验证，分布待配平。
    # 非平原带的起伏上限均超过 18 米噪声总振幅；不同上限消除闭区间边界的并列匹配。
    bands = [{"terrain": terrain, "minimum_elevation": low, "maximum_elevation": high,
              "minimum_moisture": dry, "maximum_moisture": wet, "maximum_relief": relief,
              "detour_multiplier": detour, "maximum_tracks": 2}
             for terrain, low, high, dry, wet, relief, detour in (
                 ("plains", -1, 10, 0.35, 0.65, 0.75, 1.12),
                 ("hills", -1, 10, 0.35, 0.65, 100, 1.35),
                 ("forest", -1, 30, 0.65, 2, 98, 1.35),
                 ("desert", -1, 30, -1, 0.35, 97, 1.35),
                 ("mountains", 10, 30, -1, 2, 96, 1.35),
             )]
    extraction_rate = BATCH_UNITS * REFERENCE_WORK_RATE / processes["stone-quarrying"]["work_required"]
    resources = [{"good": good, "allowed_terrain_types": list(terrains), "probability": RESOURCE_PROBABILITY,
                  "minimum_reserve": ceil(extraction_rate * RESOURCE_RESERVE_HOURS[0] * SECONDS_PER_HOUR),
                  "maximum_reserve": ceil(extraction_rate * RESOURCE_RESERVE_HOURS[1] * SECONDS_PER_HOUR),
                  "renewable": good == "timber",
                  "recovery_units_per_second": extraction_rate * FOREST_RECOVERY_SHARE if good == "timber" else 0,
                  "regeneration_interval_seconds": GLOBAL_CHECK_MINUTES * 60,
                  "global_regeneration_remaining_reserve_ratio": GLOBAL_REGEN_RATIO}
                 for _, good, terrains in RESOURCE_PROFILES]
    cargo_capacity = sum(behavior["capacity"] for spec in VEHICLES for behavior in voxel_behaviors(spec[0], "storage"))
    small = blueprint_definitions["small-locomotive"]["layout"]
    small_base_value = sum(values[small["palette"][index]["voxel"]] for *_, index in small["cells"])
    # 只估算满舱采购与造一台机车的预算，不计算核心委托报价。
    initial_money = ceil(START_BUDGET_MULTIPLIER * (cargo_capacity * max(values[good] for good, _, _ in goods) + small_base_value))
    document("generation-profiles", "generation-profile", "basic-through-world", {
        "schema_version": 2, "id": "basic-through-world",
        "display": {"name": "经济运输验证世界", "description": "真实货物、流程和六份模板计划；数值待试玩。NPC 库存和现车初始化已启用，车队待 #340，真实起步贸易待 #386。"},
        "configuration": {"width_meters": 1800, "height_meters": 1200, "minimum_node_spacing_meters": 300,
            "candidate_probability": 0.9, "elevation_noise": [{"scale_meters": 780, "amplitude": 14}, {"scale_meters": 260, "amplitude": 4}],
            "moisture_noise": [{"scale_meters": 600, "amplitude": 1}], "river_noise": [{"scale_meters": 900, "amplitude": 1}],
            "lake_elevation_threshold": 4, "river_band_half_width": 0.045, "terrain_bands": bands,
            "terrain_sample_spacing_meters": 60, "settlement_ratio": 0.45, "lair_ratio": 0,
            "hostile_settlement_ratio": 0, "lair_weights": [],
            "resources": resources,
            "initial_railway": {"maximum_tracks": 2, "minimum_initial_tracks": 1, "maximum_initial_tracks": 2, "additional_cycle_edges": 1},
            "budget": {"maximum_stage_retries": 4, "maximum_world_retries": 4, "maximum_candidates": 50000, "maximum_search_work": 4000000},
            "npc_initialization": {"fill_inputs_and_living_targets": NPC_FILL_INPUTS_AND_LIVING_TARGETS,
                                   "output_reserve_hours": PRODUCTION_RESERVE_HOURS,
                                   "minimum_for_sale_vehicles": NPC_MINIMUM_FOR_SALE_VEHICLES,
                                   "maximum_for_sale_vehicles": NPC_MAXIMUM_FOR_SALE_VEHICLES,
                                   "for_sale_vehicle_candidates": NPC_FOR_SALE_VEHICLE_CANDIDATES},
            "start_constraints": {"minimum_reachable_non_hostile_settlements": 2, "minimum_reachable_empty_nodes": 1,
                                  "require_station": True, "exclude_adjacent_lairs": True, "fully_loaded_purchase_rounds_per_train": 1,
                                  "required_affordable_commission_design": "small-locomotive"},
            "rules": {"population": "basic-population", "market": "basic-market", "construction": "basic-construction",
                      "railway": "basic-railway", "combat": "basic-combat"}},
        "start": {"initial_money": initial_money, "trains": [{"vehicles": [spec[0]], "inventory": []} for spec in VEHICLES]}})
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
        result = subprocess.run([sys.executable, str(ROOT / "scripts/content/check_economy.py"), str(args.output_dir)])
        if result.returncode:
            return result.returncode
    else:
        for path, data in files.items():
            target = resources / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
    print(f"{'校验' if args.check else '生成'} {len(files)} 个文件；超大型蓝图 {len(files['blueprints/extra-large-locomotive.blueprint.json'])} 字节。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
