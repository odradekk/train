# 界面字体

使用 Noto Sans SC 2.004 的官方 SC 地区子集，常规和粗体均为静态 OTF。family 名为 `Noto Sans SC`，覆盖官方简体中文地区字符集；字体文件直接取自官方发布，未转换或自行裁剪。

- [官方发布 Sans2.004](https://github.com/notofonts/noto-cjk/releases/tag/Sans2.004)，固定提交 `523d033d6cb47f4a80c58a35753646f5c3608a78`。
- [官方下载格式说明](https://github.com/notofonts/noto-cjk/blob/523d033d6cb47f4a80c58a35753646f5c3608a78/Sans/README.md#region-specific-subset-otfs)：Region-specific Subset OTFs 提供对应地区的字形。
- [常规字体来源](https://raw.githubusercontent.com/notofonts/noto-cjk/523d033d6cb47f4a80c58a35753646f5c3608a78/Sans/SubsetOTF/SC/NotoSansSC-Regular.otf)。
- [粗体字体来源](https://raw.githubusercontent.com/notofonts/noto-cjk/523d033d6cb47f4a80c58a35753646f5c3608a78/Sans/SubsetOTF/SC/NotoSansSC-Bold.otf)。
- [许可证来源](https://raw.githubusercontent.com/notofonts/noto-cjk/523d033d6cb47f4a80c58a35753646f5c3608a78/LICENSE)，原文保存在 `OFL.txt`，随字体一同分发。

| 文件 | 字重 | 字节数 | SHA-256 |
| --- | --- | --- | --- |
| NotoSansSC-Regular.otf | 400 | 8,331,336 | `faa6c9df652116dde789d351359f3d7e5d2285a2b2a1f04a2d7244df706d5ea9` |
| NotoSansSC-Bold.otf | 700 | 8,543,168 | `c6cb5a93abaa9edc8ee7463b7ebb7f42d618d40e6ed2f7a5371c97b0b64767c0` |

用 fontTools 4.63.0 核对二进制：两份均为 Version 2.004、31,036 个 glyph、30,890 个 Unicode 字符、1000 units/em，无 `fvar` 表；GSUB 和 GPOS 均无 `tnum` 特性。ASCII 数字 0–9 默认已经等宽，常规 advance 均为 555，粗体均为 590。正文、数字及常用界面文字样本的字形轮廓与同发布完整字库相同。

两份字体合计 16,874,504 字节，约 16.1 MiB。按 Issue #320 决定 7，数字直接使用默认等宽字形，不设置 `tnum`，不另加数字字体。官方地区子集与项目自行按用字裁剪不同；后者仍是美术 07 的开放问题。
