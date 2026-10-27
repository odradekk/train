# 测量辅助脚本

游戏测量入口是 `measure.ps1`，导出入口是 `export-measurement.ps1`。`measure.ps1` 默认运行无需参考文件的 `scene-route-1x`、`scene-route-8x`；其余场景按需显式选择。它们的场景和报告见 [测试手册](../../docs/run-tests.md#观察无关检查与窗口测量)。本目录的辅助脚本使用 PowerShell 7、Python 3 标准库和系统计数器，不需要 Pester 或额外包。

## 开发机交替对照

`compare-measurement.ps1` 用于 Windows 开发机上同一窗口里的快速对照，在协调者安排的安静窗口里运行。正式测量仍使用 Linux runner 的 `measure` workflow。

准备两侧已有的 Release 导出，以及各自构建的 `scene-reference` 文件。只有需要参考的场景才传参考文件；对照入口不构建、不导出、不制作参考。示例：

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

每份 `report.json` 的 `assessment` 按[设计 00「本阶段性能目标」](../../docs/design/00-product-and-core-loop.md#本阶段性能目标)逐项报告结果。`conclusion` 保持“待验收”，`reason` 说明尚缺目标配置实机与完整覆盖，`source` 指向设计目标。各项 `items` 包含 `metric`、`window`、`unit`、`rule`、`threshold`、`measured`、`status`、`reason`，需要时附 `observed_seconds`。阈值数值由报告代码单处维护。

`status` 为 `passed`、`failed` 或 `unknown`。缺字段、无效数值、缺少成对基线或观察截断均为 `unknown`；运行失败时各项均为 `unknown`。capture/reference 场景不作性能判定。`outcome` 继续表示运行是否完成，原有 `timing`、`identity` 和 `scope.final_performance=false` 保持原义；逐项通过不能代替整体验收。

路线场景的帧耗时 p95、p99、最大值只统计初始空间首个可操作时刻之后的帧。起点取 `identity.scene_budget.route.startup.first_operable_at`，与 `timing.frames[].elapsed_seconds` 共用起点；排除结束时刻小于或等于该时刻的帧，首个可操作帧本身的间隔仍属于打开过程。三项判定的 `window` 说明范围，`window_start_seconds` 记录相对 timing 起点的秒数，`excluded_frames` 记录排除的原始帧数。缺少可操作时刻或排除后没有帧时判为 `unknown`；缺少时刻时这两个字段为 null。

只排除初始点击到可操作的打开段；可操作后的远处补齐和路线途中空间切换的帧照常计入。实际倍速仍用完整路线窗口。非路线场景沿用现有采样窗口，新字段为 null；其他判定项的新字段也为 null。`timing` 的逐帧数据、全窗分布和旁车记录保持完整，只有三项帧耗时判定使用筛选后的窗口。

打开耗时按操作类别分别统计，p95 至少需要 30 次有效打开；最大值和补齐等待使用有效打开。补齐从首个可操作帧起，检查 5 秒内的 64 m 范围。`scene-memory` 的打开时延不作正式证据，不出这三项判定；打开记录仍提供节点稳态读数。每次节点打开按 `click` 配对同进程最近一次地图稳态：首次使用初始地图保持末 1 秒中位数，之后使用点击前最近的 `natural-map.memory`。存活堆使用返回地图后强制 GC 的读数减 `cycle 0` 初始读数。`scene-node`／`scene-node-first` 缺进入前基线时，增量为 `unknown`；整个游戏的稳态绝对值仍可判定。

`scene-soak` 的打开判定和整个游戏稳态绝对值读取 `scene-operations.jsonl`，复用有效打开分组和稳态读数；帧耗时、8× 倍速和增长使用内嵌汇总。节点稳态增量和退出存活堆保持 `unknown`，不读帧和 GC 旁车，也不用峰值或重置堆替代。“两小时不得持续增长”尚未定义判定规则，即使斜率和时长齐全也为 `unknown`；报告保留已有斜率和实际时长，不把短测斜率外推成完整长测结论。

## 离线汇总

```powershell
# 单侧多轮，也适用于 runner 的 scenarios 输出目录
python scripts/measure/summarize.py <measure输出目录> --output <新汇总文件.json>
# 两侧
python scripts/measure/summarize.py <A输出目录> <B输出目录> --output <新汇总文件.json>
```

脚本通过每个目录的 `summary.json.runs` 定位报告；`report` 必须是相对该目录的路径。绝对路径和解析后指向目录外的路径均为未知，原因分别为 `report_absolute_path` 和 `report_outside_directory`，不会读取这些文件。脚本不改原始 `report.json`、`process.json` 或 `summary.json`；汇总输出须为新文件。

只按场景分组。调用者负责保证输入、构建设置和测量参数可比，不能把不同存档或不同时长的轮次混在同一个场景里作比较。每个指标报告 `valid_runs`、`unknown_runs`、`median`、`min`、`max` 和未知原因；两侧差值是 B 中位数减 A 中位数，百分比是差值除以 A 中位数再乘 100。A 为零时百分比未知。脚本汇总下表的 `timing` 数值和以下旁车数据，不汇总 `assessment`，不维护性能阈值，也不自动判断差值是否超过波动。

| 指标 | 每轮取值 | 单位 |
| --- | --- | --- |
| `timing.frame_seconds.{p50,p95,p99,maximum}` | 报告已有分布值 | 秒 |
| `timing.simulation_seconds.{p50,p95,p99,maximum}` | 报告已有分布值 | 秒 |
| `timing.actual_speed` | 报告已有倍速值 | 倍 |
| `timing.elapsed_seconds` | 报告已有采样时长 | 秒 |

再对上述每轮值计算跨轮中位数和范围，不拼接多轮原始帧。

`openings.<侧>.by_operation` 按操作名合并同场景各进程的配套文件 `scene-operations.jsonl`。只纳入 `kind=open` 且 `sample_valid=true` 的记录，分别输出有效和无效样本数。点击到可操作、可操作到 Ready、读档按钮到可操作三种耗时各输出样本数、缺失数和 p50／p95／p99／最大值，单位为秒。分位数沿用游戏报告的 nearest-rank 算法：将样本排序后取第 `ceil(p × 样本数)` 个值，不插值。脚本不判断是否达到 30 次，报告使用者须结合样本数对照[设计 00 性能目标](../../docs/design/00-product-and-core-loop.md#本阶段性能目标)。

`scene-soak` 另有 `hourly_memory.<侧>`，从 `natural-map` 记录的 `at` 和稳态 `memory` 计算。以每轮第一条地图稳态记录为起点，每 3600 秒分段，最后一段包含终点。例如覆盖恰好两小时，3600 秒处的记录和 7200 秒处的末条记录都归第二小时。`runs[].hours` 保留逐小时样本数、起止时间、实际覆盖秒数，以及私有字节、工作集和渲染分配的线性回归斜率，单位为 bytes/hour。少于两个不同时间点或缺计数时斜率为 null；不足一小时的观察不能当作完整一小时证据。`by_hour` 给出相同小时的跨轮中位数和范围，缺失轮次计为未知，不补 0。

配套文件缺失、损坏或截断时，整份文件不参与统计，原因保留在输出中。GC 占比需从有效原始报告的 `identity.scene_budget.soak.samples[]` 计算：按 `view` 和 `speed` 分组，组内 `pause_seconds` 总和除以 `seconds` 总和。主动强制回收另列于 `scene-collections.jsonl`，不计入该占比。

以上统计均排除失败的运行、失败报告、缺失或截断的 JSON、非零退出、超时和清理未确认的进程，并保留未知原因；报告旁必须有有效的 `process.json`。缺字段、`null`、`"unavailable"`、布尔值和非有限数也作未知，不补 0。JSON 中未知数值为 `null`，状态为 `unknown`；部分轮次未知时仍显示其余有效轮次的统计及未知数量。

## 规模输入

`scene-scale.py` 移植 #499 的建筑组合、镜像放置及 flatten 配方，使用当前 `starting-trade` 配方的命令，在第 1 步保存。它构建 CLI 和正式探针项目 `Tools/SceneScale/`，生成 1／10／30／100× 四档普通存档：

```powershell
python scripts/measure/scene-scale.py --output TestResults/scene-scale-inputs
```

输出目录须为新目录或空目录。原始 builtin 只读，每档修改的是输出目录里的临时副本。普通存档包含布局和设计副本，四档共用同一个干净 Release 导出；探针随后通过原始 builtin 恢复这些存档，验证副本有效。生成的存档、内容树和清单不提交。

焦点统一为当前节点首次打开时的世界原点 `(0,0,0)`。建筑放置排除原有地块和焦点周围 192 格近景圆，镜像两侧都须符合。N／C／F 分别为近景圆外建筑的占用格数、包含它们的世界 32³ 区块数和六邻接暴露面数，场景模型地面另列；程序地形的占用 N／C／F 不可从当前公开投影取得。档位表示构造目标，清单记录实际倍率。

清单记录源码、程序集、builtin 和存档哈希、实际 N／C／F、近景占用指纹及局部几何和高度核对。五个相机位置覆盖原点和 X／Z 各 ±64 格，跨档比较完全在 192 格圆内的对象、轨道中心线和全部高度列；跨界远处对象只记录。任一近景核对失配就停止，不自动调整布局或参数。可平移范围中心只作导航边界数据。

轨道跨档比较使用 `railCenterline`：从 Scenes 公开的整条轨道 XYZ 调试折线取得每格采样，将线段裁剪至 192 格圆，按 0.001 格量化、规范端点顺序并去重。它独立于轨道网格的 128 格分段。调试折线不含停车轨，探针须以首个车站地块的侧向边界证明停车轨都在圆外；不能证明时记为 `unknown`。各相机的 `firstGeometry.fully_inside` 非轨道三角形数和哈希须一致，只排除材质为 `RailSteel`／`Sleeper` 的网格。高度仍须完全一致。原网格哈希和计数保留，`raw_mesh_comparison` 单列包含跨界远处对象在内的原始差异；`local_content_match` 表示近景核对通过。

每个位置先满足帧 Ready、192 格圆内没有相交雾区、全部近景高度列返回精确值，再等待参与近景核对的对象集合、三角形数和几何哈希连续 30 秒不变。每位置最多等待 300 秒，超时输出 `outcome=unknown` 并停止。1×、100× 在收集后再隔十秒复核；期间变化就停止。后台地形构建没有公开的完成状态，这个条件只证明观察窗口内稳定。

`stability_samples` 中的 `firstGeometry`／`secondGeometry` 按对象记录公开标识、世界位姿和范围、材质及近景三角形摘要，并分别汇总 `fully_inside`（对象几何全部在 192 格圆内）和 `crossing_boundary`（参与核对但越过圆边界）两组的三角形数和哈希；两组也各自记录非轨道几何摘要。组哈希使用原来的世界坐标和材质，不含会话对象标识，便于跨档比较。同档的稳定等待和十秒复核仍检查完整原始网格。三角形相交判定仍使用原配方的 192.001 格保守范围。失败时保留探针 JSON 和日志，清单指向失败记录；跨档失配时也保留该档的分项结果。

`crossing_geometry_observations` 按世界位姿和范围对照跨界对象，逐快照保留计数、哈希及首次观测和此前最后缺席的时刻。首次观测只给出发布时间上界；局部清单省略不含相交三角形的对象，缺席不能证明尚未发布。探针不是游戏的帧循环，游戏里的远处补齐以 `far_ready` 实测为准。

Linux workflow 的 `measure_input_set=scene-scale` 生成四档后串行运行各档参考和所选场景；正式选择为 `scene-node-first`。每档结果放在 `scale-<倍率>x/`，清单和普通存档保留在 `scale-inputs/`，临时 builtin 在运行结束时清理。`example` 输入集保持原有 `examples/` 文件限制。

## 测试与手动验证

```powershell
pwsh -NoProfile -File scripts/measure/tests/test-precheck.ps1
pwsh -NoProfile -File scripts/measure/tests/test-run-outcome.ps1
python -m unittest discover -s scripts/measure/tests -p "test_*.py" -v
```

测试不接入 CI。预检使用固定时间序列验证 30 秒覆盖、门槛边界、连续超限和缺失样本；汇总和运行判定测试使用临时目录里的真实 JSON 文件。运行判定覆盖包装器返回 0 而游戏异常退出、已完成报告之后的异常退出、游戏状态缺失和报告结束标记缺失，以及 Windows 原有条件。`test_game_process.py` 用受控子进程检查退出记录器的零退出、非零退出及信号终止；信号用例在 Windows 上跳过，Linux 上运行。编排通过真实游戏入口验证，不使用假游戏替身；记录器的子进程测试不证明 gamescope 的行为。

Linux 测量需要 `gamescope` 和 `python3`。输出文件见[测试手册](../../docs/run-tests.md#观察无关检查与窗口测量)。

不启动游戏的拒绝路径检查：沿用上面的单场景示例，传 `-CpuMeanThresholdPercent 0 -Repeat 2`。每次采样均有效且 CPU 均值大于零时，预期有 8 次预检（4 次主序列、4 次补跑）、全部 `measurement_started=false`，并返回 1。查看 `comparison.json` 确认每个计划项只有两次尝试。

```text
A/scene-follow-8x/1 attempt=1 precheck=busy started=False succeeded=False
B/scene-follow-8x/1 attempt=1 precheck=busy started=False succeeded=False
A/scene-follow-8x/2 attempt=1 precheck=busy started=False succeeded=False
B/scene-follow-8x/2 attempt=1 precheck=busy started=False succeeded=False
A/scene-follow-8x/1 attempt=2 precheck=busy started=False succeeded=False
B/scene-follow-8x/1 attempt=2 precheck=busy started=False succeeded=False
A/scene-follow-8x/2 attempt=2 precheck=busy started=False succeeded=False
B/scene-follow-8x/2 attempt=2 precheck=busy started=False succeeded=False
```

完整冒烟在安静窗口里以同一导出作为 A 和 B，各跑两轮 `scene-follow-8x -DurationSeconds 2`，再汇总；检查顺序、报告、退出状态、负载日志和每侧有效轮数。预期成功输出如下：

```text
A/scene-follow-8x/1 attempt=1 precheck=passed started=True succeeded=True
B/scene-follow-8x/1 attempt=1 precheck=passed started=True succeeded=True
A/scene-follow-8x/2 attempt=1 precheck=passed started=True succeeded=True
B/scene-follow-8x/2 attempt=1 precheck=passed started=True succeeded=True
```

汇总中两侧帧耗时 p50 都应为 `valid_runs: 2`、`unknown_runs: 0`；检查每轮的 `external-load.jsonl` 是否有有效计数。Linux 采样路径通过 runner 的 `measure` workflow 运行短场景验证，同样检查负载日志。这些冒烟验证编排和采样，不代表性能收益。
