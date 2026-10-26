# 测量辅助脚本

游戏测量入口是 `measure.ps1`，导出入口是 `export-measurement.ps1`。它们的场景和报告见 [测试手册](../../docs/run-tests.md#观察无关检查与窗口测量)。本目录的辅助脚本使用 PowerShell 7、Python 3 标准库和系统计数器，不需要 Pester 或额外包。

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

## 离线汇总

```powershell
# 单侧多轮，也适用于 runner 的 scenarios 输出目录
python scripts/measure/summarize.py <measure输出目录> --output <新汇总文件.json>
# 两侧
python scripts/measure/summarize.py <A输出目录> <B输出目录> --output <新汇总文件.json>
```

脚本通过每个目录的 `summary.json.runs` 定位报告；`report` 必须是相对该目录的路径。绝对路径和解析后指向目录外的路径均为未知，原因分别为 `report_absolute_path` 和 `report_outside_directory`，不会读取这些文件。脚本不改原始 `report.json`、`process.json` 或 `summary.json`；汇总输出须为新文件。

只按场景分组。调用者负责保证输入、构建设置和测量参数可比，不能把不同存档或不同时长的轮次混在同一个场景里作比较。每个指标报告 `valid_runs`、`unknown_runs`、`median`、`min`、`max` 和未知原因；两侧差值是 B 中位数减 A 中位数，百分比是差值除以 A 中位数再乘 100。A 为零时百分比未知。脚本不判断是否达标，也不自动判断差值是否超过波动。

| 指标 | 每轮取值 | 单位 |
| --- | --- | --- |
| `timing.frame_seconds.{p50,p95,p99,maximum}` | 报告已有分布值 | 秒 |
| `timing.simulation_seconds.{p50,p95,p99,maximum}` | 报告已有分布值 | 秒 |
| `timing.{actual_speed,window_median_actual_speed,window_minimum_actual_speed}` | 报告已有倍速值 | 倍 |
| `timing.elapsed_seconds` | 报告已有采样时长 | 秒 |
| `memory.{working_set_bytes,private_bytes,peak_working_set_bytes,godot_static_bytes,video_memory_bytes}.final` | 最后一个内存样本 | 字节 |
| 同上内存计数器的 `.peak` | 本轮所有内存样本的最大值 | 字节 |
| `operations.<name>.duration_seconds` | 同名操作在本轮的时长中位数 | 秒 |

再对上述每轮值计算跨轮中位数和范围，不拼接多轮原始帧。同名操作只要有一次失败或时长不可用，该轮这个操作就是未知；新增操作名自动参与汇总。内存 `peak` 需要全部样本有效；较早样本不可用不影响有效的末尾样本，末尾样本不可用时 `final` 为未知。这些内存值不表示节点稳态增量。

失败的运行、失败报告、缺失或截断的 JSON、非零退出、超时和清理未确认都作未知；报告旁必须有有效的 `process.json`。缺字段、`null`、`"unavailable"`、布尔值和非有限数也作未知，不补 0。JSON 中未知数值为 `null`，状态为 `unknown`；部分轮次未知时仍显示其余有效轮次的统计及未知数量。

## 测试与手动验证

```powershell
pwsh -NoProfile -File scripts/measure/tests/test-precheck.ps1
python -m unittest discover -s scripts/measure/tests -p "test_*.py" -v
```

测试不接入 CI。预检使用固定时间序列验证 30 秒覆盖、门槛边界、连续超限和缺失样本；汇总测试使用临时目录里的真实 JSON 文件，验证统计和错误数据处理。编排通过真实入口验证，不使用假游戏替身。

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
