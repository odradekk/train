# 测量辅助脚本

`measure.ps1` 默认测量无需参考文件的 `scene-route-1x`、`scene-route-8x`，其余场景须显式选择；`export-measurement.ps1` 用于导出。场景和报告见 [测试手册](../../docs/run-tests.md#观察无关检查与窗口测量)。依赖 PowerShell 7、Python 3 标准库和系统计数器，无需 Pester 或额外包。

`-NodeReadyHold` 仅使 `scene-node-first` 和参考运行的指定节点 Ready 后保持 60 秒，其余场景忽略。runner 的 `measure_node_ready_hold` 默认 false，仅传给 `measure` 的参考和场景运行。首开 `measure_seconds` 为 0–120，不改保持时长（默认 5 秒，开启后 60 秒）；混选路线须为 0。`scene-map` 末段纯地图固定保持 60 秒，不受此参数控制；其他地图保持仍为 5 秒。旧 `scene-map` 读数不可比；已提交的基线见 [baselines/](baselines/)。

两入口共用 `-TrainId`／`-NodeId`／`-EdgeId`／`-SoakProfile`；默认值、重负载目标和 workflow 调用见[测量目标](targets.md)。

## 开发机交替对照

`compare-measurement.ps1` 在协调者安排的安静窗口里做 Windows 开发机交替对照；正式测量用 Linux runner。须备好两侧 Release 导出及所需参考。参考须与测量使用同一构建、存档、列车和节点。

`-NodeReadyHold` 开启时透传给 `measure.ps1`；`-TrainId`、`-NodeId` 仅显式指定时透传，均为正整数，默认分别为 1、2。对照入口不构建、导出或制作参考。示例：

```powershell
& ./scripts/measure/compare-measurement.ps1 `
  -AExecutablePath <A构建>/train.exe -BExecutablePath <B构建>/train.exe `
  -InputPath examples/basic-through/save.json -Scenario scene-follow-8x `
  -AReferencePath <A参考>/scene-reference.json -BReferencePath <B参考>/scene-reference.json `
  -OutputDirectory TestResults/comparison -Repeat 5 -DurationSeconds 30 `
  -CpuMeanThresholdPercent 25 -CpuConsecutiveThresholdPercent 40
python scripts/measure/summarize.py TestResults/comparison/A TestResults/comparison/B --output TestResults/comparison/result.json
```

输出目录须为新目录或空目录。`Repeat` 是每侧、每场景的计划轮数，默认 5；每个场景依次运行 A1、B1、A2、B2……，多个场景依参数顺序执行。两侧使用相同存档、场景和时长；`DurationSeconds=0` 沿用场景默认值。

每次调用 `measure.ps1 -Repeat 1` 前，先观察完整 30 秒 CPU。均值不超过 `CpuMeanThresholdPercent`，且窗口内不存在任意相邻两次都严格超过 `CpuConsecutiveThresholdPercent` 的采样，才启动测量。默认门槛为 25% 和 40%；恰好等于门槛可通过。采样开头、结尾或相邻样本的缺口超过 2 秒，缺值、错误或无效值都使预检为 `unknown`。计数器的初始化读数不作空闲样本。

预检为 `busy` 或 `unknown` 时，不启动该次测量，主序列结束后按原顺序补一次。补跑仍拒绝就保留未完成；已经启动但失败或超时的测量不补跑。任何计划项未完成，入口最终返回 1；全部完成返回 0，性能差值不决定退出码。参数或文件错误会直接失败。

`comparison.json` 保留每次尝试、预检结果、启动与退出状态；每次尝试的 `precheck.json` 保留原始采样。`A/summary.json` 和 `B/summary.json` 使用 `measure.ps1` 的 `runs` 格式，每个计划项只保留最终尝试用于汇总；先前拒绝的记录仍在 `comparison.json` 中。补跑使用新目录，不覆盖原始证据。

## 外部负载与常亮

`measurement-host.ps1` 由三个 PowerShell 入口 dot-source 引入，集中维护常亮类型、负载采样和预检判定。

Windows 每个采样进程只创建一次 `System.Diagnostics.PerformanceCounter`，使用英文名称：`Processor/% Processor Time/_Total`、`Memory/Available Bytes`、`Memory/Pages Input/sec`、`Memory/Page Reads/sec`。硬件身份的一次性 CIM 查询仍在 `measure.ps1`，不用于负载轮询。Linux 继续读取 `/proc/stat` 和 `/proc/meminfo`，记录温度、交流电状态。两者都记录 Godot、train 和 dotnet 进程的累计 CPU 秒及工作集。

每秒向 `external-load.jsonl` 追加一条记录；单次采样失败时追加带 `error` 的记录并继续。第一次就失败也写 `sampler.ready`。采样进程无法启动或关闭时，`measure.ps1` 写 `sampler-errors.log`，不因此中止游戏测量。采样日志缺失不代表系统空闲，也不能当作性能可比的证据。

在 Windows 单独保持显示器常亮：

```powershell
pwsh -NoProfile -File scripts/measure/keep-display.ps1 -Seconds 300
```

脚本使用与 `measure.ps1` 相同的专用线程持有显示请求，到时、异常和 Ctrl+C 都通过 `finally` 释放；不用全局键盘或鼠标操作。输出分别为 `display acquired seconds=300` 和 `display released`。

手动验证不用管理员权限：在 PowerShell 中定义下面的只读查询，分别在持有前、持有期间和释放后调用 `Read-ExecutionState`；另一终端运行 `keep-display.ps1 -Seconds 8`。

```powershell
Add-Type -Namespace MeasurementValidation -Name Power -MemberDefinition @'
[DllImport("powrprof.dll")]
public static extern uint CallNtPowerInformation(int level, IntPtr input,
    uint inputSize, out uint output, uint outputSize);
'@
function Read-ExecutionState {
    [uint32] $state = 0
    $status = [MeasurementValidation.Power]::CallNtPowerInformation(
        16, [IntPtr]::Zero, 0, [ref] $state, 4)
    if ($status -ne 0) { throw "CallNtPowerInformation failed: $status" }
    '0x{0:X8}' -f $state
}
Read-ExecutionState
```

`16` 是 `SystemExecutionState`，结果中的 `0x2` 位是 `ES_DISPLAY_REQUIRED`。查询的是整机合并状态；其他进程也持有显示请求时，该位不会因本脚本结束而清除。没有其他显示请求时，预期输出为：

```text
持有前：0x00000001
持有期间：0x00000003
释放后：0x00000001
```

## 逐项性能判定

`report.json` 的 `assessment` 按[设计 00「本阶段性能目标」](../../docs/design/00-product-and-core-loop.md#本阶段性能目标)逐项报告结果。`conclusion` 为“待验收”，`reason` 说明缺目标配置实机与完整覆盖，`source` 指向设计目标。各项 `items` 包含 `metric`、`window`、`unit`、`rule`、`threshold`、`measured`、`status`、`reason`，可附 `observed_seconds`、`excess`。阈值由报告代码维护。

`status` 为 `passed`、`failed` 或 `unknown`。缺字段、无效数值、缺成对基线或截断均为 `unknown`；运行失败时各项均为 `unknown`。capture/reference 场景不作性能判定。`outcome` 表示运行是否完成，`timing`、`identity` 和 `scope.final_performance=false` 保持原义；逐项通过不能代替整体验收。

路线帧耗时 p95、p99、最大值排除初始打开：`identity.scene_budget.route.startup.first_operable_at` 与 `timing.frames[].elapsed_seconds` 同起点，排除帧结束时刻≤可操作时刻的记录，首个可操作帧间隔仍属打开。三项用 `window` 说明范围、`window_start_seconds` 记录相对起点、`excluded_frames` 记录排除数；缺可操作时刻或后续帧则为 `unknown`，缺时刻则两字段为 `null`。计入可操作后补齐和途中切换；实际倍速用全窗。其他项两字段为 `null`，[场景准备](scene-preparation.md)的 `excluded_frames` 除外。保留 `timing` 和旁车。

打开 p95 按类别至少需 30 个有效样本；最大值和补齐也只用有效打开。补齐从可操作起检查 5 秒内的 64 m 范围。`scene-memory` 不作正式打开时延证据，节点只观察约 5 秒，不按稳态判定。节点增量按 `click` 配对最近地图读数：首次取初始地图末 1 秒中位数，之后取最近 `natural-map.memory`；缺基线时增量 `unknown`。返回地图后强制 GC 的存活堆减 `cycle 0` 初值。原报告的 `steady` 只是末 1 秒统计，须另核对稳定性，未稳定时不据此宣称通过预算。

场景准备的判定、旁车与汇总见 [scene-preparation.md](scene-preparation.md)。

`scene-soak` 的打开判定和整个游戏稳态绝对值读取 `scene-operations.jsonl`，复用有效打开分组和稳态读数；帧耗时、8× 倍速和增长使用内嵌汇总。节点稳态增量和退出存活堆保持 `unknown`，不读帧和 GC 旁车，也不用峰值或重置堆替代。“两小时不得持续增长”尚未定义判定规则，即使斜率和时长齐全也为 `unknown`；报告保留已有斜率和实际时长，不把短测斜率外推成完整长测结论。

## 离线汇总

```powershell
# 单侧多轮，也适用于 runner 的 scenarios 输出目录
python scripts/measure/summarize.py <measure输出目录> --output <新汇总文件.json>
# 两侧
python scripts/measure/summarize.py <A输出目录> <B输出目录> --output <新汇总文件.json>
```

脚本按各目录的 `summary.json.runs` 定位报告；`report` 须相对此目录。绝对路径、解析后位于目录外的路径均不读取，标为未知，原因分别为 `report_absolute_path`、`report_outside_directory`。不改原始 `report.json`、`process.json`、`summary.json`；输出须为新文件。

按场景分组，输入、构建设置和参数须可比，不混存档或时长。每指标报告 `valid_runs`、`unknown_runs`、`median`、`min`、`max` 和未知原因；差值为 B 中位数减 A，百分比为差值/A×100，A 为零时未知。脚本汇总 `timing` 和旁车，不汇总 `assessment`、不维护游戏性能预算，也不判断差值是否超过波动。

| 指标 | 每轮取值 | 单位 |
| --- | --- | --- |
| `timing.frame_seconds.{p50,p95,p99,maximum}` | 报告已有分布值 | 秒 |
| `timing.simulation_seconds.{p50,p95,p99,maximum}` | 报告已有分布值 | 秒 |
| `timing.actual_speed` | 报告已有倍速值 | 倍 |
| `timing.elapsed_seconds` | 报告已有采样时长 | 秒 |

再计算跨轮中位数和范围，不拼接原始帧。

`openings.<侧>.by_operation` 按操作名合并同场景各进程的配套文件 `scene-operations.jsonl`。只纳入 `kind=open` 且 `sample_valid=true` 的记录，分别输出有效和无效样本数。点击到可操作、可操作到 Ready、读档按钮到可操作三种耗时各输出样本数、缺失数和 p50／p95／p99／最大值，单位为秒。读档按钮耗时只统计带 `load_button` 的读档记录。分位数沿用游戏报告的 nearest-rank 算法：将样本排序后取第 `ceil(p × 样本数)` 个值，不插值。脚本不检查 30 次门槛，须按样本数对照[设计 00 性能目标](../../docs/design/00-product-and-core-loop.md#本阶段性能目标)。

`scenarios.scene-node-first.memory_stability.<侧>[]` 取 `map-node` 的 Ready 至 `observation_ended` 末 10 秒；`scenarios.scene-map.memory_stability.<侧>[]` 取 `scene-frames.jsonl` 的 `final-map` 段末 10 秒，输出结构相同，共用统计：不足 10 秒保留观察值并标 `unknown`；节点须有效打开，帧须覆盖窗口两端、至少两个不同采样时刻，且私有字节完整为正。满足条件且 `(max-min)/min ≤ 1%` 标 `stable`，否则 `unknown` 并记录 `reason`。`memory` 保留各计数的样本数、最小值、分位数和最大值，`range_ratio` 为私有字节范围比；`window_start_at`／`window_end_at` 为窗口边界，`sample_span_seconds` 为窗内原始样本跨度。不插值，坏文件整份未知。只说明采样值稳定，不保证采样间无尖峰或后台预取完成；GPU、工作集不另判稳。

`hourly_memory.<侧>` 用 `scene-soak` 的 `natural-map.at`、`memory`，从首条记录起每 3600 秒分段。`runs[].hours` 的 `start_at`／`end_at` 是目标边界；`observed_start_at` 取不晚于起点的最后一条记录，`observed_end_at` 取不早于终点的第一条记录；未到终点则取末条。区间内原始样本（含端点）参与最小二乘回归，相邻小时共享边界样本，不插值。`samples`、`observed_seconds` 记录实际样本数、覆盖时长。

私有字节、工作集和渲染分配的斜率单位为 bytes/hour。未覆盖目标终点的段保留在 `runs[].hours`，标 `reason=partial_hour`、斜率 null，`by_hour` 计未知。少于两个不同时间点或缺计数也为 null。`by_hour` 汇总同一小时跨轮中位数和范围，缺失轮次计未知，不补 0。

配套文件缺失、损坏或截断时，整份文件不参与统计，原因保留在输出中。GC 占比需从有效原始报告的 `identity.scene_budget.soak.samples[]` 计算：按 `view` 和 `speed` 分组，组内 `pause_seconds` 总和除以 `seconds` 总和。该占比按上界报告，包含逐帧序列化 `scene-frames.jsonl`、世界计数等测量分配引起的回收，不从数值中扣除这些影响。主动强制回收另列于 `scene-collections.jsonl`，不计入该占比。

以上统计均排除失败的运行、失败报告、缺失或截断的 JSON、非零退出、超时和清理未确认的进程，并保留未知原因；报告旁必须有有效的 `process.json`。缺字段、`null`、`"unavailable"`、布尔值和非有限数也作未知，不补 0。JSON 中未知数值为 `null`，状态为 `unknown`；部分轮次未知时仍显示其余有效轮次的统计及未知数量。

## 规模输入

`scene-scale.py` 生成四档输入，再单独核对两档近景。生成和核对均要求源码干净。输出使用 `TestResults/` 或仓库外的新目录、空目录。

```powershell
python scripts/measure/scene-scale.py generate --output TestResults/scene-scale-inputs
python scripts/measure/scene-scale.py verify --input TestResults/scene-scale-inputs --output TestResults/scene-scale-verification
```

`generate` 构建 CLI 和 `Tools/SceneScale/`，生成 1／10／30／100× 普通存档。倍率是远处建筑占用格数的目标，清单记录实际倍率。生成只运行 bounds（读取边界）、make（CLI 制作存档）、stats（计数），不运行 ready／local 近景核对。`examples/starting-trade/recipe.json` 的命令按顺序在模拟步 0 执行，模拟步 1 保存。仓库 builtin 只读，各档修改输出目录中的副本。存档包含布局和设计副本，探针用仓库 builtin 恢复核对；生成产物不提交。

焦点为节点世界原点 `(0,0,0)`。远处建筑以 core-city-hub／bastion-fortress 组合镜像放置，两侧完整地块均须避开已有地块和焦点周围 192 格圆。100× 的追加位置也须通过这一筛选。10／30／100× 扩大地面的平坦范围（flatten），高度保持为 1 格。N／C／F 分别是圆外建筑的占用格数、包含它们的世界 32³ 区块数、六邻接暴露面数。模型地面另列，程序地形的占用 N／C／F 不可从公开投影取得。`navigation_center` 只记录真实导航范围中心。

生成清单 `scale-manifest.json` 和核对清单 `scale-verification.json` 按 `input_identity` 绑定 `recipe_sha256`、`content_tree`、`save_sha256`。`content_tree.files` 记录相对 POSIX 路径及文件字节 SHA256；树哈希对排序后的“路径 + NUL + 小写文件 SHA256 + LF”UTF-8 字节再取 SHA256。跨平台优先比较配方和内容树；P2-R/F2 四档存档字节也与本机一致。verify 检查四档标识和源码身份，记录 `generation_manifest_sha256`，仅对 1／10× 运行 ready／local 近景核对。规模路径的 [P2-R/F2 分段耗时记录及使用限制](../../docs/ci.md#linux-夜间运行)已记录。

近景核对覆盖五个相机位置：原点和 X／Z 各 ±64 格。各位置的 `firstGeometry.fully_inside` 对象集合须非空。跨档比较以节点原点为圆心、半径 192 格的圆内完整对象的非轨道三角形数、哈希及全部高度列，只排除 `RailSteel`／`Sleeper` 网格；失配即停止。`railCenterline` 将公开整条轨道的 XYZ 调试折线逐格采样，裁剪到 192 格圆，按 0.001 格量化、规范端点顺序、去重，独立于网格的 128 格分段。折线不含停车轨，须用首站地块侧向边界证明停车轨在圆外，否则记为 `unknown`。

每个位置先满足帧 Ready、192 格圆不与雾区相交、每列均返回精确高度，再等待对象集合、三角形数和几何哈希连续 30 秒不变。每位置最多等待 300 秒，超时输出 `outcome=unknown` 并停止。1／10× 均隔十秒复核，期间变化即停止。这只证明观察窗口内稳定，后台地形没有公开完成状态。

`stability_samples` 的 `firstGeometry`／`secondGeometry` 记录对象标识、位姿、范围、材质、三角形及非轨道摘要，按 `fully_inside` 和 `crossing_boundary` 汇总。组哈希使用世界坐标和材质，不含会话标识。同档稳定等待和复核检查完整网格；三角形相交检查使用半径 192.001 格的保守圆。原点不传相机参数，其余位置使用 192 格投影半径。

跨界对象不参与跨档判定；`raw_mesh_comparison` 列出完整网格差异，`local_content_match` 表示近景通过。`crossing_geometry_observations` 按位姿和范围保留快照计数、哈希、首次观测及此前最后缺席时刻。首次观测仅为发布时间上界，受相机半径影响；无相交三角形的对象不入清单，缺席不证明未发布。游戏补齐仍以 `far_ready` 实测。

失败时保留 JSON、日志和分项结果。`stage` 记录具体阶段，命令失败的 `RuntimeError` 消息写入 `failure`，`failed_observation` 指向已有失败观测 JSON。

Linux 规模输入的生成档位、运行范围和产物布局见 [CI](../../docs/ci.md#linux-夜间运行)。开发机用待验收提交运行 `verify`，只打开 1／10×，两档均作十秒复核。

## 测试与手动验证

```powershell
pwsh -NoProfile -File scripts/measure/tests/test-precheck.ps1
pwsh -NoProfile -File scripts/measure/tests/test-run-outcome.ps1
python -m unittest discover -s scripts/measure/tests -p "test_*.py" -v
```

`Tests/SceneScale/` 已接入[统一测试入口](../../docs/run-tests.md)和 CI。上列 Python 和 PowerShell 脚本测试尚未接入 CI。预检使用固定时间序列验证 30 秒覆盖、门槛边界、连续超限和缺失样本；汇总和运行判定测试使用临时目录里的真实 JSON 文件。运行判定覆盖包装器返回 0 而游戏异常退出、已完成报告之后的异常退出、游戏状态缺失和报告结束标记缺失，以及 Windows 的退出和清理检查。`test_game_process.py` 用受控子进程检查退出记录器的零退出、非零退出及信号终止；信号用例在 Windows 上跳过，Linux 上运行。编排通过真实游戏入口验证，不使用假游戏替身；记录器的子进程测试不证明 gamescope 的行为。

Linux 测量需要 `gamescope` 和 `python3`。输出文件见[测试手册](../../docs/run-tests.md#观察无关检查与窗口测量)。

不启动游戏的拒绝路径检查：沿用上面的单场景示例，传 `-CpuMeanThresholdPercent 0 -Repeat 2`。每次采样均有效且 CPU 均值大于零时，预期有 8 次预检（4 次主序列、4 次补跑）、全部 `measurement_started=false`，并返回 1。查看 `comparison.json` 确认每个计划项只有两次尝试。

完整冒烟在安静窗口里以同一导出作为 A 和 B，各跑两轮 `scene-follow-8x -DurationSeconds 2`，再汇总；检查顺序、报告、退出状态、负载日志和每侧有效轮数。每次测量都应成功启动并结束。

汇总中两侧帧耗时 p50 都应为 `valid_runs: 2`、`unknown_runs: 0`；检查每轮的 `external-load.jsonl` 是否有有效计数。Linux 采样路径通过 runner 的 `measure` workflow 运行短场景验证，同样检查负载日志。这些冒烟验证编排和采样，不代表性能收益。
