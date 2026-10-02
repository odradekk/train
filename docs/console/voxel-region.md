# `voxel region`：长方体区域体素编辑

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [朝向编码](README.md#朝向编码) · [结果与错误处理](README.md#结果与错误处理)

```text
voxel region add <x1> <y> <z1> <x2> <z2> <height> <namespace:id> <orientation> [at <step>] [confirm-large-edit] [confirm-end-history]
voxel region remove <x1> <y> <z1> <x2> <z2> <height> [at <step>] [confirm-large-edit] [confirm-end-history]
voxel region replace <x1> <y> <z1> <x2> <z2> <height> <namespace:id> <orientation> [at <step>] [confirm-large-edit] [confirm-end-history]

voxel region add 4 1 4 7 7 3 core:ferrous-ore px_py
voxel region replace 4 1 4 7 7 3 core:ledge pz_py at 303
voxel region remove 4 1 4 7 7 3
```

两个角的 X/Z 坐标都包含端点，次序可互换。`y` 是底层；区域从该层向上延伸 `height` 层，第二个角不另带 Y。高度必须为正数，区域最多包含 4096 格。

`add` 只修改空格并跳过已有普通世界体素；`remove` 只修改已有普通世界体素并跳过空格；`replace` 只修改已有普通世界体素，并跳过空格及定义和朝向已经相同的格。输出分别报告 `affected` 和 `skipped` 数量。没有实际修改目标时整条命令拒绝。

区域中的每个实际修改目标会作为显式格列表写入正式 `voxel.cell-set` 命令。带 `at <step>` 的命令排入待执行队列，之后世界变化不会扩大或移动它的目标范围；到达目标步时仍按正式规则重新校验并原子执行。

实际修改超过 512 格时，首次提交返回 `LargeEditConfirmationRequired`，且不占用编辑编号。把 `confirm-large-edit` 加在命令末尾后重新提交。若同时需要结束撤销历史，固定尾部顺序为：

```text
... at <step> confirm-large-edit confirm-end-history
```

任一覆盖格越界，或碰到铁路、车辆、未知定义、不允许的朝向等非法状态时，整个区域拒绝且世界不变；即使该格本来会被跳过也须检查。结果列出总数和最多六条格冲突，更多冲突以 `conflicts omitted <数量>` 汇总。铁路与车辆冲突会带归属身份；其他冲突也会带状态和坐标。每行保持在控制台的 200 字符限制内。
