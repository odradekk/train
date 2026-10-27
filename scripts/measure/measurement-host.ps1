#Requires -Version 7.0
# Decision note: 2026-10-26-performance-measurement-system

$MeasurementCpuWindowSeconds = 30

function Assert-MeasurementRunOutcome($Result, [string] $RunDirectory, [switch] $Gamescope) {
    $godotLog = Join-Path $RunDirectory 'godot.log'
    $godotLines = @(if (Test-Path -LiteralPath $godotLog -PathType Leaf) {
        Get-Content -LiteralPath $godotLog | Where-Object { $_ -match '(?i)WASAPI|AUDCLNT_E_DEVICE_INVALIDATED' }
    })
    $processLines = @($Result.Output -split '\r?\n' |
        Where-Object { $_ -match '(?i)WASAPI|AUDCLNT_E_DEVICE_INVALIDATED' })
    $process = @{
        exit_code = $Result.ExitCode; timed_out = $Result.TimedOut
        cleanup_succeeded = $Result.CleanupSucceeded; duration_ms = $Result.DurationMs
        process_diagnostics = @{ 'godot.log' = $godotLines; 'process.log' = $processLines }
    }
    $problems = [System.Collections.Generic.List[string]]::new()
    if ($Result.ExitCode -ne 0) { $problems.Add("启动进程退出码为 $($Result.ExitCode)。") }
    if ($Result.TimedOut) { $problems.Add('进程超时。') }
    if (-not $Result.CleanupSucceeded) { $problems.Add('进程清理未确认成功。') }
    if ($Gamescope) {
        $process.launcher_exit_code = $Result.ExitCode
        $process.exit_code = $null
        $process.signal = $null
        try {
            $game = Get-Content -LiteralPath (Join-Path $RunDirectory 'game-process.json') -Raw -ErrorAction Stop |
                ConvertFrom-Json -AsHashtable -ErrorAction Stop
            if ($game -isnot [System.Collections.IDictionary] -or
                ($game['exit_code'] -isnot [long] -and $game['exit_code'] -isnot [int]) -or
                -not $game.Contains('signal')) { throw '无效的游戏退出状态。' }
            if ($game.exit_code -lt 0) {
                if ($game.signal['number'] -ne -$game.exit_code -or
                    $game.signal['name'] -isnot [string] -or -not $game.signal.name) { throw '无效的退出信号。' }
            } elseif ($null -ne $game.signal) { throw '正常退出不能带信号。' }
            $process.exit_code = $game.exit_code
            $process.signal = $game.signal
            if ($game.exit_code -lt 0) { $problems.Add("游戏被信号 $($game.signal.name)($($game.signal.number)) 终止。") }
            elseif ($game.exit_code -ne 0) { $problems.Add("游戏退出码为 $($game.exit_code)。") }
        } catch { $problems.Add('游戏退出状态缺失或无法读取。') }
    }
    $process | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $RunDirectory 'process.json') -Encoding utf8
    $reportPath = Join-Path $RunDirectory 'report.json'
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { $problems.Add('未生成报告，缺少结束标记。') }
    else {
        try {
            $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -AsHashtable
            if ($report -isnot [System.Collections.IDictionary]) { throw '无效的报告。' }
        } catch { $problems.Add('报告无法解析，缺少结束标记。'); $report = $null }
        if ($null -ne $report) {
            if ($report['outcome'] -ne 'completed') { $problems.Add('报告未完成，缺少 completed 结束标记。') }
            try {
                if ($report.identity.build_configuration -ne 'ExportRelease' -or
                    @($report.timing.frames).Count -eq 0 -or $null -eq $report.timing.frames -or
                    $report.scope.final_performance -ne $false) { throw '无效的报告内容。' }
            } catch { $problems.Add('报告不是 Release、缺少真实绘制帧或 scope 无效。') }
        }
    }
    if ($problems.Count -gt 0) { throw ($problems -join '；') }
}

function New-MeasurementLoadState {
    $state = @{ previous_cpu = $null; counters = @{} }
    if ($IsWindows) {
        try {
            foreach ($entry in @(
                @('total_cpu_percent', 'Processor', '% Processor Time', '_Total'),
                @('available_bytes', 'Memory', 'Available Bytes', ''),
                @('pages_input_per_sec', 'Memory', 'Pages Input/sec', ''),
                @('page_reads_per_sec', 'Memory', 'Page Reads/sec', ''))) {
                $counter = [System.Diagnostics.PerformanceCounter]::new($entry[1], $entry[2], $entry[3])
                $state.counters[$entry[0]] = $counter
                # CPU 速率需要两次读数；初始化的零值不是空闲样本。
                $null = $counter.NextValue()
            }
        } catch {
            Close-MeasurementLoadState $state
            throw
        }
    }
    return $state
}

function Close-MeasurementLoadState([hashtable] $State) {
    if ($null -ne $State) {
        foreach ($counter in $State.counters.Values) { $counter.Dispose() }
    }
}

