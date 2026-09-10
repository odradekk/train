# `composite reset`：重置切片 3 静置车辆会话

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数)

```text
composite reset
composite reset hollow
composite reset bridge
composite reset bridge-right
```

不带参数时立即丢弃当前会话中的全部已制造车辆与零件，按当前切片的正式生成入口重建世界，并把 `NextEntityId` 与顶层事务计数恢复为初始值。切片 3 重建为空的 32³ 世界（Seed=42，`GeneratorVersion=slice-3-parked-vehicle-v1`），不把车辆写回世界 Chunk。

可选夹具名会在同一正式重置之后立刻制造对应结构：`hollow` 为空心壳，`bridge` 为等值桥接体，`bridge-right` 为右侧核心 Value=20 的桥接体。完整定义 ID 不是 reset 参数；未知夹具名只给出用法，不重置。没有撤销或确认步骤。已分配的存档 UUID 与显示名保持不变；磁盘上的上一份有效存档也不删除。

结果按字段分行：

```text
Composite world reset.
vehicles 0
next 1
transactions 0
Voxel count: 0.
```

该命令与场景快捷键 `R`、以及测试使用的 `VoxelDevelopmentSession.Reset()` 是同一正式入口。未进入体素开发场景时，顶层 `composite` 会提示先进入场景。
