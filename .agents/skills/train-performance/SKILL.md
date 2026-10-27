---
name: train-performance
description: Measure and optimize train against its guarded scenarios for scene opening and far-detail fill-in, full-game frame time and actual speed, and memory. Use when making train faster or lighter, when checking a performance claim or regression in train, and before changing its measurement code.
---

# train 的性能

由 `setup-performance` 技能生成，也用它刷新。目标和背后的理由在决策笔记 `2026-10-26-performance-measurement-system`，目标数值在[设计 00「本阶段性能目标」](../../../docs/design/00-product-and-core-loop.md)，测量代码的规则在 [App/Measurement/AGENTS.md](../../../App/Measurement/AGENTS.md)。每个数字都按 `measure-performance` 技能处理。当前要做哪些性能工作，见决策笔记 `2026-10-26-post-demo-performance-and-memory-gaps`。

## 场景

运行命令里的 `<游戏>` 是 `scripts/measure/export-measurement.ps1` 导出的 ExportRelease 构建，`<存档>` 是 `examples/` 下的示例存档：典型负载 `standard-world`，重负载 `crowded-station`、`busy-combat`、`long-consist`。直接调用 `measure.ps1` 时，大多数场景要先跑同一构建的 `scene-reference`，再用 `-ReferencePath` 传入参考文件。正式测量用 workflow 的 `measure` 选项，输入和命令见 [CI](../../../docs/ci.md#linux-夜间运行)；运行器自动准备参考，不计入指定轮数。触发前须与协调者确认 runner 空闲。

| 用户操作 | 场景 | 守护的代码和内容 | 运行 | 指标 | 基线 | 运行位置 |
|---|---|---|---|---|---|---|
| 场景打开和远处补齐 | `scene-node-first`、`scene-node`、`scene-edge`、`scene-follow` | `Scenes/`、`Rendering/SceneDisplay*`、`App/WorldSceneController.cs`、`App/HostCoordinator.cs` | `& ./scripts/measure/measure.ps1 -ExecutablePath <游戏> -InputPath <存档> -OutputDirectory <空目录> -Scenario scene-node-first,scene-node,scene-edge,scene-follow -ReferencePath <参考文件> -Repeat <轮数>` | 点击到可操作的 p95 和最大值；远景补齐时间 | #824 | Linux runner，只报告 |
| 完整游戏的帧耗时和实际倍速 | `scene-route-1x`、`scene-route-8x`（以及 2x、4x）、`scene-follow-8x`；聚居地和战斗用重负载存档 | `Core/`、`Scenes/`、`Rendering/`、`Ui/`、`App/` | 同上，`-Scenario scene-route-1x,scene-route-8x,scene-follow-8x` | 帧耗时 p50、p95、p99、最大；实际倍速 | #824 | Linux runner，只报告 |
| 内存 | `scene-memory`、`scene-soak` | `Scenes/`、`Rendering/`、`Content/`、`Core/` 的存档和状态 | 同上，`-Scenario scene-memory`；两小时长测用 workflow 的 `window-soak` 选项 | 节点稳态私有字节增量、缓冲区加纹理增量、整个游戏稳态私有字节和工作集、离开节点后强制 GC 的存活堆、每小时增长 | #824 | Linux runner，只报告 |

报告按设计 00 阈值判定由 #822 完成。

## 找出限制因素

| 运行时 | 怎么做 | 能看到什么 |
|---|---|---|
| 游戏进程（.NET） | `dotnet tool install --global dotnet-trace` 后按 PID 附加：`dotnet-trace collect -p <pid>`；`dotnet-counters monitor -p <pid>`；`dotnet-gcdump collect -p <pid>` | CPU 采样、分配和 GC 计数、堆组成。用环境变量启动 EventPipe 会让游戏的 trace 被截断（#478）；进程内 EventPipe 会话可用，但只作临时探针，不提交（#721、#786 的做法） |
| Windows 整机 | `wpr -start CPU -start GeneralProfile`，复现后 `wpr -stop <文件>.etl`，用 `wpa` 打开 | 线程、CPU、磁盘和 GPU 等待；看主线程在等什么 |
| Godot | 导出构建加 `--print-fps`；调试构建加 `--profiling`、`--gpu-profile` | 帧率、GPU 各阶段耗时；`MemoryStatic` 只在调试构建有效 |
| Core 每步 | `Tests/Benchmarks` 的 BenchmarkDotNet 基准（只作诊断，世界很小）；真实存档的每步耗时用 CLI `run` 的 `<output>.progress.jsonl` | 每秒步数、每步分配 |

以上工具在本机可用，按 PID 附加的 dotnet-trace 还没在游戏进程上试过，第一次用时把结果补进这一行。

## 必须保持的行为

| 用户操作 | 功能测试，或手动检查什么 |
|---|---|
| 场景打开和远处补齐 | `dotnet test --project Tests/Scenes/Train.Scenes.Tests.csproj`；`pwsh -NoProfile -File scripts/test.ps1 -Layer engine`；手动：场景就绪后显示内容和改前一致 |
| 完整游戏的帧耗时和实际倍速 | `dotnet test --project Tests/Core/Train.Core.Tests.csproj`；受影响示例存档用 CLI `check` 逐字节比较（见 [示例存档](../../../examples/README.md)） |
| 内存 | 同上两行；内存行为本身没有功能测试，手动检查存读往返和离开节点后状态正确 |

## 优化

1. **定目标：** 上表里的一个场景和它在设计 00 的目标，或用户给的数字。没有场景的操作先按测量规则处理：给已有场景加负载，或请用户定新目标后用 `setup-performance` 刷新。
2. **核对起点：** 在未改动的代码上跑场景和它的功能测试，和存下的基线比较。差距很大说明基线过时或机器不同，先弄清楚再继续。
3. **找限制因素：** 用上面的工具。动手前读这一区域的决策笔记，包括被拒绝的，避免重复试过的做法。
4. **写下假设：** 每条假设指出一个机制，例如「每帧都重新扫描所有高度列」。从便宜的开始试：去掉结果没人用的工作；只做一次；少做；晚点做；在玩家不等的时候做；并行做；做得更快。
5. **一次只改一处：** 每次改动后跑功能测试，再按 `measure-performance` 技能交替运行改前和改后来比较。差异超过波动且测试通过才保留，否则完全撤回。每次尝试（保留的和撤回的）连同数字记在临时目录的文件里。可以在不同工作树里并行准备多个尝试，但测量一次只跑一个。
6. **拒绝：** 收益在完整游戏里消失、拖慢典型负载、复杂度超过收益，或改变了笔记允许之外的行为（Core 模拟结果必须逐字节不变；场景就绪后显示的内容不变；只有流式加载的顺序、时机和就绪前的空白可以变，且不超出目标）。
7. **停止：** 达到目标，或剩下的想法收益太小不值得做时停下，把剩下的想法按价值排好列出来。
8. **交付：** 每个保留的改动单独提交，用 `commit` 技能；改场景的提交单独做，并按测量规则带上新基线。PR 用 `pr` 技能，写主要数字和被拒绝的尝试。别人还会想再试的、被拒绝的想法写成被拒绝的决策笔记。

## 回归

有人说 train 某处变慢或占用了更多内存时，先用 `verify-bug`，再用 `diagnose-bug`，以上表的场景作为复现。

规则来源核对到提交 `43e73fc3`（2026-10-26）。
