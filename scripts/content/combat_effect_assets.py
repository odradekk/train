"""特效和损伤遮罩的唯一源；生成或 --check 核对 Rendering/Effects/。"""

import argparse
from math import atan2, floor, hypot, sin
from pathlib import Path
import sys

from ui_symbols import png


ROOT = Path(__file__).resolve().parents[2]
SIZE = 128


def smooth(low, high, value):
    t = max(0, min(1, (value - low) / (high - low)))
    return t * t * (3 - 2 * t)


def hash_value(x, y):
    value = (x * 374761393 + y * 668265263 + 362) & 0xffffffff
    value = ((value ^ (value >> 13)) * 1274126177) & 0xffffffff
    return (value ^ (value >> 16)) / 0xffffffff


def noise(u, v, period):
    x, y = u * period, v * period
    ix, iy = floor(x), floor(y)
    fx, fy = smooth(0, 1, x - ix), smooth(0, 1, y - iy)
    a = hash_value(ix % period, iy % period)
    b = hash_value((ix + 1) % period, iy % period)
    c = hash_value(ix % period, (iy + 1) % period)
    d = hash_value((ix + 1) % period, (iy + 1) % period)
    return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy


def grain(u, v):
    return noise(u, v, 4) * .55 + noise(u, v, 8) * .3 + noise(u, v, 16) * .15


def rgba(*channels):
    return tuple(round(max(0, min(1, value)) * 255) for value in channels)


def damage_pixel(x, y):
    u, v = (x + .5) / SIZE, (y + .5) / SIZE
    coarse = grain(u, v)
    distances = []
    cx, cy = floor(u * 4), floor(v * 4)
    for dy in range(-1, 2):
        for dx in range(-1, 2):
            ix, iy = cx + dx, cy + dy
            px = ix + .2 + hash_value(ix % 4, iy % 4) * .6
            py = iy + .2 + hash_value(iy % 4 + 11, ix % 4) * .6
            distances.append(hypot(u * 4 - px, v * 4 - py))
    distances.sort()
    crack = 1 - smooth(.015, .065, distances[1] - distances[0])
    spall = (1 - smooth(.12, .36, distances[0])) * smooth(.35, .65, coarse)
    angle = atan2(v - .5, u - .5)
    radius = hypot(u - .5, v - .5)
    spoke = 1 - smooth(.02, .13, abs(sin(angle * 5 + noise(u, v, 8) * .55)))
    ring = 1 - smooth(.008, .024, abs(radius - .24 - sin(angle * 3) * .018))
    glass = max(spoke, ring * .55) * (1 - smooth(.30, .49, radius))
    return rgba(smooth(.15, .85, coarse), crack, spall, glass)


def particle_pixel(x, y):
    u, v = (x + .5) / SIZE, (y + .5) / SIZE
    px, py = (u - .5) * 2, (v - .5) * 2
    radius = hypot(px, py)
    texture = grain(u, v)
    smoke = (1 - smooth(.25, .96, radius + (texture - .5) * .35)) * (.45 + texture * .6)
    flame_width = .12 + v * .58
    flame_x = px + sin(v * 11) * .08 + (texture - .5) * .25
    flame = (1 - smooth(flame_width * .3, flame_width, abs(flame_x)))
    flame *= smooth(.02, .22, v) * (1 - smooth(.68, .99, v)) * (.65 + texture * .35)
    glow = (1 - smooth(0, .95, radius)) ** 2
    ray = max(0, 1 - abs(px * py) * 36) * (1 - smooth(.12, .9, radius))
    glow = max(glow, ray * .28)
    drop = (1 - smooth(.18, .42, hypot(px, (py - .27) * .9)))
    trail = (1 - smooth(.025, .09, abs(px))) * smooth(-.9, -.1, py) * (1 - smooth(.05, .4, py))
    return rgba(smoke, flame, glow, max(drop, trail * .7))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--output-dir", type=Path, default=ROOT / "Rendering/Effects")
    args = parser.parse_args()
    files = {"damage-mask.png": png(SIZE, damage_pixel), "particle-mask.png": png(SIZE, particle_pixel)}
    actual = {path.name for path in args.output_dir.glob("*.png")}
    extra = actual - files.keys()
    missing = files.keys() - actual
    if extra or (args.check and missing):
        for label, paths in (("多余", extra), ("缺少", missing if args.check else set())):
            for path in sorted(paths):
                print(f"{label}文件：{path}", file=sys.stderr)
        return 1
    if args.check:
        changed = [path for path, data in files.items() if (args.output_dir / path).read_bytes() != data]
        if changed:
            for path in changed:
                print(f"字节不同：{path}", file=sys.stderr)
            return 1
    else:
        args.output_dir.mkdir(parents=True, exist_ok=True)
        for path, data in files.items():
            (args.output_dir / path).write_bytes(data)
    print(f"{'校验' if args.check else '生成'} {len(files)} 张特效遮罩图。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
