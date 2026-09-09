# `voxel load`：从存档恢复世界

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel load 11111111-2222-4333-8444-555555555555
```

参数：一个必填 UUID，必须能被 `Guid` 解析（推荐 `D` 格式）。非法 UUID 文本返回用法提示，不读取文件，也不改世界。

加载使用**当前**已发布目录，而不是存档里记住的整份定义。请求路径中的 UUID 必须与文件根字段 `id` 一致，否则在替换世界或会话身份之前返回 `MismatchedId`。先按当前 Seed / 生成器 / 边界完整构造并校验候选世界，再整体替换会话世界。重建所需的 `external_definitions`（定义 ID 与保存时 `max_durability`）必须完整；缺记录整次拒绝，不会用当前上限猜测未写入 Delta 的绝对值。实例耐久保持存档绝对值；仅当当前上限低于存档值时截断。上限升高、Value/Mass/显示名、Semantic Revision 或无关内容变化不会单独拒绝加载。只有生成基底占用格的定义或朝向变化（会让恢复布局错位）才返回 `IncompatibleBase`。解析失败（未知根字段或嵌套未知字段、缺必填字段、截断）一律返回非成功状态，不会出现 `Loaded` / `Succeeded=true` 但世界未替换。

成功后正式 `query` / `add` / `remove` / `damage` / `collide`、引用校验、`reload`、网格和 `none` / `full_box` / `local_box` 都绑定恢复后的世界。会话 UUID 与显示名改为存档中的值，之后 [`voxel save`](voxel-save.md) 仍写入同一目录。

成功按字段分行输出 `Voxel Loaded.`、`id`、`name` 和当前总数。失败保留当前可编辑世界、当前会话 UUID/显示名和磁盘上的上一份有效存档，并报告：

| 状态 | 含义 |
| --- | --- |
| `NotFound` | 该 UUID 下没有 `save.json` |
| `ReadFailed` / `Truncated` | 文件无法读完、JSON 不完整或缺少必填字段 |
| `UnknownFormat` | `save_format_version` 不受支持，或出现未知根/嵌套字段 |
| `UnknownGenerator` | 生成器版本不受支持 |
| `IncompatibleBase` | 当前有效基底与存档基底布局不兼容，或身份（seed/尺寸）不匹配 |
| `MismatchedId` | 路径 UUID 与文件内 `id` 不一致 |
| `MissingDefinition` | 当前目录缺少存档用到的定义，或缺少重建所需的 `external_definitions` 记录（本切片不回退缺失 Mod，也不用当前上限补基线） |
| `InvalidDelta` / `Occupied` / `OutOfBounds` | Delta 非法、重复占格或越界 |

退出进程后再启动、进入同一切片，再用本命令恢复。不要用销毁会话或仅 `voxel reset` 代替进程重启验证。
