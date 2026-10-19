"""生成经济运输验证世界的正式内置内容。

python scripts/content/generate_small_world.py
python scripts/content/generate_small_world.py --check
python scripts/content/generate_small_world.py --output-dir TestResults/reproduction

脚本生成 resources/，手绘图标按 HAND_DRAWN_ICONS 保留；创作模式蓝图和 Blender 导出按 AUTHORED_FILES 保留，
只检查存在和 JSON Schema。校验模式拒绝缺失、多余或其他生成文件字节不同的文件（校验需要 jsonschema）。
生成只用 Python 标准库。
自然地形与三十四种货物已有正式美术：十二种原料、六种生活货物和十六种工业与建造货物。
小型机车保留第一阶段风格样稿，聚居地符号来自 ui_symbols；
其余美术仍待制作，数值待配平，
数值和画面待用户试玩确认；十一种车辆与两列小型起始车队已接入。
校验同时检查正式内容的经济关系；不生成生物聚落或存档。
"""

import argparse
from collections import Counter
import hashlib
import json
from math import ceil, cos, pi, sin
from pathlib import Path
import subprocess
import sys

from ui_symbols import SETTLEMENT_SYMBOLS, png, symbol_png


ROOT = Path(__file__).resolve().parents[2]
JSON_LIMIT = 1024 * 1024
VEHICLES = (
    # id、名称、角色、档位、长宽高、轨距、转向架高度、轴距、端距、设备组数。
    ("small-locomotive", "小型机车", "locomotive", "small", 52, 7, 9, 3, 1, 2, 4, 1),
    ("small-freight-wagon", "小型货车", "freight", "small", 52, 7, 9, 3, 1, 2, 4, 1),
    ("small-armed-wagon", "小型武装车", "armed", "small", 52, 7, 9, 3, 1, 2, 4, 1),
    ("medium-locomotive", "中型机车", "locomotive", "medium", 64, 10, 13, 7, 2, 3, 6, 2),
    ("medium-freight-wagon", "中型货车", "freight", "medium", 64, 10, 13, 7, 2, 3, 6, 2),
    ("medium-armed-wagon", "中型武装车", "armed", "medium", 64, 10, 13, 7, 2, 3, 6, 2),
    ("large-freight-wagon", "大型货车", "freight", "large", 80, 14, 18, 7, 2, 3, 8, 3),
    ("extra-large-locomotive", "超大型机车", "locomotive", "extra_large", 104, 20, 26, 15, 4, 5, 10, 4),
    ("extra-large-freight-wagon", "超大型货车", "freight", "extra_large", 104, 20, 26, 15, 4, 5, 10, 4),
)
START_TRAINS = (
    ("small-locomotive", "small-freight-wagon", "small-freight-wagon", "small-freight-wagon"),
    ("small-locomotive", "small-freight-wagon", "small-freight-wagon", "small-armed-wagon"),
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
BASIC_RESERVE_HOURS = 4
PRODUCTION_RESERVE_HOURS = BASIC_RESERVE_HOURS / 2
IMPROVEMENT_RESERVE_HOURS = BASIC_RESERVE_HOURS / 4
LOCAL_FOOD_SHARE = 0.25
WAREHOUSE_HEADROOM = 2
# 大仓库没有独立预算。件数是小仓库推导结果的这个倍数，待试玩确认。
LARGE_WAREHOUSE_FACTOR = 2
NPC_FILL_INPUTS_AND_LIVING_TARGETS = True
NPC_MINIMUM_FOR_SALE_VEHICLES = 1
NPC_MAXIMUM_FOR_SALE_VEHICLES = 2
NPC_FOR_SALE_VEHICLE_CANDIDATES = (
    "small-locomotive", "small-freight-wagon", "small-armed-wagon",
    "medium-locomotive", "medium-freight-wagon", "medium-armed-wagon",
    "large-locomotive", "large-freight-wagon", "extra-large-locomotive",
    "extra-large-freight-wagon", "titan-armed-vehicle",
)
INITIAL_SMALL_VEHICLE_MATERIAL_SETS = 1
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
    "metallurgy": (30, 2, 1), "building_materials": (30, 1, 1),
    "light_industry": (30, 1, 1), "chemical": (30, 2, 1),
    "food_processing": (60, 1, 1), "assembly": (40, 1, 1),
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
                    (8, "production", "construction-material-processing", True),
                    (9, "production", "rail-making", True)),
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


BLUE = (0x5a, 0x6b, 0x78)
GRAPHITE = (0x33, 0x37, 0x3b)
GLASS = (0x1e, 0x28, 0x30)
RED = (0x7b, 0x3a, 0x2c)
SOIL = (0x76, 0x59, 0x3f)
ROCK = (0x7f, 0x7c, 0x76)
GRASS = (0x6f, 0x8f, 0x4a)
DRY_GRASS = (0xa3, 0x9a, 0x5c)
FOREST = (0x55, 0x68, 0x3a)
CONCRETE = (0xa8, 0xa5, 0x9e)
STEEL = (0x8e, 0x93, 0x98)
YELLOW = (0xd9, 0xa6, 0x2b)
SOIL_FACE = (125, 107, 76)
EQUIPMENT_FACE = (194, 148, 60)
CRUDE_OIL = (58, 67, 75)
SITE_FENCE = (0x58, 0x66, 0x71)
PAINTABLE_MATERIALS = ("painted_metal", "concrete")
DEFAULT_PAINTS = {
    "structural-metal": BLUE,
    "propulsion-equipment": BLUE,
    "control-equipment": BLUE,
    "storage-equipment": RED,
    "concrete": CONCRETE,
    "construction-material": CONCRETE,
    "construction-block": CONCRETE,
    "platform-equipment": EQUIPMENT_FACE,
    "industrial-machinery": EQUIPMENT_FACE,
    "extraction-equipment": EQUIPMENT_FACE,
    "construction-equipment": EQUIPMENT_FACE,
    "power-equipment": EQUIPMENT_FACE,
    "mechanical-components": EQUIPMENT_FACE,
    "electrical-components": EQUIPMENT_FACE,
    "electronic-components": EQUIPMENT_FACE,
    "structural-block": STEEL,
    "lamp": EQUIPMENT_FACE,
    "machine-gun": EQUIPMENT_FACE,
    "cannon": EQUIPMENT_FACE,
    "crude-oil": CRUDE_OIL,
    "fuel": RED,
    "chemical-feedstock": BLUE,
    "site-fence": SITE_FENCE,
    "road-barrier": YELLOW,
    "street-lamp": GRAPHITE,
}


def tint(color, change=0):
    return tuple(max(0, min(255, value + change)) for value in color) + (255,)


def linear_luminance(color):
    channels = (value / 255 for value in color[:3])
    return sum(weight * (value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4)
               for weight, value in zip((0.2126, 0.7152, 0.0722), channels))


def neutral_texture(texture, base):
    base_luminance = linear_luminance(base)

    def pixel(x, y):
        color = texture(x, y)
        if color[3] == 0:
            return color[:3] + (0,)
        luminance = min(1, linear_luminance(color) / base_luminance)
        gray = round(255 * (12.92 * luminance if luminance <= 0.0031308 else
                            1.055 * luminance ** (1 / 2.4) - 0.055))
        return (gray, gray, gray, 255)

    return pixel


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
ROCK_CRACKS = ((5, 21, 27, 34), (27, 34, 43, 32), (94, 99, 111, 110),
               (111, 110, 130, 109), (-6, 61, 12, 55), (12, 55, 19, 43))
MINERAL_FRAGMENTS = ((3, 4, -4), (21, 2, 9), (35, 8, -1), (57, 3, 6), (12, 17, 0),
                     (27, 19, -8), (47, 18, 11), (61, 24, -5), (5, 31, 6), (18, 33, -3),
                     (35, 32, 12), (48, 35, 0), (59, 43, 8), (7, 48, -8), (25, 46, 7),
                     (38, 53, -2), (51, 59, -5), (17, 62, 11), (29, 58, -10), (50, 6, -6),
                     (3, 57, 4), (33, 42, -6), (44, 46, 4), (16, 8, 2))


