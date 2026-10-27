#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../measurement-host.ps1')

$root = Join-Path ([IO.Path]::GetTempPath()) ('train-run-outcome-' + [guid]::NewGuid().ToString('N'))
$completed = '{"outcome":"completed","identity":{"build_configuration":"ExportRelease"},"timing":{"frames":[0.016]},"scope":{"final_performance":false}}'
$normal = '{"exit_code":0,"signal":null}'
$success = @{ ExitCode = 0; TimedOut = $false; CleanupSucceeded = $true; DurationMs = 1; Output = '' }
$count = 0
function Check([string] $Name, [string] $GameStatus = $normal, [string] $Report = $completed,
    [string[]] $Problems = @(), [switch] $Windows, [hashtable] $Result = $success) {
    $directory = Join-Path $root $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    if ($GameStatus) { [IO.File]::WriteAllText((Join-Path $directory 'game-process.json'), $GameStatus) }
    if ($Report) { [IO.File]::WriteAllText((Join-Path $directory 'report.json'), $Report) }
    $problem = $null
    try { Assert-MeasurementRunOutcome -Result $Result -RunDirectory $directory -RunName "$Name/2" -Gamescope:(-not $Windows) }
    catch { $problem = $_.Exception.Message }
    if (($Problems.Count -eq 0) -ne ($null -eq $problem)) { throw "$Name expected=[$Problems] actual=[$problem]" }
    if ($Problems.Count -gt 0 -and -not $problem.StartsWith("$Name/2 ")) { throw "$Name missing run prefix: $problem" }
    foreach ($part in $Problems) {
        if (-not $problem.Contains($part)) { throw "$Name missing=[$part] actual=[$problem]" }
    }
    $script:count++
    return Get-Content -LiteralPath (Join-Path $directory 'process.json') -Raw | ConvertFrom-Json -AsHashtable
}

try {
    $windows = Check 'windows-success' -Windows
    $expectedKeys = @('cleanup_succeeded', 'duration_ms', 'exit_code', 'process_diagnostics', 'timed_out')
    if (@(Compare-Object $expectedKeys @($windows.Keys)).Count -ne 0 -or $windows.exit_code -ne 0) {
        throw 'Windows process metadata changed.'
    }
    $linux = Check 'linux-success'
    $nonzero = Check 'game-nonzero-after-report' -GameStatus '{"exit_code":7,"signal":null}' -Problems @('7')
    if ($nonzero.exit_code -ne 7 -or $nonzero.launcher_exit_code -ne 0) { throw 'Game exit code was lost.' }
    if ($linux.exit_code -ne 0 -or $linux.launcher_exit_code -ne 0 -or $null -ne $linux.signal) {
        throw 'Linux successful game status was not preserved.'
    }
    $aborted = Check 'game-aborted-after-report' -GameStatus '{"exit_code":-6,"signal":{"name":"SIGABRT","number":6}}' -Problems @('SIGABRT(6)')
    if ($aborted.exit_code -ne -6 -or $aborted.signal.name -ne 'SIGABRT' -or $aborted.signal.number -ne 6) {
        throw 'Game signal was lost.'
    }
    $unknown = Check 'missing-game-status-after-report' -GameStatus '' -Problems @('游戏退出状态')
    if ($null -ne $unknown.exit_code) { throw 'Missing game status was treated as an exit code.' }
    Check 'unreadable-game-status' -GameStatus '{' -Problems @('游戏退出状态') | Out-Null
    Check 'missing-report' -Report '' -Problems @('未生成报告') | Out-Null
    Check 'unreadable-report' -Report '{' -Problems @('报告无法解析') | Out-Null
    Check 'failed-report' -Report ($completed.Replace('completed', 'failed')) -Problems @('报告未完成') | Out-Null
    Check 'missing-outcome' -Report ($completed.Replace('"outcome":"completed",', '')) -Problems @('报告未完成') | Out-Null
    Check 'aborted-without-report' -GameStatus '{"exit_code":-6,"signal":{"name":"SIGABRT","number":6}}' -Report '' -Problems @('SIGABRT(6)', '未生成报告') | Out-Null
    foreach ($platform in @($true, $false)) {
        $failed = $success.Clone(); $failed.ExitCode = 9
        Check "launcher-nonzero-$platform" -Windows:$platform -Result $failed -Problems @('9') | Out-Null
        $timedOut = $success.Clone(); $timedOut.TimedOut = $true
        Check "timeout-$platform" -Windows:$platform -Result $timedOut -Problems @('超时') | Out-Null
        $unclean = $success.Clone(); $unclean.CleanupSucceeded = $false
        Check "cleanup-failed-$platform" -Windows:$platform -Result $unclean -Problems @('清理') | Out-Null
        Check "not-release-$platform" -Windows:$platform -Report ($completed.Replace('ExportRelease', 'Debug')) -Problems @('Release') | Out-Null
        Check "no-frames-$platform" -Windows:$platform -Report ($completed.Replace('[0.016]', '[]')) -Problems @('绘制帧') | Out-Null
        Check "null-frames-$platform" -Windows:$platform -Report ($completed.Replace('[0.016]', 'null')) -Problems @('绘制帧') | Out-Null
        Check "final-performance-$platform" -Windows:$platform -Report ($completed.Replace('false', 'true')) -Problems @('scope') | Out-Null
    }
    Check 'windows-missing-report' -Windows -Report '' -Problems @('未生成报告') | Out-Null
    Check 'windows-failed-report' -Windows -Report ($completed.Replace('completed', 'failed')) -Problems @('报告未完成') | Out-Null
    Write-Host "PASS $count run outcome cases"
} finally {
    if (Test-Path -LiteralPath $root) {
        if ((Resolve-Path -LiteralPath $root).Path -ne $root) { throw 'Unexpected test cleanup path.' }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
