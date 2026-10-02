# `composite manufacture`：从已发布 Blueprint 制造静置车辆

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数)

制造不可撤销。历史非空时须尾随 `confirm-end-history` 明确结束历史；若同时排期，写作 `at <step> confirm-end-history`。首次未确认提交及失败提交都保留历史。

```text
composite manufacture [<namespace:id>] [pose <x> <y> <z>] [at <step>]
composite manufacture
composite manufacture core:dev-hollow-shell
composite manufacture core:dev-hollow-shell pose 60 6 24
composite manufacture pose 60 6 36 at 305
```

参数：可选的已发布 Composite 定义 ID，必须是小写 `namespace:id`。省略时制造正式夹具 `core:dev-hollow-shell`。可选 `pose <x> <y> <z>` 为世界平移（与文化无关的有限十进制数），局部轴保持默认；非有限或无法解析时给出用法提示。可选尾随 `at <step>` 把制造排到该模拟步边界，见 [`voxel edits`](voxel-edits.md)。场景快捷键 `M` 在切片 3 中走同一入口，只制造该夹具。

成功时按当前内容目录中的不可变定义做 Construction Validity 校验，再预检车辆和全部零件所需的稳定整数 ID，随后写入会话运行态。默认刚体平移为 `(8,8,8)`，连续姿态的局部 +X/+Y 为 `(1,0,0)` / `(0,1,0)`，Rotation Snap 为 `px_py`。控制台可用 `pose` 改变平移，不能改轴；正式会话可以提交有限连续轴，伤害脱落时再整体吸附。制造姿态是命令负载的一部分，定时制造在接受时就确定姿态，到期才分配 ID。车辆体素不进入世界 Chunk 网格。测试和场景读取命令返回的 ID，不硬编码恰好分配的数字。

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
| `UnknownVoxel` / `DisallowedOrientation` / `NonFiniteTransform` | 零件定义、朝向或 Transform 非法。`default` 或轴长为零的姿态虽有限但不是合法旋转，制造前拒绝，不分配 ID、不推进事务 |
| `TransactionExhausted` | 再提交会进入不可加载的事务计数（`MaxValue - 1` 或 `MaxValue`）；不制造、不绕回、不改车辆 |
| `EntityIdExhausted` | 车辆加全部零件所需 ID 不能一次满足；不制造，权威状态、`NextEntityId` 和事务计数不变 |

伤害、断裂与脱落使用 [`composite damage`](composite-damage.md)。已制造车辆的五类原子改装使用 [`composite add`](composite-add.md) / [`remove`](composite-remove.md) / [`move`](composite-move.md) / [`rotate`](composite-rotate.md) / [`replace`](composite-replace.md) 与 [`composite batch`](composite-batch.md)。制造后的实际车辆随 [`voxel save`](voxel-save.md) / [`voxel load`](voxel-load.md) 按格式 5 保存和恢复，不按 Blueprint 名称重造。本构建仍可读格式 2–4。切片 4 编组与沿轨运动状态一并写入同一存档。非法 ID 文本返回用法提示，不执行制造。
