# 内容包 manifest

每个 Mod 的包目录下有一个 `mod.json`，声明包的身份、版本和依赖。Schema 见 [schemas/manifest.schema.json](schemas/manifest.schema.json)，示例见 [examples/example-pack/mod.json](examples/example-pack/mod.json)。规则的来由见决策笔记 `2026-10-09-package-dependencies-and-missing-placeholders`。

内置的 `core` 包没有 manifest。

## 字段

| 字段 | 类型 | 必填 | 默认值 | 说明与校验 | 阶段 |
| --- | --- | --- | --- | --- | --- |
| `schema_version` | 整数 | 是 | — | 固定为 2 | 首个切片 |
| `id` | 字符串 | 是 | — | 小写、带连字符的 UUID。它也是这个包的命名空间，其他包用 `<UUID>:<local id>` 引用这个包的定义。两个包的 UUID 相同，两个都不加载 | 首个切片 |
| `name` | 字符串 | 是 | — | 显示名称，去掉首尾空白后 1–100 个字符 | 首个切片 |
| `version` | 字符串 | 是 | — | SemVer 2.0 版本号 | 首个切片 |
| `authors` | 字符串数组 | 是 | — | 1–16 个作者，每个去掉首尾空白后 1–100 个字符，不能重复 | 首个切片 |
| `description` | 字符串 | 否 | 空字符串 | 去掉首尾空白后最多 4000 个字符 | 首个切片 |
| `dependencies` | 对象数组 | 否 | `[]` | 依赖的包，见下表 | 首个切片 |

### `dependencies` 的每一项

| 字段 | 类型 | 必填 | 说明与校验 |
| --- | --- | --- | --- |
| `id` | 字符串 | 是 | 依赖的包的 UUID，或者 `core`。同一个包只能写一次，不能写自己 |
| `name` | 字符串 | 否 | 那个包的显示名称，1–100 个字符。只在缺少这个依赖时用来告诉玩家缺了什么 |
| `min_version` | 字符串 | 是 | 要求的最低版本，SemVer 2.0；按 SemVer 的规则比较 |

- 不写 `core` 时，默认依赖任意版本的 `core`；写了 `core`，就能要求最低游戏版本。
- 加载按依赖排序。缺依赖、依赖版本太低，或者处在依赖循环里，这个包都不加载，依赖它的包也跟着不加载。
- 定义本身不声明依赖。包里的定义引用了其他包，那个包必须写在这里，否则这个包无效。

## 加载器检查的规则

- `id` 是合法的 UUID，和其他包不重复；`version` 和各 `min_version` 是合法的 SemVer。
- 依赖不重复、不写自己；依赖的包都已加载，版本不低于 `min_version`；不处在依赖循环里。
- 包里的定义引用了其他包，那个包必须写在 `dependencies` 里（`core` 除外）。

## 和现有代码的差别

当前宿主通过真实 `ContentFactory` 加载本文的 `schema_version: 2`，检查依赖、最低版本、循环及级联排除。旧版本 1 的加载实现已在 [#452](https://github.com/odradekk/train/issues/452) 退役；当前入口及实现范围见[内容契约](../contracts/content.md)。
