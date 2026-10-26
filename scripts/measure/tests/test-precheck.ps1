#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../measurement-host.ps1')

function Samples([double] $Cpu = 10) {
    @(0..30 | ForEach-Object { @{ elapsed_seconds = [double] $_; total_cpu_percent = $Cpu } })
}

$count = 0
function Assert-Status([string] $Name, [string] $Expected, [object[]] $Data,
    [double] $Elapsed = 30, [double] $Mean = 25, [double] $Consecutive = 40) {
    $result = Test-MeasurementCpuPrecheck -Samples $Data -ElapsedSeconds $Elapsed `
        -MeanThresholdPercent $Mean -ConsecutiveThresholdPercent $Consecutive
    if ($result.status -ne $Expected -or $result.passed -ne ($Expected -eq 'passed')) {
        throw "$Name expected=$Expected actual=$($result.status)"
    }
    $script:count++
}

Assert-Status 'complete quiet window' passed (Samples)
Assert-Status 'incomplete observation' unknown @(Samples | Where-Object elapsed_seconds -le 29) 29.999
Assert-Status 'just past thirty seconds' passed (Samples) 30.001
Assert-Status 'mean at threshold' passed (Samples 25)
Assert-Status 'mean below threshold' passed (Samples 24.99)
Assert-Status 'mean above threshold' busy (Samples 25.01)
$spike = Samples
$spike[10].total_cpu_percent = 60
Assert-Status 'single spike' passed $spike
$spike[11].total_cpu_percent = 41
Assert-Status 'two consecutive spikes' busy $spike
$spike[10].total_cpu_percent = 40
$spike[11].total_cpu_percent = 40
Assert-Status 'consecutive values at threshold' passed $spike
$spike[10].total_cpu_percent = 41
$spike[11].total_cpu_percent = 10
$spike[12].total_cpu_percent = 41
Assert-Status 'separate spikes' passed $spike
Assert-Status 'missing samples' unknown @()
Assert-Status 'missing middle of window' unknown @(Samples | Where-Object { $_.elapsed_seconds -notin 10,11,12 })
Assert-Status 'stale final sample' unknown @(Samples | Where-Object elapsed_seconds -le 27)
Assert-Status 'late first sample' unknown @(Samples | Where-Object elapsed_seconds -ge 3)
foreach ($value in @($null, [double]::NaN, 'unavailable', $true, -1, 101)) {
    $invalid = Samples
    $invalid[15].total_cpu_percent = $value
    Assert-Status 'unavailable CPU is unknown' unknown $invalid
}
$errorSample = Samples
$errorSample[15] = @{ elapsed_seconds = 15; error = 'counter failed' }
Assert-Status 'failed sample is unknown' unknown $errorSample
Assert-Status 'custom mean threshold' passed (Samples 30) 30 30 60
$custom = Samples
$custom[10].total_cpu_percent = 61
$custom[11].total_cpu_percent = 61
Assert-Status 'custom consecutive threshold' busy $custom 30 30 60
Write-Host "PASS $count CPU precheck cases"
