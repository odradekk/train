# train

仅 Debug 构建且接入新 `HostCoordinator` 时可用。默认旧模拟入口不提供此命令。

```text
train form <车辆ID>...
train disband <列车ID>
train start <列车ID>
train stop <列车ID>
train plan <列车ID> once|loop <计划项>...
```

`form` 按输入顺序编组车辆；`disband` 解散列车；`start` 启动列车；`stop` 停止列车；`plan` 替换运行计划。`once` 表示单次运行，`loop` 表示循环。计划至少有一项，按输入顺序执行：

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

格式错误或没有活动会话时，显示中文错误并记录 Warning，不提交命令。解析只检查语法，不检查车辆、列车、节点是否存在或其他业务规则；业务检查由 Core 执行。通过解析的命令以 `CommandSource.Console` 提交，结果记录到日志，失败原因显示在界面。错误不会自动重试。

实现见 [`App/TrainConsoleCommands.cs`](../../App/TrainConsoleCommands.cs)。
