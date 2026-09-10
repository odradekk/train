# `composite manufacture`：从已发布 Blueprint 制造静置车辆

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数)

```text
composite manufacture
composite manufacture core:dev-hollow-shell
```

参数：可选的已发布 Composite 定义 ID，必须是小写 `namespace:id`。省略时制造正式夹具 `core:dev-hollow-shell`。场景快捷键 `M` 在切片 3 中走同一入口，只制造该夹具。

成功时按当前内容目录中的不可变定义做 Construction Validity 校验，再分配车辆与零件的稳定整数 ID，写入会话运行态。默认刚体平移为 `(8,8,8)`、朝向 `px_py`。车辆体素不进入世界 Chunk 网格。测试和场景读取命令返回的 ID，不硬编码恰好分配的数字。

成功按字段分行：

```text
Composite Valid.
vehicle 1
parts 168
```

失败只返回 `Composite <Status>.`，不部分制造，也不递增 `NextEntityId` 或事务计数。常见状态：

| 状态 | 含义 |
| --- | --- |
| `UnknownDefinition` | 当前已发布目录没有该 Composite |
| `UnknownArchetype` | 定义引用的 Archetype 不在候选中 |
| `Disconnected` / `Empty` / `DuplicateOccupancy` | 布局不满足 Archetype 的非空六邻接规则 |
| `MissingContact` / `DuplicateContact` / `CoincidentContacts` / `UnboundContact` | 接触锚点数量、重合或绑定失败 |
| `EnvelopeExceeded` / `UnknownSizeClass` | 声明规格或固定包络不成立 |
| `UnknownVoxel` / `DisallowedOrientation` / `NonFiniteTransform` | 零件定义、朝向或 Transform 非法 |

当前不支持沿轨运动、破坏、完整改装或车辆存档。非法 ID 文本返回用法提示，不执行制造。
