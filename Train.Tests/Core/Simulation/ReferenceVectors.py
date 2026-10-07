"""独立复算 KeyedRandomTests 的编码及采样向量。

运行 `python Train.Tests/Core/Simulation/ReferenceVectors.py` 输出 JSON；程序只使用
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
]

assert mix(0) == 0xE220A8397B1DCDAF
assert mix(MASK) == 0xE4D971771B652C20
sys.stdout.reconfigure(encoding="utf-8")
print(json.dumps([vector(*item) for item in inputs], ensure_ascii=False, indent=2))