def soil_texture(x, y):
    for cx, cy, radius in SOIL_PEBBLES:
        dx, dy = (x - cx + 64) % 128 - 64, (y - cy + 64) % 128 - 64
        if dx * dx + 1.6 * dy * dy <= radius * radius:
            return tint(tuple((a + b) // 2 for a, b in zip(SOIL, ROCK)), grain(x, y, 128, 0x501))
    return tint(SOIL, grain(x, y, 128, 0x501))


def grass_texture(x, y, color=GRASS):
    blades = -3 if (x * 13 + y * 7) % 31 < 2 else 0
    return tint(color, grain(x, y, 128, 0x502) + grain(x // 4, y // 4, 32, 0x518) + blades)


def forest_ground_texture(x, y):
    for cx, cy, radius in SOIL_PEBBLES:
        dx, dy = (x - cx + 64) % 128 - 64, (y - cy + 64) % 128 - 64
        if dx * dx + 3 * dy * dy < radius * radius:
            return tint((0x76, 0x59, 0x36), grain(x, y, 128, 0x517))
    return grass_texture(x, y, FOREST)


def roots_texture(x, y, grass=GRASS):
    variation = grain(x, y, 128, 0x503)
    return tint(SOIL if variation < 2 else tuple((a + b) // 2 for a, b in zip(SOIL, grass)), variation)


def rock_texture(x, y):
    # 裂纹只画局部断续分支，不把整张岩层切成铺路石块。
    change = grain(x // 4, y // 4, 32, 0x504) + grain(x, y, 128, 0x506) // 2
    for ax, ay, bx, by in ROCK_CRACKS:
        dx, dy = (x - ax + 64) % 128 - 64, (y - ay + 64) % 128 - 64
        ex, ey = bx - ax, by - ay
        along = (dx * ex + dy * ey) / (ex * ex + ey * ey)
        if 0 < along < 1 and abs(dx * ey - dy * ex) < 18:
            change -= 17
            break
    return tint(ROCK, change)


def sand_texture(x, y):
    return tint((0xd4, 0xbe, 0x8c), grain(x // 4, y // 4, 32, 0x519) // 2
                + grain(x * 4, y * 4, 512, 0x520) // 2)


def sandstone_texture(x, y, side=False):
    bedding = round(4 * sin(2 * pi * y / 32 + .3 * sin(2 * pi * x / 128))) if side else 0
    return tint((0xb8, 0x8c, 0x60), grain(x, y, 128, 0x521) + bedding)


def gravel_texture(x, y, color=(0x8d, 0x88, 0x80)):
    stones = []
    for row in range(y // 8 - 1, y // 8 + 2):
        for column in range(x // 8 - 1, x // 8 + 2):
            key = (column % 16) * 73856093 ^ (row % 16) * 19349663
            key = (key ^ (key >> 13)) * 1274126177
            key ^= key >> 16
            cx, cy = column * 8 + key % 8, row * 8 + (key // 11) % 8
            stones.append(((x - cx) ** 2 + (y - cy) ** 2, (key // 31) % 17 - 8))
    stones.sort()
    shade = -20 if stones[1][0] - stones[0][0] < 8 else stones[0][1]
    return tint(color, shade + grain(x, y, 128, 0x522) // 2)


def concrete_texture(x, y):
    seam = -3 if x in (0, 127) else 0
    return tint(CONCRETE, grain(x, y, 128, 0x523) // 2 + seam)


def bark_texture(x, y):
    grooves = round(6 * cos(2 * pi * x / 8 + .3 * sin(2 * pi * y / 32)))
    return tint((0x5b, 0x46, 0x32), grooves + grain(x, y, 32, 0x524) // 2)


def foliage_texture(x, y):
    return tint((0x46, 0x66, 0x3a), 2 * grain(x, y, 64, 0x525))


def street_lamp_panel(x, y):
    return 5 <= x < 27 and 5 <= y < 27


def street_lamp_texture(x, y):
    return (0xe1, 0xd4, 0xac, 0) if street_lamp_panel(x, y) else tint(GRAPHITE)


def site_fence_texture(x, y):
    return tint(SITE_FENCE, -8 if x % 16 in (0, 1) else grain(x, y, 32, 0x526))


def road_barrier_stripe(x, y):
    return (x + y) // 8 % 2 != 0


def road_barrier_texture(x, y):
    return GRAPHITE + (0,) if road_barrier_stripe(x, y) else tint(YELLOW)


def rail_stack_texture(x, y):
    return tint(STEEL if x % 8 in (2, 3) else (0x5e, 0x44, 0x34))


def mineral_texture(x, y, color):
    # 不规则取样点在 64 px 环面上分块，边界跨贴图连续；块面色差没有光照方向。
    distances = sorted(((((x - cx + 32) % 64 - 32) ** 2 + ((y - cy + 32) % 64 - 32) ** 2) ** .5, shade)
                       for cx, cy, shade in MINERAL_FRAGMENTS)
    change = -27 if distances[1][0] - distances[0][0] < 1.1 else distances[0][1]
    return tint(color, change + grain(x, y, 64, 0x601) // 2)


def coal_texture(x, y):
    return mineral_texture(x, y, (50, 54, 58))


def ferrous_ore_texture(x, y):
    color = (147, 87, 53) if grain(x, y, 64, 0x604) > 1 else (120, 65, 46)
    return mineral_texture(x, y, color)


def conductive_ore_texture(x, y):
    color = (90, 125, 108) if grain(x, y, 64, 0x605) > 0 else (137, 94, 66)
    return mineral_texture(x, y, color)


def rare_mineral_texture(x, y):
    color = (137, 130, 146) if grain(x, y, 64, 0x606) > 3 else (107, 96, 119)
    return mineral_texture(x, y, color)


def stone_texture(x, y):
    return mineral_texture(x, y, (129, 131, 127))


def silica_texture(x, y):
    particles = (x * 17 ^ y * 37 ^ (x // 2) * (y // 2) * 13) % 9 - 4
    return tint((205, 191, 155), particles + grain(x, y, 64, 0x606))


def crude_oil_texture(x, y, side=False):
    if side:
        if x in (0, 31):
            return tint((31, 38, 44))
        band = y % 16 in (3, 4, 5)
        return tint(CRUDE_OIL, 15 if band else grain(x, y, 32, 0x607) // 2)
    radius = ((x - 15.5) ** 2 + (y - 15.5) ** 2) ** .5
    return tint(CRUDE_OIL, 16 if 12 <= radius < 14 or radius < 2 else -13 if radius >= 14 else 0)


def grain_texture(x, y, side=False):
    if not side:
        x, y = min(x, 31 - x), min(y, 31 - y)
    weave = 3 if (x + y) % 2 == 0 else -2
    variation = grain(min(x, y), max(x, y), 32, 0x602) // 2
    if side:
        body = ((x - 15.5) / 12) ** 2 + ((y - 19) / 12) ** 2
        mouth = 1 <= y <= 5 and abs(x - 15.5) <= 6 - y / 2
        tie = 6 <= y <= 8 and 11 <= x <= 20 or 8 <= y <= 12 and x == 29 - y
        if tie:
            return tint((105, 82, 47), variation)
        if mouth:
            return tint((168, 143, 99), weave + variation - (12 if x in (13, 17) else 0))
        if y >= 9 and body < 1:
            seam = body > .8 and (x < 8 or x > 23 or y > 26)
            stitch = 11 <= y <= 26 and x == 23 and y % 3 != 0
            return tint((168, 143, 99), weave + variation - (13 if seam else 22 if stitch else 0))
        return tint((91, 74, 48), grain(x, y, 32, 0x603) // 2)
    return tint((168, 143, 99), weave + variation)


def crate_side_texture(x, y, wood, contents):
    if 3 <= x <= 28 and y % 8 < 3:
        return tint(contents, grain(x, y, 32, 0x608) // 2)
    return tint(wood, -10 if x in (2, 29) else grain(x, y, 32, 0x609))


def produce_texture(x, y, side=False):
    if side:
        return crate_side_texture(x, y, (128, 98, 63), (66, 93, 49))
    if x < 2 or x > 29 or y < 2 or y > 29:
        return tint((128, 98, 63), grain(x, y, 32, 0x610))
    for cx, cy, radius, tone in ((7, 8, 5, 8), (20, 6, 4, -5), (25, 18, 5, 3),
                                (14, 16, 6, -10), (7, 25, 4, 6), (21, 26, 4, -3)):
        if (x - cx) ** 2 + (y - cy) ** 2 < radius ** 2:
            return tint((79, 112, 53), tone + grain(x, y, 32, 0x611))
    root = (((x - 7) / 2) ** 2 + ((y - 16) / 4) ** 2 < 1
            or ((x - 23) / 4) ** 2 + ((y - 12) / 2) ** 2 < 1)
    return tint((179, 110, 47) if root else (56, 79, 41), grain(x, y, 32, 0x611))


def animal_products_texture(x, y, side=False):
    if side:
        return crate_side_texture(x, y, (164, 137, 94), (198, 181, 132))
    a, b = sorted((min(x, 31 - x), min(y, 31 - y)))
    if a < 2:
        return tint((164, 137, 94), grain(a, b, 32, 0x612))
    egg = ((a - 8) / 5) ** 2 + ((b - 9) / 6) ** 2 < 1
    return tint((231, 222, 195) if egg else (165, 143, 90), grain(a, b, 32, 0x613) // 2)


def crate_bottom_texture(x, y):
    a, b = sorted((min(x, 31 - x), min(y, 31 - y)))
    weave = 5 if (a // 3 + b // 3) % 2 == 0 else -5
    return tint((132, 105, 69), weave + grain(a, b, 32, 0x614) // 2)


def timber_texture(x, y, side=False):
    if side:
        furrow = min(abs((x - center + 16) % 32 - 16) for center in (3, 11, 18, 27))
        change = -23 if furrow < 1.2 else grain(x, y, 32, 0x615)
        return tint((110, 78, 48), change)
    a, b = sorted((min(x, 31 - x), min(y, 31 - y)))
    radius = ((a - 15.5) ** 2 + (b - 15.5) ** 2) ** .5
    if radius > 14:
        return tint((110, 78, 48))
    return tint((178, 142, 91), -18 if int(radius) % 4 == 0 else grain(a, b, 32, 0x616) // 2)


def natural_fiber_texture(x, y, side=False):
    if side:
        strap = 14 <= x <= 17
    else:
        x, y = min(x, 31 - x), min(y, 31 - y)
        strap = x >= 14 or y >= 14
    return tint((67, 77, 76) if strap else (226, 218, 192), grain(min(x, y), max(x, y), 32, 0x617) // 2)


def conductive_metal_texture(x, y, side=False):
    a, b = min(x, 31 - x), min(y, 31 - y)
    if side:
        variation = -9 if a == 0 else -3 if a % 8 == 1 else grain(a, y, 32, 0x618) // 3
    else:
        a, b = sorted((a, b))
        variation = -9 if a == 0 else grain(a, b, 32, 0x619) // 2
    return tint((166, 105, 65), variation)


def fuel_texture(x, y, side=False):
    return barrel_texture(x, y, side, RED, ((12, 19),))


def chemical_feedstock_texture(x, y, side=False):
    return barrel_texture(x, y, side, BLUE, ((10, 12), (19, 21)))


def barrel_texture(x, y, side, color, bands):
    if side and any(start <= y <= end for start, end in bands):
        return (0xd9, 0xd4, 0xc7, 0)
    return tint(color, crude_oil_texture(x, y, side)[0] - CRUDE_OIL[0])


def mechanical_components_texture(x, y, side=False):
    a, b = min(x, 31 - x), min(y, 31 - y)
    if side:
        vent = 5 <= x <= 26 and 10 <= y <= 21 and y % 4 in (1, 2)
    else:
        vent = 5 <= x <= 26 and 5 <= y <= 26 and x % 8 in (3, 4) and y % 8 in (3, 4)
    if vent:
        return GRAPHITE + (0,)
    if a == 4 and b == 4:
        return STEEL + (0,)
    if not side:
        a, b = sorted((a, b))
    variation = -9 if a == 0 or b == 0 else grain(a, b, 32, 0x621) // 3
    return tint(EQUIPMENT_FACE, variation)


def construction_material_texture(x, y):
    a, b = sorted((min(x, 127 - x), min(y, 127 - y)))
    variation = grain(a, b, 128, 0x622) + grain(a // 4, b // 4, 32, 0x623) // 2
    aggregate = -2 if (a * 17 ^ b * 29) % 53 == 0 else 0
    return tint(CONCRETE, variation + aggregate)


def structural_metal_texture(x, y, side=False):
    return plate_texture(x, y) if side else tread_texture(x, y)


def specialty_material_texture(x, y, side=False):
    a, b = min(x, 31 - x), min(y, 31 - y)
    if not side:
        a, b = sorted((a, b))
    return tint((118, 113, 132), -9 if a == 0 else grain(a, b, 32, 0x624) // 3)


def glass_texture(x, y):
    a, b = sorted((min(x, 31 - x), min(y, 31 - y)))
    return tint(GLASS, 5 if a == 0 else grain(a, b, 32, 0x625) // 4)


def lumber_texture(x, y, side=False):
    if side:
        return tint((151, 112, 65), -13 if x % 16 in (0, 15) else grain(x, y // 4, 32, 0x626))
    a, b = abs(x % 16 - 7.5), abs(y % 16 - 7.5)
    ring = int((a * a + b * b) ** .5) % 4 == 0
    return tint((176, 140, 89), -15 if ring or max(a, b) == 7.5 else 0)


def textile_texture(x, y, side=False):
    if side:
        return tint((158, 139, 111), -16 if x in (0, 31) else 4 if (x + y) % 2 else -3)
    radius = int(((x - 15.5) ** 2 + (y - 15.5) ** 2) ** .5)
    return tint((174, 153, 122), -18 if radius % 4 == 0 or radius < 3 else 0)


def polymer_texture(x, y, side=False):
    a, b = min(x, 31 - x), min(y, 31 - y)
    if not side:
        a, b = sorted((a, b))
    seam = a == 0 or (b == 0 if not side else y in (2, 29))
    pellet = a % 6 in (2, 3) and b % 6 in (2, 3)
    return tint((192, 187, 167), -24 if seam else -9 if pellet else 0)


def electrical_components_texture(x, y, side=False):
    a, b = min(x, 31 - x), min(y, 31 - y)
    terminal = 6 <= a <= 9 and (10 <= y <= 21 if side else 6 <= b <= 9)
    if terminal:
        return (166, 105, 65, 0)
    insulator = a in (5, 10) and 9 <= y <= 22 if side else (
        a in (5, 10) and 5 <= b <= 10 or b in (5, 10) and 5 <= a <= 10)
    if insulator:
        return GRAPHITE + (0,)
    if not side:
        a, b = sorted((a, b))
    return tint(EQUIPMENT_FACE, -10 if a == 0 or b == 0 or a == 3 or b == 3 else 0)


def electronic_components_texture(x, y, side=False):
    a, b = min(x, 31 - x), min(y, 31 - y)
    if side and 6 <= x <= 25 and 12 <= y <= 19:
        return ((71, 91, 69) if x % 6 > 1 else GRAPHITE) + (0,)
    if not side and 5 <= a <= 12 and 5 <= b <= 12 and a % 4 in (1, 2) and b % 4 in (1, 2):
        return GRAPHITE + (0,)
    if not side:
        a, b = sorted((a, b))
    return tint(EQUIPMENT_FACE, -10 if a == 0 or b == 0 else 0)


def structural_block_texture(x, y):
    a, b = sorted((min(x, 31 - x), min(y, 31 - y)))
    return tint(STEEL, -15 if a == 0 else -6 if a == b == 3 else grain(a, b, 32, 0x627) // 3)


def construction_block_texture(x, y):
    a, b = sorted((min(x, 31 - x), min(y, 31 - y)))
    return tint(CONCRETE, -22 if a == 0 else grain(a, b, 32, 0x628) // 2)


def rail_texture(x, y, side=False):
    if side:
        return tint(STEEL, -31 if x % 16 in (5, 6, 9, 10) else -10 if x % 16 in (7, 8) else 0)
    while x >= 16 or y >= 16:
        x, y = 31 - y, x
    head = 4 <= x <= 11 and 2 <= y <= 5 and (y > 2 or 5 <= x <= 10)
    web = 7 <= x <= 8 and 6 <= y <= 10
    foot = (2 <= x <= 13 and 12 <= y <= 13 or
            4 <= x <= 11 and y == 11 or 6 <= x <= 9 and y == 10)
    return tint(STEEL, 10 if head or web or foot else -32)


def staple_food_texture(x, y, side=False):
    a, b = min(x, 31 - x), min(y, 31 - y)
    seam = y % 16 in (0, 15) if side else a == b or a == 0 or b == 0
    band = side and 12 <= y <= 19
    return tint((118, 82, 95), -16 if seam else 12 if band else grain(min(a, b), max(a, b), 32, 0x631) // 3)


def prepared_food_texture(x, y, side=False):
    if side:
        label = 9 <= y <= 22
        rim = y in (1, 2, 29, 30) or x in (0, 31)
        return tint((181, 124, 80) if label else (164, 170, 162), -17 if rim else 0)
    radius = int(((x - 15.5) ** 2 + (y - 15.5) ** 2) ** .5)
    return tint((164, 170, 162), -18 if radius in (6, 12, 14) else 0)


def clothing_texture(x, y, side=False):
    a, b = sorted((min(x, 31 - x), min(y, 31 - y)))
    fold = y % 8 in (0, 1) if side else a in (0, 5)
    return tint((83, 98, 139), -17 if fold else 3 if (a + b) % 2 else -3)


def consumer_goods_texture(x, y, side=False):
    a, b = sorted((min(x, 31 - x), min(y, 31 - y)))
    if side:
        handle = 7 <= x <= 24 and 12 <= y <= 16
        seam = x in (0, 3, 28, 31) or y in (0, 31)
    else:
        handle = False
        seam = a % 8 == 0 or b % 8 == 0
    return tint((163, 152, 178), -42 if handle else -15 if seam else 0)


def consumer_electronics_texture(x, y, side=False):
    a, b = sorted((min(x, 31 - x), min(y, 31 - y)))
    if side:
        recess = 9 <= x <= 22 and 12 <= y <= 18
        seam = y in (0, 7, 24, 31) or x in (0, 31)
    else:
        recess = b < 6
        seam = a in (0, 4)
    return tint((52, 61, 70), -16 if recess else -9 if seam else 0)


def medical_supplies_texture(x, y, side=False):
    a, b = sorted((min(x, 31 - x), min(y, 31 - y)))
    mark = 14 <= y <= 17 if side else a >= 13 and b >= 7 or b >= 13 and a >= 7
    if mark:
        return tint((61, 119, 113))
    seam = y in (0, 31) or 10 <= x <= 21 and 7 <= y <= 10 if side else a == 0
    return tint((197, 214, 212), -24 if seam else 0)


GOODS_ART = {
    "ferrous-ore": ("stone_soil", 2, None, None, ferrous_ore_texture),
    "conductive-ore": ("stone_soil", 2, None, None, conductive_ore_texture),
    "rare-mineral": ("stone_soil", 2, None, None, rare_mineral_texture),
    "stone": ("stone_soil", 2, None, None, stone_texture),
    "silica": ("stone_soil", 2, None, None, silica_texture),
    "coal": ("stone_soil", 2, None, None, coal_texture),
    "crude-oil": ("painted_metal", 1, "side.png", None, crude_oil_texture),
    "grain": ("organic", 1, "side.png", None, grain_texture),
    "produce": ("organic", 1, "side.png", "shared/crate-bottom.png", produce_texture),
    "animal-products": ("organic", 1, "side.png", "shared/crate-bottom.png", animal_products_texture),
    "timber": ("organic", 1, "side.png", None, timber_texture),
    "natural-fiber": ("organic", 1, "side.png", None, natural_fiber_texture),
    "conductive-metal": ("bare_metal", 1, "side.png", None, conductive_metal_texture),
    "fuel": ("painted_metal", 1, "side.png", None, fuel_texture),
    "mechanical-components": ("painted_metal", 1, "side.png", "shared/equipment-underside.png", mechanical_components_texture),
    "construction-material": ("concrete", 4, None, None, construction_material_texture),
    "structural-metal": ("painted_metal", 4, "side.png", "shared/vehicle-underside.png", structural_metal_texture),
    "specialty-material": ("bare_metal", 1, "side.png", None, specialty_material_texture),
    "glass": ("glass", 1, None, None, glass_texture),
    "lumber": ("organic", 1, "side.png", None, lumber_texture),
    "textile": ("organic", 1, "side.png", None, textile_texture),
    "chemical-feedstock": ("painted_metal", 1, "side.png", None, chemical_feedstock_texture),
    "polymer": ("polymer_rubber", 1, "side.png", None, polymer_texture),
    "electrical-components": ("painted_metal", 1, "side.png", "shared/equipment-underside.png", electrical_components_texture),
    "electronic-components": ("painted_metal", 1, "side.png", "shared/equipment-underside.png", electronic_components_texture),
    "structural-block": ("painted_metal", 1, None, None, structural_block_texture),
    "construction-block": ("concrete", 1, None, None, construction_block_texture),
    "rail": ("bare_metal", 1, "side.png", None, rail_texture),
    "staple-food": ("organic", 1, "side.png", None, staple_food_texture),
    "prepared-food": ("bare_metal", 1, "side.png", None, prepared_food_texture),
    "clothing": ("organic", 1, "side.png", None, clothing_texture),
    "consumer-goods": ("polymer_rubber", 1, "side.png", None, consumer_goods_texture),
    "consumer-electronics": ("polymer_rubber", 1, "side.png", None, consumer_electronics_texture),
    "medical-supplies": ("polymer_rubber", 1, "side.png", None, medical_supplies_texture),
}
# 表中货物的 icon.png 是唯一源，生成器只生成 Keep File 导入声明。
HAND_DRAWN_ICONS = tuple(f"voxels/{identity}/icon.png" for identity in GOODS_ART)
# 相对 resources/ 的精确路径；蓝图由创作模式维护，模型由 Blender 源导出，生成器只维护清单和导入声明。
AUTHORED_FILES = (
    "blueprints/large-locomotive.blueprint.json",
    "blueprints/titan-armed-vehicle.blueprint.json",
    "blueprints/heavy-vehicle-factory.blueprint.json",
    "blueprints/assembly-factory.blueprint.json",
    "blueprints/basic-shed.blueprint.json",
    "blueprints/bastion-fortress.blueprint.json",
    "blueprints/building-materials-factory.blueprint.json",
    "blueprints/chemical-factory.blueprint.json",
    "blueprints/core-city-hub.blueprint.json",
    "blueprints/electronics-factory.blueprint.json",
    "blueprints/extraction-site.blueprint.json",
    "blueprints/farm.blueprint.json",
    "blueprints/food-factory.blueprint.json",
    "blueprints/large-station.blueprint.json",
    "blueprints/large-warehouse.blueprint.json",
    "blueprints/light-industry-factory.blueprint.json",
    "blueprints/metallurgy-factory.blueprint.json",
    "blueprints/power-station.blueprint.json",
    "blueprints/recovery-factory.blueprint.json",
    "blueprints/small-station.blueprint.json",
    "blueprints/small-warehouse.blueprint.json",
    "blueprints/vehicle-workshop.blueprint.json",
    "voxels/machine-gun/model.glb",
    "voxels/cannon/model.glb",
    "voxels/bogie/model.glb",
)


def plate_texture(x, y):
    seam = x % 128 in (0, 64)
    rivet = x % 64 == 3 and y % 16 in (4, 5)
    return tint(BLUE, -10 if seam else -6 if rivet else grain(x, y, 128, 0x507))


def tread_texture(x, y):
    return tint(BLUE, 6 if (x + y) % 16 in (2, 3) or (x - y) % 16 in (2, 3) else grain(x, y, 128, 0x508))


def underside_texture(x, y):
    return tint((0x2b, 0x2e, 0x31))


def engine_texture(x, y):
    return tint(BLUE, -22 if 4 <= x < 28 and y % 8 in (2, 3) else grain(x, y, 32, 0x511))


def control_texture(x, y):
    window = 3 <= x < 29
    color = tint(GLASS if window else BLUE, grain(x, y, 32, 0x512))
    return color[:3] + (0,) if window else color


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

    if identity == "bogie":
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
    for name, size in (("vehicle-underside", 128), ("equipment-underside", 32)):
        texture = lambda x, y: underside_texture(x, y)[:3] + (0,)
        files[f"voxels/shared/{name}.png"] = png(size, texture)
    for identity, size, top, side in (
        ("propulsion-equipment", 32, grille_texture, engine_texture),
        ("control-equipment", 32, equipment_roof_texture, control_texture),
        ("storage-equipment", 32, storage_roof_texture, storage_texture),
    ):
        top = neutral_texture(top, DEFAULT_PAINTS[identity])
        side = neutral_texture(side, DEFAULT_PAINTS[identity])
        files[f"voxels/{identity}/top.png"] = png(size, top)
        if side is not None:
            files[f"voxels/{identity}/side.png"] = png(size, side)
    for identity in ("bogie", "propulsion-equipment",
                     "control-equipment", "storage-equipment"):
        files[f"voxels/{identity}/icon.png"] = icon(identity)


def scene_assets(files, voxel):
    for identity, name, material, span, top, side in (
        ("ground", "草地", "organic", 4, grass_texture, roots_texture),
        ("dry-grass", "干草地", "organic", 4, lambda x, y: grass_texture(x, y, DRY_GRASS),
         lambda x, y: roots_texture(x, y, DRY_GRASS)),
        ("forest-ground", "林地", "organic", 4, forest_ground_texture,
         lambda x, y: roots_texture(x, y, FOREST)),
        ("subsoil", "土", "stone_soil", 4, soil_texture, None),
        ("rock", "灰岩", "stone_soil", 4, rock_texture, None),
        ("sand", "沙", "stone_soil", 4, sand_texture, None),
        ("sandstone", "砂岩", "stone_soil", 4, sandstone_texture,
         lambda x, y: sandstone_texture(x, y, side=True)),
        ("gravel", "碎石", "stone_soil", 4, gravel_texture, None),
        ("ballast", "道床碎石", "stone_soil", 2,
         lambda x, y: gravel_texture(x * 2, y * 2, (0x8a, 0x87, 0x82)), None),
        ("concrete", "混凝土", "concrete", 4, concrete_texture, None),
        ("tree-trunk", "树皮", "organic", 1, bark_texture, None),
        ("foliage", "树叶", "organic", 2, foliage_texture, None),
        ("street-lamp", "路灯", "painted_metal", 1, street_lamp_texture, None),
        ("site-fence", "工地围挡", "painted_metal", 1, site_fence_texture, None),
        ("road-barrier", "施工路障", "painted_metal", 1, road_barrier_texture, None),
        ("rail-stack", "钢轨堆", "bare_metal", 1, rail_stack_texture, None),
    ):
        top_path = "shared/subsoil.png" if identity == "subsoil" else "top.png"
        texture_path = f"voxels/{top_path}" if identity == "subsoil" else f"voxels/{identity}/{top_path}"
        icon_path = top_path if span == 4 else f"shared/{'organic' if material == 'organic' else 'equipment'}-icon.png"
        if material in PAINTABLE_MATERIALS:
            if icon_path == top_path:
                files[f"voxels/{identity}/icon.png"] = png(32 * span, top)
                icon_path = "icon.png"
            top = neutral_texture(top, DEFAULT_PAINTS[identity])
            if side is not None:
                side = neutral_texture(side, DEFAULT_PAINTS[identity])
        files[texture_path] = png(32 * span, top)
        if side is not None:
            files[f"voxels/{identity}/side.png"] = png(32 * span, side)
        bottom = "shared/subsoil.png" if identity in ("ground", "dry-grass", "forest-ground") else top_path
        voxel(identity, name, material, faces(top_path, "side.png" if side else top_path, bottom), icon_path,
              tile_span=span, light=identity == "street-lamp", description=f"用于自然环境和铁路场景的{name}。")


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


def natural_models():
    for identity, name, height, radius, crown_height, trunk_width in (
        ("tree-small", "小树", 6, 3, 2, 1),
        ("tree-medium", "中树", 10, 4, 3, 2),
        ("tree-large", "大树", 14, 5, 4, 2),
    ):
        cells = {(x, y, z): 1 for x in range(-radius, radius + 1)
                 for z in range(-radius, radius + 1) for y in range(height - crown_height, height + crown_height + 1)
                 if (x * x + z * z) / (radius * radius) + (y - height) ** 2 / (crown_height * crown_height)
                 <= 1 + ((x * 37 ^ y * 71 ^ z * 97) % 7 - 3) * .04}
        for x in range(-(trunk_width // 2), trunk_width - trunk_width // 2):
            for z in range(-(trunk_width // 2), trunk_width - trunk_width // 2):
                cells.update({(x, y, z): 0 for y in range(height + 1)})
        model = blueprint(identity, name, "scene_model", ["tree-trunk", "foliage"], cells)
        model["display"]["description"] = f"{name}，树干支撑成团树冠，供森林装饰使用。"
        yield model
    for identity, name, rx, ry, rz in (("rock-small", "小岩石", 4, 2, 3), ("rock-large", "大岩石", 7, 4, 5)):
        cells = {(x, y, z): 0 for x in range(-rx, rx + 1) for y in range(ry + 1) for z in range(-rz, rz + 1)
                 if x * x / (rx * rx) + y * y / (ry * ry) + z * z / (rz * rz)
                 <= 1 + ((x * 31 ^ y * 67 ^ z * 89) % 5 - 2) * .05}
        model = blueprint(identity, name, "scene_model", ["rock"], cells)
        model["display"]["description"] = f"{name}，由灰岩组成的自然露头。"
        yield model


def railway_models():
    for tracks in (1, 2, 3):
        half_width = 10 + 17 * (tracks - 1)
        centers = [34 * track - 17 * (tracks - 1) for track in range(tracks)]
        # 112 格跨过现有最宽的 96 格湖面，两端各留 8 格搭岸。
        bridge = {(x, y, z): 0 for x in range(-half_width, half_width + 1)
                  for y in (-4, -3) for z in range(-56, 56)}
        for center in centers:
            for y, width in ((-3, 10), (-2, 9)):
                bridge.update({(x, y, z): 1 for x in range(center - width, center + width + 1) for z in range(-56, 56)})
            for pier in range(-48, 49, 16):
                bridge.update({(x, y, z): 0 for x in range(center - 2, center + 3)
                               for y in range(-16, -4) for z in range(pier - 1, pier + 2)})
        for x in (-half_width, half_width):
            bridge.update({(x, -2, z): 0 for z in range(-56, 56)})
        model = blueprint(f"bridge-{tracks}", f"{tracks}轨桥梁", "scene_model", ["concrete", "ballast"], bridge)
        model["display"]["description"] = "混凝土梁桥，道床直接铺在桥面上，桥墩间隔 16 格，矮护栏低于轨面。"
        yield model
        half_opening = 17 * tracks
        portal = {(x, y, z): 0 for x in range(-half_opening - 2, half_opening + 2)
                  for y in range(44) for z in (0, 1)
                  if x < -half_opening or x >= half_opening or y >= 42}
        portal.update({(x, y, 1): 1 for x in range(-half_opening, half_opening) for y in range(42)})
        model = blueprint(f"tunnel-portal-{tracks}", f"{tracks}轨隧道口", "scene_model", ["concrete"], portal)
        model["layout"]["palette"].append({"voxel": "concrete", "paint": "#000000"})
        model["display"]["description"] = "混凝土矩形洞门，每轨留 34 格宽、42 格高的开口；黑色内壁遮住洞内。"
        yield model


def construction_models():
    site = {(x, y, z): 0 for x in range(-12, 13) for z in range(-9, 10) for y in range(3)
            if (abs(x) == 12 or abs(z) == 9) and not (z == -9 and abs(x) < 3)}
    site.update({(x, y, z): 1 for x in range(-8, -3) for y in range(2) for z in range(-4, 3)})
    site.update({(x, y, z): 2 for x in range(4, 9) for y in range(2) for z in range(-5, 2)})
    model = blueprint("construction-site", "静态工地", "scene_model", ["site-fence", "concrete", "rail-stack"], site)
    model["display"]["description"] = "围挡留出入口，内部堆放混凝土与钢轨材料，不搭脚手架。"
    yield model
    repair = {(x, y, z): 0 for x in range(-8, 9) for y in range(2) for z in (-9, 9)}
    # 以断轨为原点：三轨外侧断点的最远邻轨距 68 格，再留 16 格泰坦净空。
    repair.update({(x, y, z): 1 for x in range(88, 93) for y in range(2) for z in range(-6, 3)})
    for z in (4, 7, 10):
        repair.update({(x, 0, z): 2 for x in range(88, 105)})
    for x, z in ((-88, -6), (88, 6)):
        repair.update({(x, y, z): 2 for y in range(8)})
        repair[x, 8, z] = 3
    model = blueprint("railway-repair-site", "断轨修复工地", "scene_model",
                      ["road-barrier", "rail-stack", "concrete", "street-lamp"], repair)
    model["display"]["description"] = "缺口两端的黄黑路障、新钢轨和混凝土轨枕堆，以及两盏工作灯；材料在开通铁轨净空外。"
    yield model


def vehicle(spec):
    identity, name, role, size, length, width, height, gauge, frame_height, axle_spacing, end_margin, units = spec
    x0, z0 = -(width // 2), -(length // 2)
    x1, z1 = x0 + width - 1, z0 + length - 1
    if size == "small" and role == "locomotive":
        volume = {(x, 1, z) for x in range(-3, 4) for z in range(-26, 26)}
        volume |= {(x, y, z) for x in range(-3, 4) for y in range(2, 9) for z in range(16, 24)}
        volume |= {(x, y, z) for x in range(-2, 3) for y in range(2, 8) for z in range(-24, 16)}
        directions = ((1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1))
        cells = {at: 0 for at in volume
                 if any(tuple(at[i] + delta[i] for i in range(3)) not in volume for delta in directions)}
    else:
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
    if size == "small" and role == "locomotive":
        cells.update({(0, 6, 23): 3, (2, 5, 5): 2, (2, 5, 1): 2, (2, 5, -3): 2, (-2, 3, -20): 4})
    else:
        if role == "locomotive":
            for z in range(-3 * units, 3 * units, 2):
                cells[0, frame_height + 1, z] = 2
            cells[0, frame_height + 1, z1 - 1] = 3
        storage_count = {"locomotive": 1, "freight": 2 * units, "armed": units}[role]
        for index in range(storage_count):
            cells[1, frame_height + 1, 2 * index] = 4
        if role == "armed":
            for index in range(units):
                cells[x0, frame_height + 2, 4 * index] = 5
    value = blueprint(identity, name, "vehicle",
                      ["structural-metal", "bogie", "propulsion-equipment", "control-equipment", "storage-equipment"],
                      cells, {"size_class": size})
    if role == "armed":
        value["layout"]["palette"].append({"voxel": "machine-gun"})
    value["display"]["description"] = f"{name}：能力和价值来自实际部件，数值与外观待试玩确认。"
    if size == "small" and role == "locomotive":
        value["display"]["description"] = "第一阶段铁路机车风格样稿：驾驶室、机器间、检修走廊和端部平台；画面待用户阶段试玩确认。"
    if role == "armed":
        value["display"]["description"] += " 静态武器只提供合法设计输入，战斗行为待战斗阶段验证。"
    return value


def fill_box(cells, origin, size, index):
    x0, y0, z0 = origin
    width, height, depth = size
    cells.update({(x, y, z): index for x in range(x0, x0 + width) for y in range(y0, y0 + height)
                  for z in range(z0, z0 + depth)})


def large_diesel_engine(propulsion):
    cells = {(x, y, z): 0 for x in range(-3, 4) for y in range(8) for z in range(-10, 10)
             if x in (-3, 3) or y in (0, 7) or z in (-10, 9)}
    for x in (-1, 1):
        fill_box(cells, (x, 2, -8), (1, 4, 16), 1)
    fill_box(cells, (0, 3, -9), (1, 1, 18), 1)
    value = blueprint("large-diesel-engine", "大型柴油机", "large_equipment",
                      ["structural-metal", "mechanical-components"], cells,
                      {"behaviors": {"propulsion": {"traction": 9 * propulsion["traction"],
                                                    "rated_speed": propulsion["rated_speed"]}}})
    value["layout"]["palette"].extend([
        {"voxel": "structural-metal", "paint": "#33373b"},
        {"voxel": "structural-metal", "paint": "#626750"},
        {"voxel": "mechanical-components", "paint": "#7e858b"},
        {"voxel": "structural-metal", "paint": "#d9a62b"},
    ])
    for cell in value["layout"]["cells"]:
        x, y, z, _ = cell
        if y == 0:
            cell[3] = 2
        elif x in (-3, 3) and 1 <= y <= 6:
            cell[3] = 3
        elif -1 <= x <= 1 and 3 <= y <= 5 and -8 <= z <= 7:
            cell[3] = 4
        elif y == 7 and -1 <= z <= 0:
            cell[3] = 5
    return value


def heavy_turret(cannon):
    cells = {(x, y, z): 0 for x in range(-3, 4) for y in range(8) for z in range(-3, 4)
             if x in (-3, 3) or y in (0, 7) or z in (-3, 3)}
    fill_box(cells, (-1, 3, 4), (3, 2, 2), 1)
    value = blueprint("heavy-turret", "重炮塔", "large_equipment", ["structural-metal", "mechanical-components"], cells,
                      {"behaviors": {"weapon": {"damage": ceil(cannon["damage"] * 6 / 4), "interval": cannon["interval"]}}},
                      {"behaviors": {"rotation": {"axis": "y", "pivot": [0, 0, 0], "range": [-180, 180], "speed": 90}}})
    value["display"]["description"] = "重炮塔正式设计：整圈转动仅属表现，战斗行为待战斗阶段验证；武器数值待试玩确认。"
    for entry in value["layout"]["palette"]:
        entry["paint"] = "#33373b"
    value["layout"]["palette"].extend([
        {"voxel": "structural-metal", "paint": "#626750"},
        {"voxel": "structural-metal", "paint": "#7e858b"},
    ])
    for cell in value["layout"]["cells"]:
        if cell[3] == 0 and cell[1] > 0:
            cell[3] = 3 if cell[1] == 7 else 2
    return value


def gantry_crane():
    cells = {}
    for x in (-21, 19):
        fill_box(cells, (x, 0, -72), (3, 2, 144), 0)
        for z in (-65, 62):
            fill_box(cells, (x, 2, z), (3, 42, 3), 0)
    for z in (-65, 62, -1):
        fill_box(cells, (-21, 44, z), (43, 2, 3), 0)
    for x in (-20, 20):
        fill_box(cells, (x, 44, -64), (1, 2, 128), 0)
    fill_box(cells, (-2, 44, -1), (5, 4, 3), 1)
    fill_box(cells, (0, 42, 0), (1, 2, 1), 1)
    value = blueprint("gantry-crane", "龙门吊", "large_equipment", ["structural-metal", "mechanical-components"], cells,
                      {"behaviors": {"construction": {"work_rate": 4, "max_plot_size": "giant", "max_size_class": "titan"}}})
    value["layout"]["palette"].extend([
        {"voxel": "structural-metal", "paint": "#33373b"},
        {"voxel": "structural-metal", "paint": "#626750"},
        {"voxel": "structural-metal", "paint": "#d9a62b"},
        {"voxel": "mechanical-components", "paint": "#7e858b"},
        {"voxel": "mechanical-components", "paint": "#d9a62b"},
    ])
    for cell in value["layout"]["cells"]:
        x, y, z, index = cell
        if y <= 1:
            cell[3] = 2
        elif 2 <= y <= 43 and abs(x) >= 19:
            cell[3] = 3
        elif 44 <= y <= 45 and -1 <= z <= 1:
            cell[3] = 4 if index == 0 else 6
        elif 46 <= y <= 47:
            cell[3] = 5
    return value


def content_files():
    files = {}
    processes = process_definitions()
    values = good_values(processes)
    voxel_definitions = {}
    blueprint_definitions = {}
    template_definitions = {}

    def paint_design(design):
        for entry in design["layout"].get("palette", []):
            identity = entry["voxel"].removeprefix("core:")
            presentation = voxel_definitions[identity]["appearance"]["presentation"]
            materials = presentation.get("surface_materials", [presentation.get("material")])
            if any(material in PAINTABLE_MATERIALS for material in materials) and "paint" not in entry:
                entry["paint"] = "#" + bytes(DEFAULT_PAINTS[identity]).hex()
        for composite in design["layout"].get("composites", []):
            if "design" in composite:
                paint_design(composite["design"])

    def document(directory, kind, identity, value):
        if kind == "blueprint":
            paint_design(value)
        data = (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        if len(data) > JSON_LIMIT:
            raise ValueError(f"{identity}: {len(data)} bytes exceeds {JSON_LIMIT}")
        files[f"{directory}/{identity}.{kind}.json"] = data
        if kind == "blueprint":
            blueprint_definitions[identity] = value
        elif kind == "settlement-template":
            template_definitions[identity] = value

    colors = {"soil": SOIL_FACE + (255,),
              "organic": (99, 143, 81, 255), "equipment": EQUIPMENT_FACE + (255,)}
    for material, color in colors.items():
        files[f"voxels/shared/{material}-face.png"] = png(32, lambda x, y: color)
        files[f"voxels/shared/{material}-icon.png"] = png(128, lambda x, y: color)
    files["voxels/shared/paintable-face.png"] = png(32, lambda x, y: (255, 255, 255, 255))
    for name in SETTLEMENT_SYMBOLS:
        files[f"settlement-types/{name}/symbol.png"] = symbol_png(name)

    def voxel(identity, name, material=None, face_paths=None, icon_path=None, simulation=None, wheelset=False, light=False,
              tile_span=None, description=None):
        if identity in GOODS_ART:
            material, tile_span, side, bottom, _ = GOODS_ART[identity]
            face_paths, icon_path = faces("top.png", side, bottom), "icon.png"
            description = raw_description.format(name)
        if material in PAINTABLE_MATERIALS:
            if identity not in DEFAULT_PAINTS:
                raise ValueError(f"{identity}: paintable voxel has no default paint")
            face_paths = {face: "shared/paintable-face.png" if path in (
                "shared/equipment-face.png", "shared/soil-face.png") else path for face, path in face_paths.items()}
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
        if identity in ("machine-gun", "cannon"):
            value["appearance"]["presentation"] = {
                "kind": "mesh", "model": "model.glb", "surface_materials": ["painted_metal", "bare_metal"]}
            value["display"]["description"] = f"{name}的第三阶段模型样稿；数值待配平，画面待用户阶段试玩确认。"
        if identity == "bogie":
            value["appearance"]["presentation"] = {
                "kind": "mesh", "model": "model.glb", "surface_materials": ["bare_metal"]}
            value["display"]["description"] = f"{name}的第三阶段模型样稿；数值待配平，画面待用户阶段试玩确认。"
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
    raw_description = "{}的正式美术；数值待配平，画面待用户阶段试玩确认。"
    voxel("structural-metal", "结构金属", simulation=good("structural-metal", 0.028, 260))
    voxel("construction-material", "建筑材料", simulation=good("construction-material", 0.046, 220))
    voxel("bogie", "转向架", "bare_metal", faces("shared/equipment-face.png"),
          "icon.png", good("bogie", 0.034, 240), wheelset=True)
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
        voxel(identity, name, simulation=good(identity, mass))
    voxel("stone", "石料", simulation=good("stone", 0.042))
    scene_assets(files, voxel)
    style_assets(files)
    for identity, (material, tile_span, side, _, texture) in GOODS_ART.items():
        size = 32 * tile_span
        top = neutral_texture(texture, DEFAULT_PAINTS[identity]) if material in PAINTABLE_MATERIALS else texture
        files[f"voxels/{identity}/top.png"] = png(size, top)
        if side is not None:
            side_texture = lambda x, y: texture(x, y, side=True)
            if material in PAINTABLE_MATERIALS:
                side_texture = neutral_texture(side_texture, DEFAULT_PAINTS[identity])
            files[f"voxels/{identity}/{side}"] = png(size, side_texture)
    files["voxels/shared/crate-bottom.png"] = png(32, crate_bottom_texture)

    for model in railway_models():
        document("blueprints", "blueprint", model["id"], model)
    for model in natural_models():
        document("blueprints", "blueprint", model["id"], model)
    for model in construction_models():
        document("blueprints", "blueprint", model["id"], model)
    for identity, name, surface, subsurface, rock, local_amplitude in (
        ("plains", "平原", "ground", "subsoil", "rock", 0.7),
        ("hills", "丘陵", "dry-grass", "subsoil", "rock", 18),
        ("forest", "森林", "forest-ground", "subsoil", "rock", 0.7),
        ("desert", "荒漠", "sand", "sand", "sandstone", 0.7),
        ("mountains", "山地", "gravel", "rock", "rock", 18),
    ):
        description = f"{name}地表、下层与岩层，以及铁路沿线的局部起伏。"
        decorations, density = [], 0
        if identity == "forest":
            decorations, density = [("tree-small", 2), ("tree-medium", 2), ("tree-large", 1)], .002
        elif identity in ("hills", "mountains"):
            decorations, density = [("rock-small", 3), ("rock-large", 1)], .0007
        document("terrain-types", "terrain-type", identity,
                 {"schema_version": 1, "id": identity, "display": {"name": name, "description": description},
                  "surface_voxel": surface, "subsurface_voxel": subsurface, "rock_voxel": rock,
                  "local_height_noise": [{"scale_meters": 140, "amplitude": local_amplitude}],
                  "decorations": [{"scene_model": model, "weight": weight} for model, weight in decorations],
                  "decoration_target_per_square_meter": density,
                  "railway_models": [{"tracks": n, "bridge": f"bridge-{n}", "tunnel_portal": f"tunnel-portal-{n}"} for n in (1, 2, 3)]})
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
    voxel("fuel", "燃料", simulation=fuel)

    for identity, name, physical_source, texture in NEW_GOODS:
        source = voxel_definitions[physical_source]["simulation"]
        simulation = good(identity, source["mass"], source["max_durability"])
        if identity in ("machine-gun", "cannon"):
            factor = CANNON_FACTOR if identity == "cannon" else 1
            simulation["behaviors"] = {"weapon": {"damage": ceil(source["max_durability"] * WEAPON_DAMAGE_SHARE) * factor,
                                                   "interval": MACHINE_GUN_INTERVAL_SECONDS * factor}}
        material = {"soil": "stone_soil", "organic": "organic", "equipment": "painted_metal"}[texture]
        face_paths, icon_path = faces(f"shared/{texture}-face.png"), f"shared/{texture}-icon.png"
        voxel(identity, name, material, face_paths, icon_path, simulation,
              light=identity == "lamp")

    document("blueprints", "blueprint", "large-diesel-engine",
             large_diesel_engine(voxel_definitions["propulsion-equipment"]["simulation"]["behaviors"]["propulsion"]))
    document("blueprints", "blueprint", "heavy-turret",
             heavy_turret(voxel_definitions["cannon"]["simulation"]["behaviors"]["weapon"]))

    # 输出补色需要货物材质，车辆须等武器等体素登记后再输出。
    for spec in VEHICLES:
        value = vehicle(spec)
        document("blueprints", "blueprint", value["id"], value)
    for path in AUTHORED_FILES:
        if path.startswith("blueprints/"):
            value = json.loads((ROOT / "builtin/core/resources" / path).read_text(encoding="utf-8"))
            blueprint_definitions[value["id"]] = value

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
    crane = {(x, y, 0): 0 if y < 7 else 1 for x in range(-8, 9) for y in range(8) if x in (-8, 8) or y == 7}
    crane_blueprint = blueprint("workshop-crane", "车间吊机", "large_equipment", ["structural-metal", "structural-metal"], crane,
                                {"behaviors": {"construction": {"work_rate": 4, "max_plot_size": "large", "max_size_class": "large"}}})
    crane_blueprint["layout"]["palette"][0]["paint"] = "#33373b"
    crane_blueprint["layout"]["palette"][1]["paint"] = "#d9a62b"
    document("blueprints", "blueprint", "workshop-crane", crane_blueprint)
    document("blueprints", "blueprint", "gantry-crane", gantry_crane())

    plot_sizes = {identity: definition["simulation"]["plot_size"]
                  for identity, definition in blueprint_definitions.items() if definition["category"] == "building"}

    settlements = (
        ("agricultural-town", "农业镇", ("plains", "hills"), ("farm", "food-factory")),
        ("mining-town", "矿业镇", ("mountains", "hills"),
         ("extraction-site", "metallurgy-factory", "building-materials-factory", "metallurgy-factory")),
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
                              "map_symbol": "symbol.png", "major": identity == "industrial-city"}})
        plots = [{"id": 1, "size": plot_sizes["small-station"], "at": [-104, 1, -176], "orientation": "px_py", "building": "small-station"},
                 {"id": 2, "size": plot_sizes["small-station"], "at": [104, 1, -176], "orientation": "px_py"}]
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
                  "display": {"name": name + "经济布局", "description":
                              ("十地块布局，地块 9 为第二座冶金厂并启用炼轨；含两个站点地块与真实生产计划；普通库存与现车由核心按生成配置初始化。"
                               if identity == "mining-town" else
                               "十地块布局，含两个站点地块与真实生产计划；普通库存与现车由核心按生成配置初始化。")}})

    city = json.loads(files["settlement-templates/industrial-city.settlement-template.json"])
    city.update(id="core-city", scene_model="core-city-ground")
    city["plots"].extend([
        {"id": 11, "size": plot_sizes["core-city-hub"], "at": [-224, 1, 800], "orientation": "px_py", "building": "core-city-hub"},
        {"id": 12, "size": plot_sizes["bastion-fortress"], "at": [224, 1, 800], "orientation": "px_py", "building": "bastion-fortress"},
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

    def blueprint_behaviors(blueprint_id, capability):
        definition = blueprint_definitions[blueprint_id.removeprefix("core:")]
        layout = definition["layout"]
        behaviors = [behavior for *_, index in layout["cells"]
                     if (behavior := voxel_definitions[layout["palette"][index]["voxel"].removeprefix("core:")]
                         .get("simulation", {}).get("behaviors", {}).get(capability)) is not None]
        if (behavior := definition.get("simulation", {}).get("behaviors", {}).get(capability)) is not None:
            behaviors.append(behavior)
        for composite in layout.get("composites", []):
            behaviors.extend(blueprint_behaviors(composite["blueprint"], capability))
        return behaviors

    per_generator_outputs, template_estimates = [], []
    for template in template_definitions.values():
        buildings_by_plot = {plot["id"]: plot["building"] for plot in template["plots"] if "building" in plot}
        plans = [(processes[plan["process"]], sum(behavior["work_rate"] for behavior in
                  blueprint_behaviors(buildings_by_plot[plan["plot"]], plan["capability"])))
                 for plan in template["production_plans"] if plan["enabled"]]
        demand = sum(rate * process["power_per_work"] for process, rate in plans)
        template_estimates.append((template, plans, demand))
        generator_count = sum(len(blueprint_behaviors(building, "power")) for building in buildings_by_plot.values())
        per_generator_outputs.append(ceil(demand * POWER_HEADROOM / generator_count))
    output = max(per_generator_outputs)
    power = voxel_definitions["power-equipment"]
    power["simulation"]["behaviors"]["power"]["output"] = output
    document("voxels", "voxel", "power-equipment", power)
    fuel["fuel_energy"] = output * FUEL_UNIT_SECONDS
    document("voxels", "voxel", "fuel", voxel_definitions["fuel"])

    needs = ("food", "clothing", "improved_diet", "consumer_goods", "consumer_electronics", "medical_supplies")
    settlement_types = {}
    for path, data in files.items():
        if path.endswith(".settlement-type.json"):
            value = json.loads(data)
            settlement_types[value["id"]] = value["simulation"]
    maximum_population = max(value["population_max"] for value in settlement_types.values())
    farm_rate = sum(behavior["work_rate"] for behavior in blueprint_behaviors("farm", "production"))
    grain = processes["grain-farming"]
    grain_per_hour = farm_rate * grain["outputs"][0]["quantity"] / grain["work_required"] * SECONDS_PER_HOUR
    need_units_per_person_hour = grain_per_hour * LOCAL_FOOD_SHARE / maximum_population

    # 建筑规模估算：计入生活、全部投入和产出的毛量，故意不抵扣重叠货物。
    # 这不是核心补货目标，也不模拟食物替代、共享容量分配或初始化。
    warehouse_budgets = []
    reserve_seconds = PRODUCTION_RESERVE_HOURS * SECONDS_PER_HOUR
    for template, plans, demand in template_estimates:
        residents = settlement_types[template["settlement_type"]]["population_max"]
        living = residents * need_units_per_person_hour * (
            2 * BASIC_RESERVE_HOURS + (len(needs) - 2) * IMPROVEMENT_RESERVE_HOURS)
        material_units, lines = 0, len(goods)
        for process, rate in plans:
            outputs = [process["extracts"]] if process["kind"] == "extraction" else process["outputs"]
            entries = process.get("inputs", []) + outputs
            material_units += sum(entry["quantity"] for entry in entries) * rate / process["work_required"] * reserve_seconds
            lines += len(entries) + (process["power_per_work"] > 0)
        fuel_units = ceil(demand * reserve_seconds / fuel["fuel_energy"])
        warehouse_budgets.append(ceil(living + material_units + fuel_units) + lines * BATCH_UNITS)
    small_capacity = sum(behavior["capacity"] for behavior in blueprint_behaviors("small-warehouse", "storage"))
    large_capacity = sum(behavior["capacity"] for behavior in blueprint_behaviors("large-warehouse", "storage"))
    required_capacity = ceil(max(warehouse_budgets) * WAREHOUSE_HEADROOM)
    if small_capacity < required_capacity:
        raise ValueError(f"small-warehouse: authored storage capacity {small_capacity} is below budget {required_capacity}")
    if large_capacity < small_capacity * LARGE_WAREHOUSE_FACTOR:
        raise ValueError(f"large-warehouse: authored storage capacity {large_capacity} is below {LARGE_WAREHOUSE_FACTOR} small warehouses")

    small_vehicle_materials = Counter()
    for identity, _, _, size, *_ in VEHICLES:
        if size == "small":
            layout = blueprint_definitions[identity]["layout"]
            small_vehicle_materials |= Counter(layout["palette"][cell[3]]["voxel"].removeprefix("core:") for cell in layout["cells"])
    supplied_goods = {item["voxel"] for _, plans, _ in template_estimates for process, _ in plans
                      for item in ([process["extracts"]] if process["kind"] == "extraction" else process["outputs"])}
    construction_materials = Counter()
    for path in (("small-station", "basic-shed", "small-warehouse", "farm"),
                 ("small-station", "basic-shed", "small-warehouse", "power-station", "extraction-site")):
        construction_materials |= Counter(material for identity in path
            for *_, index in blueprint_definitions[identity]["layout"]["cells"]
            if (material := blueprint_definitions[identity]["layout"]["palette"][index]["voxel"].removeprefix("core:")) not in supplied_goods)
    for template in list(template_definitions.values()):
        stock = construction_materials.copy()
        description = "一次性备有"
        if any(behavior.get("max_size_class") is not None
               for plot in template["plots"] if "building" in plot
               for behavior in blueprint_behaviors(plot["building"], "construction")):
            stock.update({good: quantity * INITIAL_SMALL_VEHICLE_MATERIAL_SETS
                          for good, quantity in small_vehicle_materials.items()})
            description += "小型车辆材料和"
        template["inventory"] = [{"voxel": good, "quantity": quantity} for good, quantity in sorted(stock.items())]
        template["display"]["description"] += description + "一套建设设备，用完不补货；建设设备持续供货待 #586。"
        document("settlement-templates", "settlement-template", template["id"], template)

    consumption = []
    for order, (identity, _, _) in enumerate(goods):
        roles = ("food", "improved_diet") if identity == "prepared-food" else (needs[order],)
        consumption.append({"good": identity, "selection_order": order,
                            "satisfies": [{"need": role, "amount_per_good_unit": 1} for role in roles]})
    document("rules", "population-rule", "basic-population",
             {"schema_version": 1, "id": "basic-population", "simulation": {
                 "needs": {need: {"class": "basic" if need in ("food", "clothing") else "improvement",
                                  "amount_per_person_second": need_units_per_person_hour / SECONDS_PER_HOUR,
                                  "weight": 1} for need in needs},
                 "consumption_goods": consumption, "labor_ratio": LABOR_RATIO,
                 "consumption_interval_ticks": CONSUMPTION_SECONDS * TICKS_PER_SECOND,
                 "statistics_period_ticks": STATISTICS_SECONDS * TICKS_PER_SECOND,
                 "base_growth_per_second": DAILY_GROWTH / (HOURS_PER_DAY * SECONDS_PER_HOUR),
                 "improvement_growth_per_second": DAILY_GROWTH / (HOURS_PER_DAY * SECONDS_PER_HOUR),
                 "maximum_loss_per_second": DAILY_MAXIMUM_LOSS / (HOURS_PER_DAY * SECONDS_PER_HOUR)}})
    document("rules", "market-rule", "basic-market", {"schema_version": 2, "id": "basic-market", "simulation": {
        "basic_reserve_hours": BASIC_RESERVE_HOURS, "production_reserve_hours": PRODUCTION_RESERVE_HOURS,
        "improvement_reserve_hours": IMPROVEMENT_RESERVE_HOURS,
        "default_goods_prices": {"sensitivity_basis_points": round(MARKET_SENSITIVITY * BASIS_POINTS),
                                 "minimum_multiplier_basis_points": round(MARKET_MULTIPLIER_LIMITS[0] * BASIS_POINTS),
                                 "maximum_multiplier_basis_points": round(MARKET_MULTIPLIER_LIMITS[1] * BASIS_POINTS),
                                 "spread_basis_points": round(MARKET_MARGIN * BASIS_POINTS)},
        "good_prices": [], "vehicle_buy_multiplier_basis_points": round((1 + MARKET_MARGIN) * BASIS_POINTS),
        "vehicle_sell_multiplier_basis_points": round((1 - MARKET_MARGIN) * BASIS_POINTS),
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
        "deconstruction_work_per_item": 3, "repair_work_per_damaged_part": 4, "power_per_work": 0, "labor_per_work": 0.25}})
    document("railway-rules", "railway-rule", "basic-railway", {"schema_version": 2, "id": "basic-railway",
        "braking_deceleration": 0.7, "minimum_acceleration": 0.15, "minimum_gap_meters": 28,
        "approach_length_meters": 80, "departure_length_meters": 80, "node_speed_limit_meters_per_second": 15,
        "rail_units_per_meter": 0.7, "build_seconds_per_meter": 3, "repair_seconds_per_break": 48,
        "rail_good": "rail", "repair_rail_units_per_break": 18,
        "appearance": {"ballast_voxel": "ballast", "minimum_curve_radius_meters": 80, "maximum_grade": 0.08}})
    document("combat-rules", "combat-rule", "basic-combat", {"schema_version": 1, "id": "basic-combat",
        "retreat_seconds": 24, "morale_damage_coefficient": 0.015, "weapon_destroyed_morale_loss": 0.18,
        "morale_recovery_per_second": 0.012, "surrender_threshold": 0.12, "new_lair_interval_seconds": 5400})
    # 初值待试玩；丘陵兜底所有有限起伏，不同上限保证交集处唯一匹配。
    bands = [{"terrain": terrain, "minimum_elevation": low, "maximum_elevation": high,
              "minimum_moisture": dry, "maximum_moisture": wet, "maximum_relief": relief,
              "detour_multiplier": detour, "maximum_tracks": tracks}
             for terrain, low, high, dry, wet, relief, detour, tracks in (
                 ("plains", 0, 18, 0.2, 0.75, 1.4, 1.12, 2),
                 ("hills", 0, 18, 0, 1, sys.float_info.max, 1.40, 2),
                 ("forest", 0, 18, 0.65, 1, 18, 1.25, 2),
                 ("desert", 0, 18, 0, 0.2, 19, 1.25, 2),
                 ("mountains", 11, 18, 0, 1, 20, 1.65, 1),
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
    cargo_capacity = sum(behavior["capacity"] for train in START_TRAINS for identity in train
                         for behavior in blueprint_behaviors(identity, "storage"))
    freight = blueprint_definitions["small-freight-wagon"]["layout"]
    freight_base_value = sum(values[freight["palette"][index]["voxel"].removeprefix("core:")] for *_, index in freight["cells"])
    # 只估算两列起始车队满舱采购与小型货车组成的预算，不计算核心委托报价。
    initial_money = ceil(START_BUDGET_MULTIPLIER * (cargo_capacity * max(values[good] for good, _, _ in goods) + freight_base_value))
    document("generation-profiles", "generation-profile", "basic-through-world", {
        "schema_version": 2, "id": "basic-through-world",
        "display": {"name": "经济运输验证世界", "description": "真实货物、流程和六份模板计划；两列小型车队、十一种车辆与 NPC 现车已接入。数值待试玩，起始资金的委托可负担性待 #387 验证。"},
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
                                  "required_affordable_commission_design": "small-freight-wagon"},
            "rules": {"population": "basic-population", "market": "basic-market", "construction": "basic-construction",
                      "railway": "basic-railway", "combat": "basic-combat"}},
        "start": {"initial_money": initial_money, "trains": [{"vehicles": train, "inventory": []} for train in START_TRAINS]}})
    for path in list(files) + list(HAND_DRAWN_ICONS) + list(AUTHORED_FILES):
        if path.endswith((".png", ".glb")):
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


def resource_paints_source():
    resources = {profile[1] for profile in RESOURCE_PROFILES}
    entries = "\n".join(f'        "core:{identity}" => new("#{bytes(DEFAULT_PAINTS[identity]).hex()}"),'
                        for identity in sorted(resources & DEFAULT_PAINTS.keys()))
    return ("// Generated by scripts/content/generate_small_world.py.\n"
            "using Train.Content.Contracts;\nusing Train.Core.Contracts;\n\n"
            "namespace Train.Scenes;\n\n"
            "internal static class BuiltInResourcePaints\n{\n"
            "    internal static PaintColor? For(VoxelDefinitionId voxel) => voxel.Value switch\n    {\n"
            + entries + "\n        _ => null\n    };\n}\n").encode("utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "builtin/core")
    args = parser.parse_args()
    files = content_files()
    resources = args.output_dir / "resources"
    paints_path = (ROOT / "Scenes" if args.output_dir.resolve() == ROOT / "builtin/core" else args.output_dir) / "BuiltInResourcePaints.g.cs"
    paints_source = resource_paints_source()
    actual = {path.relative_to(resources).as_posix() for path in resources.rglob("*") if path.is_file()}
    expected = files.keys() | set(HAND_DRAWN_ICONS) | set(AUTHORED_FILES)
    extra = actual - expected
    missing = expected - actual
    if extra or (args.check and missing):
        for label, paths in (("多余", extra), ("缺少", missing if args.check else set())):
            for path in sorted(paths):
                print(f"{label}文件：{path}", file=sys.stderr)
        return 1
    if args.check:
        changed = [path for path, data in files.items()
                   if path not in AUTHORED_FILES and (resources / path).read_bytes() != data]
        if not paints_path.is_file() or paints_path.read_bytes() != paints_source:
            changed.append(str(paints_path))
        if changed:
            for path in changed:
                print(f"字节不同：{path}", file=sys.stderr)
            return 1
        check_schemas(files | {path: (resources / path).read_bytes()
                               for path in AUTHORED_FILES if path.endswith(".json")})
        result = subprocess.run([sys.executable, str(ROOT / "scripts/content/check_economy.py"), str(args.output_dir)])
        if result.returncode:
            return result.returncode
    else:
        for path, data in files.items():
            if path in AUTHORED_FILES:
                continue
            target = resources / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        paints_path.parent.mkdir(parents=True, exist_ok=True)
        paints_path.write_bytes(paints_source)
    print(f"{'校验' if args.check else '生成'} {len(files)} 个文件；超大型蓝图 {len(files['blueprints/extra-large-locomotive.blueprint.json'])} 字节。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
