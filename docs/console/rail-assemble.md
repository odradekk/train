# `rail assemble`：制造并定位一车头一车厢的刚性编组

[返回命令索引](README.md) · [场景前提与参数](README.md#使用前提与通用参数) · [结果与错误处理](README.md#结果与错误处理)

编组不可撤销；结束撤销历史所需的确认词及排期顺序见 [`confirm-end-history`](README.md#confirm-end-history)。

```text
rail assemble
rail assemble at 330
```

本命令提交 `train.assemble` 世界修改，与其他修改共用 [`voxel edits`](voxel-edits.md) 的编号、边界顺序、日志与存档。无 `at` 时在当前边界立即执行并输出下文的编组结果；`at <step>` 排到该步边界，输出 `Edit <id> train.assemble pending` 与接受/目标位置，结果在执行位置按当时铁路和内容确定；`at` 等于当前步时立即执行，按 [`voxel edits`](voxel-edits.md) 的两行格式回报。修改编号或边界序号耗尽输出 `Rail assemble InputExhausted.`，会话已故障输出 `Rail assemble SimulationFaulted.`，均不分配编号。

参数：无。走与开发场景、测试相同的正式入口 `AssembleStaticTrain()`：从已发布 JSON Blueprint 制造 `core:dev-locomotive` 与 `core:dev-carriage`，按直轨约束求解刚体姿态，并用真实存活车钩零件建立一条明确连接。不按空间邻近自动接续，也不推进列车。

成功要求当前会话已有正式铁路、尚无编组，且两车具备存活动力/控制/轨道接触能力、接触支撑和车辆约束。接触支撑按完整三维接触、实际轨段与当前连续有效区间校验；同一路径上明确连接的技术切分共享端点仍算支撑，参考按朝向与实际连接归属，不按登记顺序、也不凭相邻自动接续。车钩必须是本票相向连接端及其存活零件，不能用任意端墙件或包络间隙冒充。一次事务分配两车零件身份与编组身份，并先预检实体 ID 与事务容量。失败不制造车辆、不改世界占用、不消费实体 ID 或事务计数。

成功输出 `Rail assemble Formed assembly <id>.`。失败输出 `Rail assemble <Status>.`，例如没有铁路为 `NoRailway`，已有编组为 `AlreadyAssembled`。

编组后用 [`rail query assembly`](rail-query.md) 查看活动车头、前接触点、运营资格和车钩身份；用 [`rail start`](rail-start.md) 启动牵引。用 [`composite query`](composite-query.md) 查看两车定义、能力和零件。正式铁路与编组随 [`voxel save`](voxel-save.md) 写入格式 5；失败时保留旧会话和上一份有效文件。
