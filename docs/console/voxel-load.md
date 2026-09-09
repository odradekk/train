# `voxel load`：从存档恢复世界

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel load 11111111-2222-4333-8444-555555555555
```

参数：一个必填 UUID，必须能被 `Guid` 解析（推荐 `D` 格式）。非法 UUID 文本返回用法提示，不读取文件，也不改世界。

加载使用**当前**已发布目录，而不是存档里记住的整份定义。先按当前 Seed / 生成器 / 边界完整构造并校验候选世界，再整体替换会话世界。实例耐久保持存档绝对值；仅当当前上限低于存档值时截断。上限升高、Value/Mass/显示名或无关内容变化不会单独拒绝加载。只有生成基底占用格的定义或朝向变化（会让恢复布局错位）才返回 `IncompatibleBase`。

成功后正式 `query` / `add` / `remove` / `damage` / `collide`、引用校验、`reload`、网格和 `none` / `full_box` / `local_box` 都绑定恢复后的世界。会话 UUID 与显示名改为存档中的值，之后 [`voxel save`](voxel-save.md) 仍写入同一目录。

成功按字段分行输出 `Voxel Loaded.`、`id`、`name` 和当前总数。失败保留当前可编辑世界和磁盘上的上一份有效存档，并报告：

| 状态 | 含义 |
| --- | --- |
| `NotFound` | 该 UUID 下没有 `save.json` |
| `ReadFailed` / `Truncated` | 文件无法读完或 JSON 不完整 |
| `UnknownFormat` | `save_format_version` 不受支持 |
| `UnknownGenerator` | 生成器版本不受支持 |
| `IncompatibleBase` | 当前有效基底与存档基底布局不兼容，或身份（seed/尺寸）不匹配 |
| `MissingDefinition` | 当前目录缺少存档用到的定义（本切片不回退缺失 Mod） |
| `InvalidDelta` / `Occupied` / `OutOfBounds` | Delta 非法、重复占格或越界 |

退出进程后再启动、进入同一切片，再用本命令恢复。不要用销毁会话或仅 `voxel reset` 代替进程重启验证。
