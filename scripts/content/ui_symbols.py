"""界面符号的唯一源；生成或 --check 核对 Ui/Symbols/，聚居地由内容生成器输出。"""

import argparse
from math import ceil, cos, floor, pi, sin
from pathlib import Path
import struct
import sys
import zlib


def arc(x, y, radius, start=0, end=360):
    steps = max(1, ceil(abs(end - start) / 6))
    return tuple((x + radius * cos((start + (end - start) * i / steps) * pi / 180),
                  y + radius * sin((start + (end - start) * i / steps) * pi / 180))
                 for i in range(steps + 1))


SYMBOLS = {
    "agricultural-town": (
        ((12, 3), (12, 21)), ((6, 5), (12, 9), (18, 5)),
        ((5, 10), (12, 14), (19, 10)), ((6, 15), (12, 19), (18, 15))),
    "mining-town": (
        ((4, 9), (7, 5), (12, 3), (17, 5), (21, 10)), ((13, 4), (5, 21))),
    "forestry-town": (
        ((12, 3), (5, 11), (8, 11), (3, 17), (10, 17), (10, 21),
         (14, 21), (14, 17), (21, 17), (16, 11), (19, 11), (12, 3)),),
    "oilfield-town": (
        ((12, 3), (8, 8), (5, 14), (5, 17), (8, 21), (16, 21),
         (19, 17), (19, 14), (16, 8), (12, 3)), ((9, 13), (8, 16), (10, 18))),
    "industrial-city": (
        ((3, 21), (3, 11), (9, 7), (9, 11), (15, 7), (15, 12),
         (21, 12), (21, 21), (3, 21)), ((17, 12), (17, 3), (20, 3), (20, 12)),
        ((7, 16), (7, 17)), ((12, 16), (12, 17)), ((17, 16), (17, 17))),
    "lair": (
        arc(12, 18, 9, 180, 360) + ((21, 21), (3, 21), (3, 18)),
        arc(12, 20, 4, 180, 360), ((12, 9), (10, 12))),
    "train-running": (
        ((7, 4), (19, 12), (7, 20)), ((4, 12), (19, 12))),
    "train-servicing": (
        ((4, 6), (20, 6), (20, 20), (4, 20), (4, 6)),
        ((12, 3), (12, 14)), ((8, 10), (12, 14), (16, 10))),
    "train-waiting": (((8, 4), (8, 20)), ((16, 4), (16, 20))),
    "warning": (
        ((12, 3), (22, 21), (2, 21), (12, 3)),
        ((12, 9), (12, 13)), ((12, 17), (12, 17))),
    "train-combat": (
        arc(12, 12, 6), ((12, 2), (12, 8)), ((12, 16), (12, 22)),
        ((2, 12), (8, 12)), ((16, 12), (22, 12))),
    "train-retreating": (
        ((4, 8), (14, 8)) + arc(14, 13, 5, -90, 90)[1:] + ((10, 18),),
        ((8, 4), (4, 8), (8, 12))),
    "notification-info": (
        arc(12, 12, 9), ((12, 11), (12, 17)), ((12, 7), (12, 7))),
    "notification-complete": (arc(12, 12, 9), ((7, 12), (10, 15), (17, 8))),
    "notification-critical": (
        ((8, 3), (16, 3), (21, 8), (21, 16), (16, 21), (8, 21), (3, 16), (3, 8), (8, 3)),
        ((8, 8), (16, 16)), ((16, 8), (8, 16))),
}
SETTLEMENT_SYMBOLS = tuple(name for name in SYMBOLS if name.endswith(("-town", "-city")))


def png(size, pixel):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    rows = b"".join(b"\0" + b"".join(bytes(pixel(x, y)) for x in range(size)) for y in range(size))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def symbol_png(name, size=48):
    samples = 4
    width = size * samples
    scale = width / 24
    mask = bytearray(width * width)
    # 对线段的距离取并集，同时得到 2px 线宽、圆端和圆角；超采样保留缩放边缘。
    for path in SYMBOLS[name]:
        for (ax, ay), (bx, by) in zip(path, path[1:]):
            ax, ay, bx, by = ax * scale, ay * scale, bx * scale, by * scale
            dx, dy = bx - ax, by - ay
            length_squared = dx * dx + dy * dy
            for y in range(max(0, floor(min(ay, by) - scale)), min(width, ceil(max(ay, by) + scale))):
                for x in range(max(0, floor(min(ax, bx) - scale)), min(width, ceil(max(ax, bx) + scale))):
                    px, py = x + 0.5 - ax, y + 0.5 - ay
                    t = max(0, min(1, (px * dx + py * dy) / length_squared)) if length_squared else 0
                    if (px - t * dx) ** 2 + (py - t * dy) ** 2 <= scale * scale:
                        mask[y * width + x] = 1

    def pixel(x, y):
        covered = sum(mask[(y * samples + sy) * width + x * samples + sx]
                      for sy in range(samples) for sx in range(samples))
        return 255, 255, 255, round(255 * covered / (samples * samples))

    return png(size, pixel)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    directory = Path(__file__).resolve().parents[2] / "Ui/Symbols"
    files = {name + ".png": symbol_png(name) for name in SYMBOLS if name not in SETTLEMENT_SYMBOLS}
    if args.check:
        actual = {path.name for path in directory.glob("*.png")}
        different = actual ^ files.keys()
        different |= {name for name, data in files.items()
                      if name in actual and (directory / name).read_bytes() != data}
        if different:
            print("宿主符号缺失、多余或不同：" + ", ".join(sorted(different)), file=sys.stderr)
            return 1
    else:
        directory.mkdir(parents=True, exist_ok=True)
        for name, data in files.items():
            (directory / name).write_bytes(data)
    print(f"{'校验' if args.check else '生成'} {len(files)} 个宿主符号。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
