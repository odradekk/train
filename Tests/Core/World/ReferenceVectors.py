"""独立复算 KeyedRandomTests 的编码及采样向量。

运行 `python Tests/Core/World/ReferenceVectors.py` 输出 JSON；程序只使用
Python 标准库，不加载 Train.Core。SplitMix64 参考：
https://prng.di.unimi.it/splitmix64.c
"""

import json
import struct
import sys


MASK = (1 << 64) - 1


def mix(value):
    """按 SplitMix64 的无符号 64 位混合步骤计算结果。"""
    value = (value + 0x9E3779B97F4A7C15) & MASK
    value = ((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9) & MASK
    value = ((value ^ (value >> 27)) * 0x94D049BB133111EB) & MASK
    return value ^ (value >> 31)


def vector(seed, purpose, components):
    """按随机键公开布局编码输入，并计算对应的无状态采样值。"""
    purpose_bytes = purpose.encode("utf-8")
    raw = (
        b"TRNG"
        + struct.pack("<IQI", 1, seed, len(purpose_bytes))
        + purpose_bytes
        + struct.pack("<I", len(components))
        + b"".join(struct.pack("<Q", component) for component in components)
    )
    value = 0
    for byte in raw:
        value = mix(value ^ byte)
    return {
        "seed": seed,
        "purpose": purpose,
        "components": components,
        "encoded": raw.hex().upper(),
        "sample": mix(value ^ len(raw)),
    }


inputs = [
    (0, "fixture.empty", []),
    (42, "fixture.resource-choice", [6, 7]),
    (42, "fixture.resource-choice", [7, 6]),
    (MASK, "fixture.boundary", [0, MASK]),
    (0, "资源", [0]),
    (42, "fixture.resource-choice/other", [6, 7]),
    (0, "ab", [99]),
    (0, "a", [98, 99]),
    (0, "é", []),
    (0, "e\u0301", []),
    (42, "combat.hit.v1", [1, 100, 0, 7, 0]),
    (42, "combat.hit.v1", [1, 100, 1, 2, 0]),
    (0, "combat.hit.v1", [MASK, MASK, 0, 1, 2147483647]),
    (42, "combat.hit.v1", [1, 100, 2, 7, 3]),
    (42, "creature.spawn.v1", [4, 9]),
    (42, "creature.raid.target.v1", [4, 9]),
    (42, "creature.raid.position.v1", [4, 9, 6]),
    (42, "creature.wander.v1", [3, 9]),
    (7, "creature.lair.node.v1", [12]),
    (7, "creature.lair.type.v1", [12, 5]),
    (40, "settlement.founding.type", [14]),
    (40, "settlement.founding.template", [14, 16, *b"core:mining-town"]),
    (44, "world.stage1.v1.npc.vehicle.count", [3, 7, 0]),
    (44, "world.stage1.v1.npc.vehicle.choice", [3, 7, 2]),
    # ResourceStateTests 用 WorldResources.Sample 核对这四个值：货物身份按 UTF-8 长度与字节展开，再接序号。
    (42, "resource.regeneration.node.v1", [16, *b"fixture:material", 0]),
    (42, "resource.regeneration.reserve.v1", [16, *b"fixture:material", 0]),
    (42, "resource.regeneration.node.v1", [16, *b"fixture:material", 1]),
    (42, "resource.regeneration.reserve.v1", [len("矿:铁".encode()), *"矿:铁".encode(), 0]),
]

assert mix(0) == 0xE220A8397B1DCDAF
assert mix(MASK) == 0xE4D971771B652C20
sys.stdout.reconfigure(encoding="utf-8")
print(json.dumps([vector(*item) for item in inputs], ensure_ascii=False, indent=2))
