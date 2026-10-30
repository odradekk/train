# 为存档指定测量目标

`measure.ps1` 和 `run-measurement.ps1` 接受同名参数：`-TrainId` 选择跟随列车，`-NodeId` 选择节点视图，`-EdgeId` 选择线路视图。参考运行和正式场景必须使用相同目标；目标不存在时运行失败，不自动替换。

普通场景省略目标时使用列车 1、节点 2、线路 11。`scene-soak` 默认选择 `-SoakProfile standard-world`，省略线路时仍使用原来的线路 15；显式 `-EdgeId` 会覆盖该值。该默认长测的顺序、目标角色和结束条件保留不变。

三个重负载存档使用以下配置，输入路径为 `examples/<存档>/save.json`：

| 存档及 SoakProfile | TrainId | NodeId | EdgeId | 优先视图 |
| --- | ---: | ---: | ---: | --- |
| crowded-station | 21 | 2 | 3 | 聚居地节点 |
| busy-combat | 21 | 2 | 3 | 节点战斗 |
| long-consist | 21 | 15 | 23 | 列车跟随 |

重负载长测每段暂停读入原存档、准备优先视图，再按 1×、8×各采 10 秒。主体依次采地图、节点、线路、跟随和编辑器；编辑器使用指定列车的第一辆车。busy-combat 的节点视图就是节点战斗，不重复取样。没有的节点战斗或线路战斗会记录原因。

负载判断读取不晚于保存点的最近一组配方期望；其中的 `node` 表示只统计该节点。全部期望满足才标为 `full`，下降后标为 `below-recipe`；新采样窗还没有本会话的新计数时标为 `unknown`。报告同时记录期望步数、保存步数和实际计数。

协调者分配 runner 时段后，可以用以下命令做短预演：

```powershell
gh workflow run windows-checks.yml --repo odradekk/train --ref <分支> `
  -f selection=window-soak -f measure_input=examples/busy-combat/save.json `
  -f measure_train=21 -f measure_node=2 -f measure_edge=3 `
  -f measure_soak_profile=busy-combat -f soak_seconds=300
```

`window-soak` 每个 job 固定一轮，默认 7200 秒。打开、内存和路线测量选择 `measure`，同样传入存档、目标及配置，其他输入见 [CI](../../docs/ci.md#linux-夜间运行)。
