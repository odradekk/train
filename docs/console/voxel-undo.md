# `voxel undo`：撤销最近一次体素编辑

[返回命令索引](README.md) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel undo
```

命令在暂停状态撤销面板或控制台最近成功提交的即时 `voxel.cell-set`、`voxel add` 或 `voxel remove`。反向操作作为新的正式编辑执行，会取得新的编辑编号并写入确定性日志；模拟步、既有编辑编号和实体 ID 都不回退。成功输出新的 `Edit <id> ... executed ...` 记录。

当前格已被后续修改、反向校验失败时整次拒绝，世界与撤销/重做历史保持不变。没有记录返回 `Edit NoHistory.`。模拟正在运行返回 `Edit SimulationRunning.`，暂停后可重试。面板中有未结束草稿返回 `Edit VoxelDraftExists.`，须先提交或丢弃草稿；草稿自身使用面板内的草稿撤销。撤销成功后可用 [`voxel redo`](voxel-redo.md) 重做；此时成功提交新的体素编辑会丢弃重做分支。

面板和控制台共用同一条历史。历史容量暂为 64 项，淘汰最旧记录时结果追加 `World undo history capacity reached; oldest record evicted.`。保存、另存与导出保留当前会话历史，但历史不写入存档；重新打开存档后没有旧历史。运行、单步、成功切换世界、成功发布内容或经确认成功执行不可撤销修改会结束撤销历史。

不可撤销修改所需的确认词及排期顺序见 [`confirm-end-history`](README.md#confirm-end-history)。
