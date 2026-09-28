# `composite reset`：重置切片 3 静置车辆会话

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数)

```text
composite reset
composite reset hollow
composite reset bridge
composite reset bridge-right
composite reset discard
composite reset save hollow
composite reset cancel
```

与 [`voxel reset`](voxel-reset.md) 走同一保护。世界已对齐保存点时可以不带选择；有未保存变化时必须给出 `discard`、`save` 或 `cancel`。`cancel` 和失败的 `save` 不重置。选择和夹具名可以交换顺序。不带参数且允许重置时，丢弃当前会话中的全部已制造车辆与零件，按当前切片的正式生成入口重建世界，并把 `NextEntityId` 与顶层事务计数恢复为初始值。切片 3 重建为空的 32³ 世界（Seed=42，`GeneratorVersion=slice-3-parked-vehicle-v1`），不把车辆写回世界 Chunk。Playground 已建立固定配置时，重置会重新建立该配置，而不是清除来源。Playground 当前没有固定配置时，命令失败并保留世界，不改走切片重置。

可选夹具名和重置属于同一次候选：先在候选世界上重新建立，再制造 `hollow`（空心壳）、`bridge`（等值桥接体）或 `bridge-right`（右侧核心 Value=20 的桥接体）。定义缺失或制造约束失败时不替换当前世界，未保存标记和已有状态都保留；若选择是先保存，保存成功则记下那个保存点。两边都成功时，重置加夹具的结果成为新的保存边界。完整定义 ID 不是 reset 参数；未知夹具名只给出用法，不重置。已分配的存档 UUID 与显示名保持不变；磁盘上的上一份有效存档也不删除。

结果按字段分行：

```text
Composite world reset.
vehicles 0
next 1
transactions 0
Voxel count: 0.
```

控制台命令和场景快捷键 `R` 都经过同一未保存保护。测试直接调用的 `VoxelDevelopmentSession.Reset()` 是切片原语：没有来源时，它与保护确认之后的重置相同；在已建立固定配置的会话上，它会去掉来源，而不会重新建立该配置。未进入体素开发场景时，顶层 `composite` 会提示先进入场景。

在 Playground 中，重置后 Playground 观察的旧选择与聚焦/跟随目标一律清除；随后夹具制造的车辆即使复用同一 ID 也不会被选中或跟随，镜头原位转为自由观察并提示 `world-replaced reset`（见 [observe](observe.md)）。
