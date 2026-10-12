# Windows 阶段检查的候选基准

以下截图基准、容差和吞吐阈值均**等待用户确认**，由 [#407 的批准计划](https://github.com/odradekk/train/issues/407#issuecomment-6131866759)授权提交候选值。目前只用于验证检查机制，不能据此宣布阶段或产品性能验收通过。

## 主菜单截图

输入为 [windows-main-menu.png](../Tests/Engine/Baselines/windows-main-menu.png) 与 [windows-main-menu.json](../Tests/Engine/Baselines/windows-main-menu.json)。基准通过物理路径直接读取；已有的 `Tests/.gdignore` 阻止编辑器为测试 PNG 生成导入文件。截图检查复用引擎测试程序集、Bootstrap 和 JUnit；仅在 Windows 有窗口运行中执行。普通无界面引擎运行在执行前排除截图套件，不把它记为跳过。

候选图来自当前真实 App 主菜单，固定 1920×1080、Forward+、Vulkan。采集机为 Windows 11 `10.0.26300.9550`，GPU 为 NVIDIA GeForce RTX 3080 Laptop GPU，图形 API 为 Vulkan `1.4.329`，Godot 为 `4.7.2-stable`。

2026-10-13 在三个独立 Godot 进程和独立用户目录中采集。三次 PNG 相同，SHA-256 为 `49c47021a731951921085cc2672e6915019f743fc8e0b45c1973a2239e356130`；三组两两比较的最大 RGBA 通道差和不同像素数均为零。因此候选值为 `MaxChannelDelta=0`、`MaxDifferentPixelFraction=0`，不为未观察到的像素波动增加容差。

比较先把两图转为 RGBA8。任一通道差严格大于 `MaxChannelDelta` 时，该像素计为不同；不同像素比例必须小于等于 `MaxDifferentPixelFraction`。尺寸不一致、缺少基准或容差均失败。此候选只在上述配置验证，其他 GPU、驱动和 Godot 版本需要重新核对。

## 资源模拟吞吐量

输入为 [windows-resource-simulation.json](../Tests/Benchmarks/Baselines/windows-resource-simulation.json)，候选最低吞吐为 **240,000 步/秒**。负载固定为 `resource-simulation-v1`：16 个资源请求实体，每批 65,536 个完整模拟步，每次迭代重建会话，结束时核对步数、库存和各实体持有量。它测量当前资源模拟夹具的核心吞吐，不提供帧耗时或完整游戏 8× 运行证据。

测量使用 BenchmarkDotNet `0.15.8`、独立 Release 构建；每次启动一次测量进程，预热 3 次、测量 15 次。采集机为上述 Windows 环境，CPU 为 Ryzen 9 5900HX（8 核、16 线程），SDK 为 `10.0.112`，运行时为 `10.0.12`。

三次独立完整测量的吞吐为 481,450、485,927、303,052 步/秒，均值为 423,476；极差占均值约 43.18%。第三次明显较慢，保留其原始结果。共享机器的外部负载和热状态未被控制，因此这些数值只是候选。阈值取最低吞吐的 80% 后向下取整到 240,000，比最低值低约 20.81%，比均值低约 43.33%。Linux 只记录吞吐和原始日志，不应用 Windows 阈值。

Benchmark 项目不在 `Train.sln` 中，需显式构建 Release：

```powershell
dotnet build Tests/Benchmarks/Train.Benchmarks.csproj -c Release
dotnet run --project Tests/Benchmarks/Train.Benchmarks.csproj -c Release --no-build -- --artifacts <本次运行目录>
```

`throughput.json` 提供负载身份、每步耗时、样本数和步/秒。默认 BDN 日志和报告保留原始测量，不额外导出无人读取的 Full JSON。

## 证据与接入状态

原始截图、两两差异数据、JUnit、设备身份，以及三次 Benchmark 的命令、退出码、耗时和原始测量保留在忽略目录 `TestResults/issue-407-candidate-baselines/`，长期引用写入 Issue 完成评论。提交的 PNG、容差和阈值是检查输入，不提交这批运行报告。

当前统一入口接入等待 #411 合入并 merge `origin/master`。接入时无界面引擎层最低用例数须为 9，并按 #411 的结构检查规则处理 `Train.Benchmarks` 登记和单独 Release 构建。Windows 缺少必需基准或阈值时阶段层失败；非 Windows 的导出、导出后运行、截图和阈值判定记为未执行。

`windows-checks.yml` 已提供每日 UTC 20:23（北京时间 04:23）和手动 `nightly` 的 Linux job，只有这两种选择分配自托管 runner。当前调用全部正确性三层，stage 调用仍待上述接入；目前没有 Linux 检查需要渲染上下文，因此未使用 xvfb。runner 尚未恢复在线，尚未 dispatch 或取得 Linux 执行证据。
