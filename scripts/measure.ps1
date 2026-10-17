#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ExecutablePath,
    [Parameter(Mandatory)][string] $InputPath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateSet('map-1x', 'map-8x', 'follow-1x', 'follow-8x', 'node-1x', 'node-8x',
        'sdfgi-off', 'sdfgi-on', 'taa-off', 'taa-on', 'scene-node', 'scene-node-first',
        'scene-edge', 'scene-follow', 'scene-follow-8x', 'scene-memory', 'scene-capture', 'scene-map', 'scene-reference')]
    [string[]] $Scenario = @('map-1x', 'map-8x', 'follow-1x', 'follow-8x', 'node-1x', 'node-8x',
        'sdfgi-off', 'sdfgi-on', 'taa-off', 'taa-on'),
    [ValidateRange(1, 2147483647)][int] $Repeat = 3,
    # 启动器超时上限 2147478 秒，扣除 scene-memory 的 600 秒退出余量。
    [ValidateRange(0, 2146878)][int] $DurationSeconds = 0,
    [string] $ReferencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'checks/Invoke-HiddenProcess.ps1')

function Get-FileIdentity([string] $Path) {
    @{ path = $Path; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Write-Json([string] $Path, $Value) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8
}

$repo = Split-Path $PSScriptRoot -Parent
$runs = [System.Collections.Generic.List[object]]::new()
$summaryPath = $null
$failure = $null
$displayAwake = $null
$displayAwakeState = @{ acquired = $false; restored = $false }
try {
    if (-not $IsWindows) { throw '测量入口需要 Windows Release 导出与真实窗口。' }
    # 执行状态属于原生线程；专用线程持有到 finally，不能依赖 PowerShell 的运行线程。
    if (-not ('MeasurementDisplayAwake' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public sealed class MeasurementDisplayAwake : IDisposable
{
    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
    private readonly ManualResetEventSlim ready = new ManualResetEventSlim();
    private readonly ManualResetEventSlim stop = new ManualResetEventSlim();
    private readonly Thread thread;
    private string error;
    public MeasurementDisplayAwake()
    {
        thread = new Thread(Hold) { IsBackground = true };
        thread.Start();
        ready.Wait();
        if (error != null) { thread.Join(); throw new InvalidOperationException(error); }
    }
    private void Hold()
    {
        uint previous = SetThreadExecutionState(0x80000002);
        if (previous == 0) { error = "SetThreadExecutionState 无法保持显示器唤醒。"; ready.Set(); return; }
        ready.Set();
        try { stop.Wait(); }
        finally
        {
            if (SetThreadExecutionState(previous) == 0)
                error = "SetThreadExecutionState 无法恢复原先执行状态。";
        }
    }
    public void Dispose()
    {
        stop.Set();
        thread.Join();
        ready.Dispose();
        stop.Dispose();
        if (error != null) throw new InvalidOperationException(error);
    }
}
"@
    }
    $displayAwake = [MeasurementDisplayAwake]::new()
    $displayAwakeState.acquired = $true
    if ($Scenario.Count -eq 0) { throw '至少选择一个情景。' }
    $ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
    $InputPath = (Resolve-Path -LiteralPath $InputPath).Path
    if ($ReferencePath) { $ReferencePath = (Resolve-Path -LiteralPath $ReferencePath).Path }
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $InputPath -PathType Leaf)) { throw '可执行文件与输入存档必须是文件。' }
    $OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    if (Test-Path -LiteralPath $OutputDirectory) {
        if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or
            @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
            throw '输出目录必须是新目录或空目录。'
        }
    }
    else { [System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null }
    $summaryPath = Join-Path $OutputDirectory 'summary.json'

    $exportDirectory = Split-Path $ExecutablePath -Parent
    $pck = [System.IO.Path]::ChangeExtension($ExecutablePath, '.pck')
    if (-not (Test-Path -LiteralPath $pck -PathType Leaf)) { throw "缺少旁置 PCK：$pck" }
    $assemblies = @(Get-ChildItem -LiteralPath $exportDirectory -Filter Train.dll -File -Recurse)
    if ($assemblies.Count -ne 1) { throw '导出目录必须包含唯一的 Train.dll。' }

    # 短命进程读取元数据，避免同一终端复测不同构建时程序集身份冲突或 DLL 锁定。
    $build = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -Command {
        param($path)
        $ErrorActionPreference = 'Stop'
        [Reflection.Assembly]::LoadFrom($path).GetCustomAttributes(
            [Reflection.AssemblyInformationalVersionAttribute], $false)[0].InformationalVersion
    } -args $assemblies[0].FullName
    if ($LASTEXITCODE -ne 0) { throw '无法读取 Train.dll 构建版本。' }
    if ($build -notmatch '\+([0-9a-f]{40})$') { throw "程序集版本未包含完整 Git 提交：$build" }
    $commit = $Matches[1]
    $apiBlob = & git -C $repo rev-parse "${commit}:docs/contracts/public-api.txt"
    if ($LASTEXITCODE -ne 0) { throw '无法读取构建提交的 public-api Git blob。' }
    $identity = @{
        build_commit = $commit
        public_api_git_blob = $apiBlob
        executable = Get-FileIdentity $ExecutablePath
        pck = Get-FileIdentity $pck
        assembly = Get-FileIdentity $assemblies[0].FullName
        input = Get-FileIdentity $InputPath
        system = @{
            os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            processors = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
            physical_memory_bytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
            display_adapters = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, PNPDeviceID)
        }
    }

    foreach ($name in ($Scenario | Select-Object -Unique)) {
        $isScene = $name.StartsWith('scene-', [StringComparison]::Ordinal)
        $timeout = if ($name -eq 'scene-memory') {
            if ($DurationSeconds -eq 0) { 7800 } else { $DurationSeconds + 600 }
        } elseif ($isScene) { 600 } else { 180 }
        for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
            $runDirectory = Join-Path $OutputDirectory "$name-$iteration"
            [System.IO.Directory]::CreateDirectory($runDirectory) | Out-Null
            $reportPath = Join-Path $runDirectory 'report.json'
            $identityPath = Join-Path $runDirectory 'identity.json'
            $loadPath = Join-Path $runDirectory 'external-load.jsonl'
            $stopPath = Join-Path $runDirectory 'sampler.stop'
            $readyPath = Join-Path $runDirectory 'sampler.ready'
            Write-Json $identityPath $identity
            $run = @{ scenario = $name; repeat = $iteration; report = $reportPath; external_load = $loadPath; succeeded = $false }
            $runs.Add($run)
            $previousAppData = $env:APPDATA
            $previousLocalAppData = $env:LOCALAPPDATA
            $job = $null
            try {
                $env:APPDATA = Join-Path $runDirectory 'appdata'
                $env:LOCALAPPDATA = Join-Path $runDirectory 'localappdata'
                [System.IO.Directory]::CreateDirectory($env:APPDATA) | Out-Null
                [System.IO.Directory]::CreateDirectory($env:LOCALAPPDATA) | Out-Null
                $job = Start-Job -ArgumentList $loadPath, $stopPath, $readyPath -ScriptBlock {
                    param($loadPath, $stopPath, $readyPath)
                    $ErrorActionPreference = 'Stop'
                    while (-not (Test-Path -LiteralPath $stopPath)) {
                        $cpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
                        $memory = Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory
                        $processes = @(Get-Process | Where-Object { $_.ProcessName -like 'Godot*' -or $_.ProcessName -eq 'dotnet' } |
                            ForEach-Object { @{ pid = $_.Id; name = $_.ProcessName; cpu_seconds = $_.CPU; working_set_bytes = $_.WorkingSet64 } })
                        @{ utc = [DateTime]::UtcNow.ToString('O'); total_cpu_percent = $cpu.PercentProcessorTime; processes = $processes
                            available_bytes = $memory.AvailableBytes; pages_input_per_sec = $memory.PagesInputPerSec
                            page_reads_per_sec = $memory.PageReadsPerSec } |
                            ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath $loadPath -Encoding utf8
                        [System.IO.File]::WriteAllText($readyPath, '')
                        Start-Sleep -Seconds 1
                    }
                }
                $readyDeadline = [DateTime]::UtcNow.AddSeconds(15)
                while (-not (Test-Path -LiteralPath $readyPath)) {
                    if ($job.State -eq 'Failed' -or [DateTime]::UtcNow -gt $readyDeadline) { throw '外部负载采样未能启动。' }
                    Start-Sleep -Milliseconds 100
                }
                $arguments = @(
                    '--windowed', '--resolution', '1920x1080', '--log-file', (Join-Path $runDirectory 'godot.log'), '--',
                    "--measure=$name", '--measure-input', $InputPath, '--measure-output', $reportPath, '--measure-identity', $identityPath)
                if ($isScene -or $DurationSeconds -gt 0) { $arguments += @('--measure-duration', $DurationSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)) }
                if ($ReferencePath) { $arguments += @('--measure-reference', $ReferencePath) }
                $result = Invoke-HiddenProcess -FilePath $ExecutablePath -TimeoutSeconds $timeout -ArgumentList $arguments
                $result.Output | Set-Content -LiteralPath (Join-Path $runDirectory 'process.log') -Encoding utf8
                $godotLog = Join-Path $runDirectory 'godot.log'
                $godotLines = @(if (Test-Path -LiteralPath $godotLog -PathType Leaf) {
                    Get-Content -LiteralPath $godotLog | Where-Object { $_ -match '(?i)WASAPI|AUDCLNT_E_DEVICE_INVALIDATED' }
                })
                $processLines = @($result.Output -split '\r?\n' |
                    Where-Object { $_ -match '(?i)WASAPI|AUDCLNT_E_DEVICE_INVALIDATED' })
                Write-Json (Join-Path $runDirectory 'process.json') @{
                    exit_code = $result.ExitCode; timed_out = $result.TimedOut
                    cleanup_succeeded = $result.CleanupSucceeded; duration_ms = $result.DurationMs
                    process_diagnostics = @{ 'godot.log' = $godotLines; 'process.log' = $processLines }
                }
                if ($result.ExitCode -ne 0 -or $result.TimedOut -or -not $result.CleanupSucceeded) { throw "$name/$iteration 进程失败或超时。" }
                if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "$name/$iteration 未生成报告。" }
                $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
                if ($report.outcome -ne 'completed' -or $report.identity.build_configuration -ne 'ExportRelease' -or
                    @($report.timing.frames).Count -eq 0 -or $null -eq $report.timing.frames -or
                    $report.scope.final_performance -ne $false) { throw "$name/$iteration 报告未完成、不是 Release 或缺少真实绘制帧。" }
            }
            finally {
                $env:APPDATA = $previousAppData
                $env:LOCALAPPDATA = $previousLocalAppData
                if ($null -ne $job) {
                    [System.IO.File]::WriteAllText($stopPath, '')
                    try {
                        $finished = Wait-Job -Job $job -Timeout 15
                        if ($null -eq $finished) { Stop-Job -Job $job; throw '外部负载采样未能及时停止。' }
                        Receive-Job -Job $job -ErrorAction Stop | Out-Null
                        if ($job.State -ne 'Completed') { throw '外部负载采样失败。' }
                    }
                    finally { Remove-Job -Job $job -Force }
                }
            }
            $run.succeeded = $true
            Write-Host "$name/$iteration 完成：$reportPath"
        }
    }
}
catch { $failure = $_.Exception.Message }
finally {
    if ($null -ne $displayAwake) {
        try { $displayAwake.Dispose(); $displayAwakeState.restored = $true }
        catch { $failure = @($failure, $_.Exception.Message) | Where-Object { $_ } | Join-String -Separator '; ' }
    }
    if ($null -ne $summaryPath) {
        Write-Json $summaryPath @{
            succeeded = $null -eq $failure; error = $failure; runs = @($runs.ToArray())
            display_awake = $displayAwakeState
            scope = @{
                final_performance = $false
                warmup_seconds = 5; sample_seconds = $(if ($DurationSeconds -gt 0) { $DurationSeconds } else { 30 }); repetitions = $Repeat
                scene_duration_seconds_argument = $DurationSeconds
                scene_timing = '新 scene 情景的预热、采样和运行时长以各运行报告为准；DurationSeconds=0 使用情景默认值。'
                external_load = '每秒记录总 CPU、可用物理内存、页面输入与读取速率及 Godot/dotnet 的 PID、名称、累计 CPU 秒、工作集；保留并发负载，不要求暂停其他任务。'
                limitation = '开发机真实窗口测量，仅用于重复采样与证据核对，不代表目标硬件最终性能。'
            }
        }
    }
}
if ($null -ne $failure) { Write-Error $failure -ErrorAction Continue; exit 1 }
Write-Host "测量完成：$summaryPath"
exit 0
