# `voxel use`：切换 Playground 具名配置

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel use straight-rail-empty
voxel use straight-rail-train discard
voxel use straight-rail-empty save
voxel use straight-rail-train cancel
```

参数：一个具名固定配置 ID，当前是 `straight-rail-train` 或 `straight-rail-empty`。可选 `discard` / `save` / `cancel` 与 [`voxel reset`](voxel-reset.md) 相同。

当前会话没有固定配置来源时，命令失败并保留会话，关系为 `world_source`。未知配置、声明无法建立或初始世界被拒绝时，不替换当前世界，并报告 `IncompatibleBase` 或 `MissingDefinition` 以及阶段、对象和关系。成功时重新建立目标配置，会话暂停在建立边界，清空存档 UUID，使下一次 [`voxel save`](voxel-save.md) 不会覆盖切换前的世界。来源里的 `base_fingerprint` 描述这次建立的基底，不把配置写成生成器，也不写入 Catalog Generation。

成功文本以 `Voxel configuration <id>.` 开头，并说明是否暂停。`cancel`、未选择的未保存状态和保存失败都不会切换。保存成功但候选失败时，当前世界留在刚才保存的边界上。

Playground 面板的 “切换到另一配置” 和 “重置” 走同一保护。`Esc` 与 `quit` 不是切换。