function Get-MeasurementLoadSample([hashtable] $State) {
    $record = @{ utc = [DateTime]::UtcNow.ToString('O') }
    if ($IsWindows) {
        foreach ($entry in $State.counters.GetEnumerator()) {
            $value = $entry.Value.NextValue()
            if (-not [double]::IsFinite($value) -or $value -lt 0) { throw '性能计数器返回无效值。' }
            $record[$entry.Key] = [double] $value
        }
    } else {
        # /proc/stat 首行是全部 CPU 的累计时钟数；第 4、5 项为空闲与 I/O 等待。
        $ticks = @((Get-Content -LiteralPath /proc/stat -TotalCount 1) -split '\s+' |
            Select-Object -Skip 1 -First 8 | ForEach-Object { [long] $_ })
        $record.total_cpu_percent = $null
        if ($null -ne $State.previous_cpu) {
            $total = ($ticks | Measure-Object -Sum).Sum - ($State.previous_cpu | Measure-Object -Sum).Sum
            $idle = $ticks[3] + $ticks[4] - $State.previous_cpu[3] - $State.previous_cpu[4]
            if ($total -gt 0) { $record.total_cpu_percent = 100 * ($total - $idle) / $total }
        }
        $State.previous_cpu = $ticks
        $record.available_bytes = [long](Select-String -LiteralPath /proc/meminfo -Pattern '^MemAvailable:\s+(\d+) kB$').Matches[0].Groups[1].Value * 1024
        $temperatures = @(Get-ChildItem /sys/class/thermal -Filter 'thermal_zone*' -ErrorAction SilentlyContinue |
            ForEach-Object { Get-Content -LiteralPath (Join-Path $_.FullName 'temp') -ErrorAction SilentlyContinue } |
            ForEach-Object { [double] $_ / 1000 })
        $record.max_thermal_celsius = ($temperatures | Measure-Object -Maximum).Maximum
        $record.ac_online = @(Get-ChildItem /sys/class/power_supply -ErrorAction SilentlyContinue |
            Where-Object { (Get-Content -LiteralPath (Join-Path $_.FullName 'type') -ErrorAction SilentlyContinue) -eq 'Mains' } |
            ForEach-Object { (Get-Content -LiteralPath (Join-Path $_.FullName 'online')) -eq '1' })
    }
    $record.processes = @(Get-Process | Where-Object {
        $_.ProcessName -like 'Godot*' -or $_.ProcessName -like 'train*' -or $_.ProcessName -eq 'dotnet'
    } | ForEach-Object {
        try { @{ pid = $_.Id; name = $_.ProcessName; cpu_seconds = $_.CPU; working_set_bytes = $_.WorkingSet64 } }
        finally { $_.Dispose() }
    })
    return $record
}

function Write-MeasurementLoadSamples([string] $OutputPath, [string] $StopPath, [string] $ReadyPath) {
    $state = $null
    $initialError = $null
    try { $state = New-MeasurementLoadState }
    catch { $initialError = $_.Exception.Message }
    try {
        while (-not (Test-Path -LiteralPath $StopPath)) {
            Start-Sleep -Seconds 1
            try {
                if ($initialError) { throw $initialError }
                $record = Get-MeasurementLoadSample $state
            } catch { $record = @{ utc = [DateTime]::UtcNow.ToString('O'); error = $_.Exception.Message } }
            try { $record | ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath $OutputPath -Encoding utf8 }
            finally { [IO.File]::WriteAllText($ReadyPath, '') }
        }
    } finally { Close-MeasurementLoadState $state }
}

function Test-MeasurementCpuPrecheck {
    param([object[]] $Samples, [double] $ElapsedSeconds,
        [double] $MeanThresholdPercent = 25, [double] $ConsecutiveThresholdPercent = 40)
    $decision = @{ status = 'unknown'; passed = $false; reason = '采样未覆盖完整的 30 秒。'
        sample_count = $Samples.Count; mean_cpu_percent = $null; consecutive_high = $false }
    if ($ElapsedSeconds -lt $MeasurementCpuWindowSeconds -or $Samples.Count -lt 2) { return $decision }
    $previous = $null
    $sum = 0.0
    foreach ($sample in $Samples) {
        try { $cpu = $sample.total_cpu_percent; $at = $sample.elapsed_seconds }
        catch { $decision.reason = '存在缺失或失败的采样。'; return $decision }
        if ($null -eq $cpu -or $cpu -is [bool] -or $cpu -is [string] -or
            -not [double]::IsFinite($cpu) -or $cpu -lt 0 -or $cpu -gt 100 -or
            $null -eq $at -or -not [double]::IsFinite($at) -or $at -lt 0 -or $at -gt $ElapsedSeconds) {
            $decision.reason = '存在无效的采样值。'; return $decision
        }
        if ($null -eq $previous) {
            if ($at -gt 2) { return $decision }
        } else {
            $gap = $at - $previous.elapsed_seconds
            if ($gap -le 0 -or $gap -gt 2) { return $decision }
            if ($cpu -gt $ConsecutiveThresholdPercent -and $previous.total_cpu_percent -gt $ConsecutiveThresholdPercent) {
                $decision.consecutive_high = $true
            }
        }
        $sum += $cpu
        $previous = $sample
    }
    if ($ElapsedSeconds - $previous.elapsed_seconds -gt 2) { return $decision }
    $decision.mean_cpu_percent = $sum / $Samples.Count
    $decision.passed = $decision.mean_cpu_percent -le $MeanThresholdPercent -and -not $decision.consecutive_high
    $decision.status = if ($decision.passed) { 'passed' } else { 'busy' }
    $decision.reason = if ($decision.passed) { $null } else { 'CPU 均值或连续两次采样超过门槛。' }
    return $decision
}

# 执行状态属于原生线程；专用线程持有到 finally，不能依赖 PowerShell 的运行线程。
if ($IsWindows -and -not ('MeasurementDisplayAwake' -as [type])) {
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
