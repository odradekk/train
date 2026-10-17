# `train`：编组、解编与运行计划

[返回命令索引](README.md)

仅 Debug 构建且接入新 `HostCoordinator` 时可用。默认旧模拟入口不提供此命令。

```text
train form <车辆ID>...
train disband <列车ID>
train start <列车ID>
train stop <列车ID>
train plan <列车ID> once|loop <计划项>...
```

设计编辑期间，所有 `train` 子命令拒绝并报告 `editing_active`，不提交核心命令。

`form` 按输入顺序编组车辆；`disband` 解编列车；`start` 启动列车；`stop` 停止列车；`plan` 替换运行计划。`once` 表示单次运行，`loop` 表示循环。计划至少有一项，按输入顺序执行：

- `station:<节点ID>:<停站秒数>`：停站，秒数为非负整数，换算为模拟步数；自动货物规则为空。
- `waypoint:<节点ID>`：途经节点。

所有 ID 为非零无符号 64 位整数。秒数必须能在无符号 64 位范围内换算为模拟步数。命令、子命令、`once`、`loop`、`station` 和 `waypoint` 不区分大小写。不支持查询、报价、自动货物规则、引号或转义。

```text
train form 12 8 15
train plan 3 once station:5:10 waypoint:9 station:11:0
train plan 3 loop station:5:30 waypoint:9
train start 3
train stop 3
train disband 3
```

节点内启停和计划替换使用同一套位置阶段，语法不变：

- `form` 创建的列车停在节点等候点 W，阶段 `Waiting`，无站台预约。
- `stop` 对静止列车原地停车；进站中的列车制动到停站点 S，已过 S 则继续驶出并在下一节点停车。在线路上请求停在前方节点 S；已有站台预约保留。
- `plan` 从新计划第一项开始，保留节点内位置和预约；进站可重选下一线路，出站保持已定驶出线路。`Departing` 阶段（包括停在 L 等铁轨的列车）依序消费新计划开头指向本站的 `waypoint`；本站 `station` 先驶出再返回。若消费后单次计划完成，仍在行驶的列车保留已定驶出线路并设置停车请求，继续驶出，在下一节点 S 停稳后转为 `Stopped`；已静止在 L 的列车原地转为 `Stopped`，清除停车请求和排队步。前者若到 L 正式选轨被拒绝，也原地转为 `Stopped` 并清除停车请求和排队步。已有站台预约均保留到实际驶出、删除或解编时才释放。其余 `waypoint` 越过 S 才消费；沿进入线路原路返回须先在 S 停稳，反向起步才切换编组正反标记。
- `start` 继续计划和暂停停留。若启动或换计划才新增本站 `station`，进站列车已过 W 但尚未越过 S，或已静止在 S，没有空闲站台也接受命令，在 S 以 `platform_wait` 等候，获预约后开始停留。已越过 S、处于 `Departing` 的列车（含停在 L 等铁轨的）保留已定驶出线路，先驶出，返回本站时再预约和停留。正常 Station 在 S 停稳后的下一步才开始倒计时。

节点三段尺度、预约释放及排队规则见[铁路设计](../design/06-railway.md#节点与站台)。

格式错误或没有活动会话时，显示中文错误并记录 Warning，不提交命令。解析只检查语法，不检查车辆、列车、节点是否存在或其他业务规则；业务检查由 Core 执行。通过解析的命令以 `CommandSource.Console` 提交，结果记录到日志，失败原因显示在界面。错误不会自动重试。

实现见 [`App/TrainConsoleCommands.cs`](../../App/TrainConsoleCommands.cs)。
