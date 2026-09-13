# `rail repair`：修复原轨段损坏占格

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail repair 110 1 15
rail repair 110 1 15 144 1 14
```

参数：一组或多组世界坐标。只修复已登记的原轨段占格，恢复 `core:rail` 与基础耐久 100，并恢复依赖该格的通行区间。轨段身份、几何和沿程原点不因修复而改变。

成功输出 `Rail Repaired.`、恢复格数和当前占格数。重复修复完好格得到 `Intact`；未知格得到 `UnknownCell`；格内已是非轨体素得到 `Conflict`。失败时原状态不变，也不分配新轨段身份。

另建轨段请用 [`rail construct`](rail-construct.md)，那会申请新身份，且不能覆盖已有映射。伤害仍用 [`voxel damage`](voxel-damage.md)。
