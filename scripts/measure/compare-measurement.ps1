#Requires -Version 7.0
# Decision note: 2026-10-26-performance-measurement-system
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $AExecutablePath,
    [Parameter(Mandatory)][string] $BExecutablePath,
    [Parameter(Mandatory)][string] $InputPath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][string[]] $Scenario,
    [string] $AReferencePath,
    [string] $BReferencePath,
    [ValidateRange(1, 2147483647)][int] $Repeat = 5,
    [ValidateRange(0, 2146878)][int] $DurationSeconds = 0,
    [switch] $NodeReadyHold,
    [ValidateRange(1, 9223372036854775807)][long] $TrainId,
    [ValidateRange(1, 9223372036854775807)][long] $NodeId,
    [ValidateRange(0, 100)][double] $CpuMeanThresholdPercent = 25,
    [ValidateRange(0, 100)][double] $CpuConsecutiveThresholdPercent = 40
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw '交替对照入口用于 Windows 开发机；正式测量使用 runner 的 measure workflow。' }
. (Join-Path $PSScriptRoot 'measurement-host.ps1')
$measurementScript = Join-Path $PSScriptRoot 'measure.ps1'
$allowed = (Get-Command $measurementScript).Parameters['Scenario'].Attributes |
    Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
$Scenario = @($Scenario | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Select-Object -Unique)
if ($Scenario.Count -eq 0 -or @($Scenario | Where-Object { $_ -notin $allowed.ValidValues }).Count) {
    throw '必须指定 measure.ps1 支持的场景。'
}
$executables = @{ A = (Resolve-Path -LiteralPath $AExecutablePath).Path; B = (Resolve-Path -LiteralPath $BExecutablePath).Path }
$references = @{ A = $AReferencePath; B = $BReferencePath }
foreach ($side in @('A', 'B')) {
    if (-not (Test-Path -LiteralPath $executables[$side] -PathType Leaf)) { throw '可执行文件必须是文件。' }
    if ($references[$side]) { $references[$side] = (Resolve-Path -LiteralPath $references[$side]).Path }
}
$InputPath = (Resolve-Path -LiteralPath $InputPath).Path
if (-not (Test-Path -LiteralPath $InputPath -PathType Leaf)) { throw '输入存档必须是文件。' }
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and
    (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count)) {
    throw '输出目录必须是新目录或空目录。'
}
New-Item -ItemType Directory -Force -Path (Join-Path $OutputDirectory A), (Join-Path $OutputDirectory B) | Out-Null
$slots = [Collections.Generic.List[object]]::new()
$attempts = [Collections.Generic.List[object]]::new()
$retry = [Collections.Generic.List[object]]::new()
$state = $null
$initialError = $null
$awake = $null

function Write-ComparisonSummary {
    foreach ($side in @('A', 'B')) {
        $runs = @($slots | Where-Object side -eq $side | ForEach-Object { $_.result })
        @{ succeeded = @($runs | Where-Object { -not $_.succeeded }).Count -eq 0; runs = $runs } |
            ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory "$side/summary.json") -Encoding utf8
    }
    @{ succeeded = @($slots | Where-Object { -not $_.result.succeeded }).Count -eq 0
        attempts = @($attempts.ToArray()); repeat = $Repeat; scenarios = $Scenario
        cpu_precheck = @{ window_seconds = $MeasurementCpuWindowSeconds; mean_threshold_percent = $CpuMeanThresholdPercent
            consecutive_threshold_percent = $CpuConsecutiveThresholdPercent } } |
        ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'comparison.json') -Encoding utf8
}

