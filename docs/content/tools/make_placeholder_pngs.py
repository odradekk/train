"""为内容包里的体素定义生成占位 PNG。

读取 <包>/resources/voxels/*.voxel.json，为每个引用到的 PNG 生成一张纯色占位图：
颜色按材质类别区分，面图边长为 32 像素乘以平铺跨度，正面图 32 像素，图标 128 像素。
已存在的文件默认跳过，加 --force 才覆盖，避免冲掉正式素材。只用标准库。

用法：python make_placeholder_pngs.py <包目录> [--force]
"""
import json
import os
import struct
import sys
import zlib

MATERIAL_COLORS = {
    "painted_metal": (96, 112, 128),
    "bare_metal": (150, 150, 156),
    "concrete": (170, 165, 155),
    "stone_soil": (122, 100, 80),
    "glass": (40, 60, 82),
    "polymer_rubber": (52, 52, 52),
    "organic": (112, 142, 62),
}
FACE_SHADE = {"top": 1.15, "side": 1.0, "bottom": 0.75, "front": 1.0}


def shade(color, factor):
    return tuple(max(0, min(255, round(c * factor))) for c in color)


def write_png(path, size, pixel):
    rows = b"".join(
        b"\x00" + b"".join(bytes(pixel(x, y)) for x in range(size)) for y in range(size)
    )

    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header)
                + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b""))


def face_pixel(color, size, front):
    edge = shade(color, 0.6) + (255,)
    fill = color + (255,)
    mark = shade(color, 1.4) + (255,)
    lo, hi = size // 4, size - size // 4

    def pixel(x, y):
        if x in (0, size - 1) or y in (0, size - 1):
            return edge
        if front and lo <= x < hi and lo <= y < hi:
            return mark
        return fill
    return pixel


def icon_pixel(color):
    # 图标透明底，物品画在中间约 100×100 的安全区内（美术 07）。
    edge = shade(color, 0.6) + (255,)
    fill = color + (255,)

    def pixel(x, y):
        if not (14 <= x < 114 and 14 <= y < 114):
            return (0, 0, 0, 0)
        if x in (14, 113) or y in (14, 113):
            return edge
        return fill
    return pixel


def main():
    args = [a for a in sys.argv[1:] if a != "--force"]
    force = "--force" in sys.argv[1:]
    voxels = os.path.join(args[0], "resources", "voxels")
    done = set()
    for name in sorted(os.listdir(voxels)):
        if not name.endswith(".voxel.json"):
            continue
        with open(os.path.join(voxels, name), encoding="utf-8") as f:
            definition = json.load(f)
        own = os.path.join(voxels, definition["id"])
        presentation = definition["appearance"]["presentation"]
        color = MATERIAL_COLORS[presentation.get("material", "painted_metal")]
        jobs = [(definition["display"]["icon"], 128, icon_pixel(color))]
        span = presentation.get("tile_span", 1)
        for face, ref in presentation.get("faces", {}).items():
            size = 32 if face == "front" else 32 * span
            jobs.append((ref, size, face_pixel(shade(color, FACE_SHADE[face]), size, face == "front")))
        for ref, size, pixel in jobs:
            path = os.path.join(voxels, ref) if ref.startswith("shared/") else os.path.join(own, ref)
            if path in done or (os.path.exists(path) and not force):
                continue
            done.add(path)
            write_png(path, size, pixel)
            print(os.path.relpath(path, args[0]))


if __name__ == "__main__":
    main()
