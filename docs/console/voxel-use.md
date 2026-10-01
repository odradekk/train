# `voxel use`：切换到具名固定配置

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel use straight-rail-empty
voxel use straight-rail-train discard
voxel use straight-rail-empty save
voxel use straight-rail-train cancel
```

仅世界编辑器会话可用。旧切片体素开发场景返回 `IncompatibleBase` 和 `stage base subject session relationship world_editor`，保留当前世界，也不会先保存。

参数：一个具名固定配置 ID，当前是 `straight-rail-train` 或 `straight-rail-empty`。可选 `discard` / `save` / `cancel` 与 [`voxel reset`](voxel-reset.md) 相同。

当前世界可以来自空白、固定配置或没有 `world_source` 的旧切片存档，不要求目标配置与当前范围、种子或来源相同。未知配置、声明无法建立或初始世界被拒绝时，不替换当前世界，并报告 `IncompatibleBase` 或 `MissingDefinition` 以及阶段、对象和关系。成功时重新建立目标配置，会话暂停在建立边界，清空存档 UUID，使下一次 [`voxel save`](voxel-save.md) 不会覆盖切换前的世界。来源里的 `base_fingerprint` 描述这次建立的基底，不把配置写成生成器，也不写入 Catalog Generation。成功清理旧观察引用；失败保留世界和观察状态。

成功文本以 `Voxel configuration <id>.` 开头，并说明是否暂停。`cancel`、未选择的未保存状态和保存失败都不会切换。保存成功但候选失败时，当前世界留在刚才保存的边界上。

在真实世界编辑器中，确认、保存和候选建立分帧完成。期间模拟推进和新的世界修改暂挂；取消或失败后恢复原来的运行或暂停状态，不补算等待时间。切换已经开始时，另一条世界切换命令不会更换目标。

世界编辑器面板的“切换到另一配置”和“重新建立当前配置”走同一保护。世界编辑器中的 `Esc` 和 [`quit`](quit.md) 也经过正常离开保护。