function Invoke-ComparisonSlot($Slot, [int] $Attempt) {
    $runName = "$($Slot.scenario)-$($Slot.repeat)-attempt-$Attempt"
    $directory = Join-Path $OutputDirectory "$($Slot.side)/$runName"
    New-Item -ItemType Directory -Path $directory | Out-Null
    $samples = [Collections.Generic.List[object]]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    # 计数器在本进程中只创建一次；丢弃前一轮游戏期间的累计区间。
    try { if ($state) { foreach ($counter in $state.counters.Values) { $null = $counter.NextValue() } } }
    catch { $samples.Add(@{ elapsed_seconds = 0; error = $_.Exception.Message }) }
    while ($clock.Elapsed.TotalSeconds -lt $MeasurementCpuWindowSeconds) {
        Start-Sleep -Seconds 1
        try {
            if ($initialError) { throw $initialError }
            $sample = Get-MeasurementLoadSample $state
        } catch { $sample = @{ utc = [DateTime]::UtcNow.ToString('O'); error = $_.Exception.Message } }
        $sample.elapsed_seconds = $clock.Elapsed.TotalSeconds
        $samples.Add($sample)
    }
    $precheck = Test-MeasurementCpuPrecheck -Samples $samples.ToArray() -ElapsedSeconds $clock.Elapsed.TotalSeconds `
        -MeanThresholdPercent $CpuMeanThresholdPercent -ConsecutiveThresholdPercent $CpuConsecutiveThresholdPercent
    @{ decision = $precheck; samples = @($samples.ToArray()) } | ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath (Join-Path $directory 'precheck.json') -Encoding utf8
    $measurement = Join-Path $directory 'measurement'
    $entry = @{ side = $Slot.side; scenario = $Slot.scenario; repeat = $Slot.repeat; attempt = $Attempt
        precheck = $precheck; measurement_started = $false; succeeded = $false; exit_code = $null
        report = "$runName/measurement/$($Slot.scenario)-1/report.json"; error = $null }
    $attempts.Add($entry)
    $Slot.result = $entry
    if ($precheck.passed) {
        $entry.measurement_started = $true
        $arguments = @('-NoProfile', '-File', $measurementScript, '-ExecutablePath', $executables[$Slot.side],
            '-InputPath', $InputPath, '-OutputDirectory', $measurement, '-Scenario', $Slot.scenario,
            '-Repeat', '1', '-DurationSeconds', $DurationSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
        if ($NodeReadyHold) { $arguments += '-NodeReadyHold' }
        if ($script:PSBoundParameters.ContainsKey('TrainId')) { $arguments += @('-TrainId', $TrainId.ToString([Globalization.CultureInfo]::InvariantCulture)) }
        if ($script:PSBoundParameters.ContainsKey('NodeId')) { $arguments += @('-NodeId', $NodeId.ToString([Globalization.CultureInfo]::InvariantCulture)) }
        if ($references[$Slot.side]) { $arguments += @('-ReferencePath', $references[$Slot.side]) }
        try {
            & (Join-Path $PSHOME 'pwsh.exe') @arguments *> (Join-Path $directory 'measurement.log')
            $entry.exit_code = $LASTEXITCODE
            $summary = Get-Content -LiteralPath (Join-Path $measurement 'summary.json') -Raw | ConvertFrom-Json
            $entry.succeeded = $entry.exit_code -eq 0 -and $summary.succeeded -and @($summary.runs).Count -eq 1 -and $summary.runs[0].succeeded
            if (-not $entry.succeeded) { $entry.error = '测量失败，保留原始输出，不补跑。' }
        } catch { $entry.error = $_.Exception.Message }
    }
    Write-ComparisonSummary
    Write-Host "$($Slot.side)/$($Slot.scenario)/$($Slot.repeat) attempt=$Attempt precheck=$($precheck.status) started=$($entry.measurement_started) succeeded=$($entry.succeeded)"
    return $precheck.passed
}

try {
    try { $state = New-MeasurementLoadState } catch { $initialError = $_.Exception.Message }
    $awake = [MeasurementDisplayAwake]::new()
    foreach ($name in $Scenario) {
        for ($round = 1; $round -le $Repeat; $round++) {
            foreach ($side in @('A', 'B')) {
                $slot = @{ side = $side; scenario = $name; repeat = $round; result = $null }
                $slots.Add($slot)
                if (-not (Invoke-ComparisonSlot $slot 1)) { $retry.Add($slot) }
            }
        }
    }
    foreach ($slot in $retry) { $null = Invoke-ComparisonSlot $slot 2 }
} finally {
    Close-MeasurementLoadState $state
    if ($null -ne $awake) { $awake.Dispose() }
}
if (@($slots | Where-Object { -not $_.result.succeeded }).Count) { exit 1 }
exit 0
