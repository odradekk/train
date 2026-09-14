# `rail query`：查询已提交轨段、占格与通行区间

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

```text
rail query
rail query 80 1 14
rail query occupancy
rail query intervals
rail query connections
rail query graph
rail query rebuild
rail query assembly
rail query motion
```

无参数时列出轨段数、连接数、轨枕数和当前实际占格。三整数坐标查询该格是否仍被正式轨道占用，以及各轨段的世界 X 支撑区间。边界接触不算正体积重叠；边缘格 `(80,1,14)`、左右钢轨和跨 Chunk / 技术接缝都应能查到。

`occupancy` 给出当前占用与登记格数（登记在局部全毁后仍保留）。`intervals` 按权威世界写出各轨段当前可通行世界 X。`connections` 只列出明确端点连接，相邻但未连接的轨段不会出现。`graph` 读取派生索引；未刷新时为 `stale`，且不会把缺损区间判为可通行。`rebuild` 从权威状态重建派生图后再查询，不会移动已提交编组的引用、接触或姿态。`assembly` 查询当前正式编组：活动车头、稳定轨段沿程、前接触点、速度、牵引开关、模拟步、运营资格和真实车钩零件身份。`motion` 只读步号、暂停标志和编组速度。没有编组时 `assembly` 输出 `Rail assembly none.`。必要接触零件损毁时结构结算会合法清理编组；查询保持只读，返回 none，不越界、不伪造第二个接触点。车辆仍停在原地，可用 [`composite query`](composite-query.md) 查看失效资格。起停见 [`rail start`](rail-start.md) / [`rail stop`](rail-stop.md)，按步见 [`rail step`](rail-step.md)。

该命令只读，不改变世界、身份或事务计数。
