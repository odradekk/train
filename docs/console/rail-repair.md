# `rail repair`：修复原轨段损坏占格

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail repair 110 1 15
rail repair 110 1 15 144 1 14
rail repair 110 1 15 at 330
```

本命令提交 `rail.repair` 世界修改，与其他修改共用 [`voxel edits`](voxel-edits.md) 的编号、边界顺序、日志与存档；坐标按输入顺序保存。无 `at` 时在当前边界立即执行并输出下文的铁路结果；`at <step>` 排到该步边界，输出 `Edit <id> rail.repair pending` 与接受/目标位置，结果到执行位置才确定并记入日志。已完全摧毁的轨段仍按其登记身份和几何定位，排队的修复在执行时恢复它。

参数：一组或多组世界坐标。只修复已登记的原轨段占格，恢复 `core:rail` 与基础耐久 100，并恢复依赖该格的通行区间。轨段身份、几何和沿程原点不因修复而改变。

成功输出 `Rail Repaired.`、恢复格数和当前占格数，并按当前世界重查已提交编组的支撑与停车原因；通过后只转为可启动，不自动起步、不恢复旧速度。重复修复完好格得到 `Intact`；未知格得到 `UnknownCell`；格内已是非轨体素得到 `Conflict`。失败时原状态不变，也不分配新轨段身份。另建相同位置的新轨段不能覆盖已登记占格，也不会接替旧 `RailPosition`。

另建轨段请用 [`rail construct`](rail-construct.md)，那会申请新身份，且不能覆盖已有映射。伤害仍用 [`voxel damage`](voxel-damage.md)。
