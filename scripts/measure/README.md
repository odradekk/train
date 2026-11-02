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

`report.json.assessment` 按[设计 00](../../docs/design/00-product-and-core-loop.md#本阶段性能目标)逐项报告结果。`conclusion` 保持“待验收”，`reason` 说明缺目标配置实机与完整覆盖。`items` 包含 `metric`、`window`、`unit`、`rule`、`threshold`、`measured`、`status`、`reason`，可附 `observed_seconds`、`excess`。单轮阈值在 `MeasurementAssessment`，长测离线及合并阈值也在 `memory_assessment.py`。

`status` 为 `passed`、`failed`、`unknown` 或 `recorded`。所有场景的 Linux `game_working_set` 只记录数值，状态为 `recorded`，不提供阈值和超出量；3 GiB 目标留到 Windows 判定。C# 按测量进程系统区分，离线脚本按 `identity.os` 区分。缺字段、无效值、缺配对、截断或运行失败仍为 `unknown`。capture/reference 不判定；`outcome` 只表示运行是否完成，逐项通过不能代替整体验收。

路线帧耗时三项排除结束时刻≤`identity.scene_budget.route.startup.first_operable_at` 的帧；该字段与 `timing.frames[].elapsed_seconds` 同起点，首个可操作帧仍属打开。`window_start_seconds` 记录起点，`excluded_frames` 记录排除数；缺时刻或后续帧则 `unknown`，缺时刻时两字段为 null。计入可操作后补齐和途中切换；实际倍速仍用全窗。原始 `timing` 和旁车保留，[场景准备](scene-preparation.md)另记排除数。

打开 p95 同类至少 30 个有效样本；最大值和补齐也只用有效打开。补齐从可操作起检查 5 秒内的 64 m 范围。`scene-memory` 节点只观察约 5 秒，不作正式时延或稳态证据。节点按 `click` 配对最近地图读数：首次取初始地图末 1 秒中位数，之后取最近 `natural-map.memory`；缺基线则增量 unknown。返回地图强制 GC 堆减 `cycle 0` 初值。`steady` 仅为末 1 秒统计，须另核对稳定性。

场景准备的判定、旁车与汇总见 [scene-preparation.md](scene-preparation.md)。

`scene-soak` 的绝对内存取所有已记录稳态中位数的最大值，含打开的 `steady`、`natural-map.memory` 和初始／最终／编辑器稳态，不取峰值。节点增量仍缺配对。`retired_heap_delta` 只取内嵌 `soak.memory.reset_heaps` 中 `phase=reset` 的 `live_bytes`，各段减第 1 段，最大增量 ≤100 MiB；`final` 状态不同，不参与。须覆盖全部分段且时长≥7200 秒。`private_growth` 用下述第 2 小时 OLS，单轮 ≤200 MiB/h，`observed_seconds` 和 `window_start_seconds` 记录实际窗口。工作集／渲染斜率仅保留观测值；`scene-memory` 不生成增长判定。

open 记录的 `node_voxel_cells` 保存节点点击前统计的格数（非负整数）：逐实例累计模板模型、建筑、车辆（含停放车辆）和生物的完整内容模型，不含程序地形、轨道和道床。节点私有字节增量沿用上文的地图配对要求：`scene-memory` 有配对，其余节点场景缺基线时仍为 `unknown`。每次有效节点打开的私有字节增量预算为 1 GiB × max(1, 节点格数 ÷ 标准世界基准格数)。`node_private_delta` 条目选取“增量−预算”最大的一次打开：`measured` 为该次增量，`threshold` 为其预算，`excess` 为 max(0, 增量−预算)；`node_voxel_cells` 是这次打开的格数，`reference_node_voxel_cells` 是标准世界基准格数。任一有效节点打开缺少有效格数或内存配对时，条目为 `unknown`。缓冲区加纹理增量 ≤512 MiB、整个游戏私有字节 ≤4 GiB，两项不按规模放宽。计数方法与基准身份见[节点体素基准](baselines/2026-11-02-node-voxel-budget.md)。

## 离线汇总

```powershell
# 单侧多轮，也适用于 runner 的 scenarios 输出目录
python scripts/measure/summarize.py <measure输出目录> --output <新汇总文件.json>
# 两侧
python scripts/measure/summarize.py <A输出目录> <B输出目录> --output <新汇总文件.json>
```

脚本按各目录的 `summary.json.runs` 定位报告；`report` 须相对此目录。绝对路径、解析后位于目录外的路径均不读取，标为未知，原因分别为 `report_absolute_path`、`report_outside_directory`。不改原始 `report.json`、`process.json`、`summary.json`；输出须为新文件。

按场景分组，输入、构建设置和参数须可比，不混存档或时长。统计报告有效／未知轮数、中位数和范围。差值为 B 中位数减 A，百分比为差值/A×100，A 为零时未知。长测内存按下述规则复判；其余指标不合并旧 `assessment`，也不判断差值是否超过波动。

| 指标 | 每轮取值 | 单位 |
| --- | --- | --- |
| `timing.frame_seconds.{p50,p95,p99,maximum}` | 报告已有分布值 | 秒 |
| `timing.simulation_seconds.{p50,p95,p99,maximum}` | 报告已有分布值 | 秒 |
| `timing.actual_speed` | 报告已有倍速值 | 倍 |
| `timing.elapsed_seconds` | 报告已有采样时长 | 秒 |

不拼接原始帧。

`openings.<侧>.by_operation` 按操作名合并同场景各进程的配套文件 `scene-operations.jsonl`。只纳入 `kind=open` 且 `sample_valid=true` 的记录，分别输出有效和无效样本数。点击到可操作、可操作到 Ready、读档按钮到可操作三种耗时各输出样本数、缺失数和 p50／p95／p99／最大值，单位为秒。读档按钮耗时只统计带 `load_button` 的读档记录。分位数沿用游戏报告的 nearest-rank 算法：将样本排序后取第 `ceil(p × 样本数)` 个值，不插值。脚本不检查 30 次门槛，须按样本数对照[设计 00 性能目标](../../docs/design/00-product-and-core-loop.md#本阶段性能目标)。

`scenarios.scene-node-first.memory_stability.<侧>[]` 取 `map-node` 的 Ready 至 `observation_ended` 末 10 秒；`scenarios.scene-map.memory_stability.<侧>[]` 取 `scene-frames.jsonl` 的 `final-map` 段末 10 秒，输出结构相同，共用统计：不足 10 秒保留观察值并标 `unknown`；节点须有效打开，帧须覆盖窗口两端、至少两个不同采样时刻，且私有字节完整为正。满足条件且 `(max-min)/min ≤ 1%` 标 `stable`，否则 `unknown` 并记录 `reason`。`memory` 保留各计数的样本数、最小值、分位数和最大值，`range_ratio` 为私有字节范围比；`window_start_at`／`window_end_at` 为窗口边界，`sample_span_seconds` 为窗内原始样本跨度。不插值，坏文件整份未知。只说明采样值稳定，不保证采样间无尖峰或后台预取完成；GPU、工作集不另判稳。

`hourly_memory.<侧>` 用 `natural-map.at`、`memory`，从首条记录起每 3600 秒分段。`runs[].hours` 的 `start_at`／`end_at` 是目标边界，观察端点分别取不晚于起点的最后一条、不早于终点的第一条记录；未到终点取末条。区间内原始样本含端点作 OLS，相邻小时共享边界，不插值；保留实际样本数和时长。

斜率单位 bytes/hour。未覆盖终点的段保留，标 `reason=partial_hour`、斜率 null；不足两个不同时间点或缺计数也为 null。`by_hour` 汇总同一小时跨轮中位数和范围，缺轮计未知，不补 0。

`scene-soak.memory_assessment.<侧>` 从原始报告和旁车复判。`runs` 保留单轮结果；`retired_heap_delta` 要求每轮 ≤100 MiB，`private_growth_median` 要求第 2 小时斜率中位数 ≤100 MiB/h，`private_growth_maximum` 要求每轮 ≤200 MiB/h。有效轮不足 3 一律 `unknown`，多于 3 轮全部纳入。至少 3 轮时，最大值遇已知超限可判失败；有未知轮的中位数只有全部轮次中超过半数已知超限才判失败，否则 `unknown`。未知轮阻止通过。

配套文件缺失、损坏或截断时整份未知，保留原因。GC 占比取有效报告 `identity.scene_budget.soak.samples[]`，按 `view`／`speed` 分组，Σ`pause_seconds`/Σ`seconds`；这是包含逐帧序列化、世界计数等测量分配的上界，不扣除测量影响。主动 GC 另记于 `scene-collections.jsonl`，不计入占比。

以上统计均排除失败的运行、失败报告、缺失或截断的 JSON、非零退出、超时和清理未确认的进程，并保留未知原因；报告旁必须有有效的 `process.json`。缺字段、`null`、`"unavailable"`、布尔值和非有限数也作未知，不补 0。JSON 中未知数值为 `null`，状态为 `unknown`；部分轮次未知时仍显示其余有效轮次的统计及未知数量。

## 规模输入

规模输入的生成、近景核对和结果说明见[规模输入](scale-inputs.md)。

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
