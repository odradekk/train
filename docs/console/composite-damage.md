# `composite damage`：对车辆零件施加伤害

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
composite damage <id> <x> <y> <z> <amount>
composite damage 1 3 1 1 100
composite damage 1 3 1 1 100 1 1 1 100
```

参数：车辆运行时 ID，以及一组或多组局部格子 `x y z amount`。`amount` 必须是正整数。坐标是车辆 Local Grid，不是世界格子，也不接受小数。一组目标走单目标伤害；多组目标属于同一伤害事件：先完整校验并规划扣耐久、删除归零零件后的六面连通分量，预检整次事件的后继车辆身份与新增编组所需新 ID 和事务容量后，才统一提交伤害、身份裁决、碎块脱落，再按存活连接与沿轨资格重建编组。失败全退，观察者只能看到已提交结果。不能用 `voxel remove`、删除节点或专用断开布尔值代替本命令验证破坏。运行中车头后车钩归零后，前车继续牵引，后车按当前 `track_contact` 减速度独立滑行。

成功时先写总状态，再按输入顺序列出每个目标的局部格、状态、伤害值、剩余耐久，以及结算后的零件身份与当前位置（仍在车上则写当前车辆 ID，已转入世界则写世界格）。总状态只看本次受影响车辆及其后继，不因场上无关车辆改为 `Damaged`。部分伤害或断裂后仍有受影响 Composite 返回 `Damaged`；这些车辆都不再保留身份时返回 `Removed`，剩余碎块按整体 Snap 转入世界或整块销毁。转入世界的目标若零件仍在，对应行是 `Damaged` 且带 `world` 格与 `part` ID，不是 `Removed/0`。

```text
target (0,0,0) Damaged by 1 durability 99 vehicle 57 part 2
target (3,1,1) Removed by 100 durability 0 part 29
```

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

`amount` 为 `0` 或负数返回 `InvalidDamage`；未知车辆返回 `UnknownVehicle`；目标局部格没有零件返回 `Empty`；再提交会进入不可加载的事务计数返回 `TransactionExhausted`；次继承者所需 ID 不足返回 `EntityIdExhausted`。任一项失败则整件事失败，已提交布局、零件状态、所有权和车辆身份，以及 `NextEntityId` 和事务计数保持不变。查询（含 `composite query damaged`）不推进事务。参数个数无法解析时只给出用法，不执行伤害。改装后的伤害必须读实际布局与同一身份，不能用改装前的旧格子打到已搬走的零件。若该车已有存档内自定义定义，致命删除或断裂后的每个后继按自己的真实零件和仍绑定的接触点重建该定义与 Revision，并把 `LastUsedRevision` 对齐到新的 `SaveLocalRevision`；不对受损碎块再跑施工合法性。从未改装过的车（没有存档内定义）断裂后仍保持 `SaveLocalDefinition` 为空，不发明完整存档。这些实际后继与脱落身份可随 [`voxel save`](voxel-save.md) 写入格式 3；本构建仍可读格式 2。切片 4 正式铁路与沿轨运动状态一并写入同一存档。

```text
composite reset bridge
composite add 1 3 0 1 core:refit-marker px_py
composite damage 1 3 1 1 100 3 0 1 100
composite query
```

第二桥 `(3,0,1)` 与原桥同事件归零后，两侧各 27 零件、各 1 个接触点；查询到的存档内 Revision 已变，零件格子与定义格子一致，世界体素仍为 0。
