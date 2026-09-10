# `composite damage`：对车辆零件施加伤害

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
composite damage <id> <x> <y> <z> <amount>
composite damage 1 3 1 1 100
composite damage 1 3 1 1 100 1 1 1 100
```

参数：车辆运行时 ID，以及一组或多组局部格子 `x y z amount`。`amount` 必须是正整数。坐标是车辆 Local Grid，不是世界格子，也不接受小数。一组目标走单目标伤害；多组目标属于同一伤害事件：先完整校验，再统一扣耐久、删除归零零件，最后只计算一次六面连通分量并完成身份裁决与脱落。不能用 `voxel remove` 或直接删零件代替本命令验证破坏。

成功时先写总状态，再按输入顺序列出每个目标的局部格、状态、伤害值和剩余耐久，然后列出仍存在的车辆摘要和世界体素数。部分伤害或断裂后仍有存活 Composite 返回 `Damaged`；无人保留 Composite 身份时返回 `Removed`，剩余碎块按整体 Snap 转入世界或整块销毁。

从切片 3 桥接夹具：

```text
composite reset bridge
composite damage 1 3 1 1 100
composite query
composite query damaged
```

桥被删除后两侧核心均存活。等值平局由无状态 SplitMix64 裁决，价值最高（平局时分数最高）的分量继承原车辆 ID，另一侧获得新 ID。两侧都缺少两个接触点，运营状态为 `Disabled`，不能当成可运行车辆。世界体素数保持 0。

```text
composite reset bridge-right
composite damage 1 3 1 1 100
```

右侧核心 Value=20，右侧必为主继承者。

```text
composite reset bridge
composite damage 1 3 1 1 100 1 1 1 100
```

桥和左核心同事件归零后，仅右侧保留 Composite 身份；左侧失去身份的分量整体转入世界网格。交换这两组目标的输入顺序，最终权威结果相同。

```text
composite reset bridge
composite damage 1 3 1 1 100 1 1 1 100 5 1 1 100
```

桥和两核心同事件归零后无人保留 Composite 身份，剩余体素仍走完整脱落规则。

`amount` 为 `0` 或负数返回 `InvalidDamage`；未知车辆返回 `UnknownVehicle`；目标局部格没有零件返回 `Empty`。任一项失败则整件事失败，已提交布局、零件状态、所有权和车辆身份保持不变。查询（含 `composite query damaged`）不推进事务。参数个数无法解析时只给出用法，不执行伤害。当前不支持车辆存档、改装或沿轨运动。
