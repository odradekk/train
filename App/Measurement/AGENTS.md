# 性能测量

这个目录放守护 train 三个操作的测量场景：场景打开和远处补齐、完整游戏的帧耗时和实际倍速、内存。它由 `setup-performance` 技能建立；负载、目标和预算的理由在决策笔记 `2026-10-26-performance-measurement-system`，目标数值在设计 00「本阶段性能目标」。这里是项目基础设施：改动要审查、要测试，和产品代码一样保持干净。

## 布局

- 游戏内的测量场景在这个目录，因为它们要编进游戏程序集。场景名用 `scene-*`，由 `MeasurementRun` 按 `--measure=<场景>` 分派；按场景类别拆成 `MeasurementRun.<类别>.cs` 分部文件。帧统计、报告、路线记录等支撑代码是不带 `MeasurementRun.` 前缀的独立文件。
- runner 一侧的脚本在 `scripts/measure/`：运行命令、导出、常驻显示、负载采样和 CPU 预检、交替对照运行器、汇总分析。
- 只为某个模块做的诊断放在该模块旁边，不属于测量场景。
- 测量专用的依赖只有：.NET 运行时和 Godot 自带的计数、`Tests/Benchmarks` 的 BenchmarkDotNet（只作 Core 诊断）。新增测量框架或依赖先写决策笔记。

## 运行

- `& ./scripts/measure/measure.ps1 -ExecutablePath <导出的游戏> -InputPath <存档> -OutputDirectory <空目录> -Scenario <场景列表> -Repeat <轮数>` 运行指定场景；不给 `-Scenario` 时跑默认列表。大多数 `scene-*` 场景先要同一构建的 `scene-reference` 输出。
- 正式测量在 Linux runner 上，用 workflow 的 `measure` 选项手动触发；输入和命令见 [CI](../../docs/ci.md#linux-夜间运行)。运行器先跑一次参考，不计入指定轮数，再把参考传给每个所选场景。一次一个 job，触发前须与协调者确认 runner 空闲。开发机只用于定位问题和同一窗口内的交替对照，在协调者安排的安静窗口里做。
- 测量代码通过发布的构建加载产品代码：`scripts/measure/export-measurement.ps1` 导出 ExportRelease 构建，游戏以真实 1920×1080 窗口运行（Linux 包在 gamescope 里），带 `--measure=<场景>`。无界面、窗口尺寸不对或被最小化的运行判为无效。
- 每个场景通过 `MeasurementReport` 输出每轮的 `report.json`，`measure.ps1` 写 `summary.json` 和 `external-load.jsonl`。报告逐项对照设计 00 的阈值由 #822 完成。
- 测量一次只跑一个；同一时段机器上不跑其他重负载。

## 新增和修改

- **先找再加。** 加场景前，先找测量同一个用户操作的场景，给它加负载，而不是另起一个。新场景要在决策笔记 `2026-10-26-performance-measurement-system` 里有目标；新目标要用户批准并更新笔记。
- **写明计划。** 每个场景按 `measure-performance` 技能的字段写明计划，放在场景分部文件开头的说明里，包括反向验证时在哪里注入了开销。
- **只共享真正共用的。** 支撑代码先放在用到它的场景旁边；第二个场景需要同样的行为时，再移到共用的支撑文件，两边都用它。
- **测真实的东西。** 通过真实入口调用产品代码，测发布的构建。不为测量新增产品接口，不复制产品算法，功能断言留给功能测试。产品代码只能带这些不改变行为的观测钩子：`SceneDisplay` 的 `Measure`／`Observe`、`DesignEditorPanel.ElementsUpdated`、`EditorSceneController.FirstPresented`、`DeveloperInspector`。新增钩子要不改变行为，并经 reviewer 批准。
- **数据。** 用户和生产数据不进夹具、日志或报告。已提交的示例存档（`examples/`）原样使用；规模点存档由已提交的 CLI 配方生成。不从网络或仓库外读文件。
- **基线**放在 `scripts/measure/baselines/`，提交进仓库；两个导出预设都排除了 `scripts/**`，基线不会进入游戏导出包（#824 建立）。每份基线记录：提交号；工作树有没有未提交改动；内容版本（示例存档的提交号）；机器、电源模式和运行时的外部负载；Godot 和 .NET 版本；轮数、中位数和范围。忙碌时采到的基线重采，或标明。存下的基线用来看长期漂移；判断一次改动用改前改后交替运行。
- **保持数字可比。** 改了场景测什么（负载、计时边界、完成点），新旧数字就不能比。这种改动和优化分开提交，同一改动里记录新基线，提交说明写明原因。
- **预算。** 目前没有预算，只报告。加预算需要决策笔记，并先在执行它的机器上校准，设在实测波动之上。
- **删掉已不存在的。** 用户操作或代码路径被删除时，对应场景在同一改动里删掉。
- **在外面探索。** 探索性脚本和剖析结果放在仓库外的临时目录，满足上面的规则后才能进来。

## 检查

这里的代码和产品代码一样通过构建和测试：改动后运行 `dotnet test --project Tests/Godot/Train.Godot.Tests.csproj --filter "FullyQualifiedName~Train.Godot.Tests.Measurement"`。有逻辑的支撑代码（统计、判定、汇总）要有测试；脚本测试入口见 [`scripts/measure/README.md`](../../scripts/measure/README.md#测试与手动验证)。根 `AGENTS.md` 的检查表列出了这里改动对应的检查。
