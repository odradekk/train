#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ExecutablePath,
    [Parameter(Mandatory)][string] $InputPath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][string] $Scenario,
    [ValidateSet('counters','sample')][string] $Mode = 'counters',
    [int] $DurationSeconds = 0,
    [long] $TrainId = 1,
    [string] $ReferencePath,
    [string] $Machine = 'development',
    [string] $MeasureScript = (Join-Path $PSScriptRoot 'source/scripts/measure.ps1'),
    [string] $DotnetTracePath = (Join-Path $PSScriptRoot 'tools/dotnet-trace.exe')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$argsFile = Join-Path $output 'invocation.json'
$measureArgs = @('-ExecutablePath', (Resolve-Path -LiteralPath $ExecutablePath).Path,
    '-InputPath', (Resolve-Path -LiteralPath $InputPath).Path, '-OutputDirectory', (Join-Path $output 'measurement'),
    '-Scenario', $Scenario, '-Repeat', '1', '-DurationSeconds', "$DurationSeconds", '-TrainId', "$TrainId", '-Machine', $Machine)
if ($ReferencePath) { $measureArgs += @('-ReferencePath', (Resolve-Path -LiteralPath $ReferencePath).Path) }
@{ script = (Resolve-Path -LiteralPath $MeasureScript).Path; arguments = $measureArgs; mode = $Mode;
    dotnet_trace = $DotnetTracePath; started_utc = [DateTimeOffset]::UtcNow.ToString('o') } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $argsFile
$env:TRAIN_774_MODE = $Mode
$env:DOTNET_EnableEventPipe = '0'
$measure = $null
$collectors = [Collections.Generic.List[Diagnostics.Process]]::new()
$seen = [Collections.Generic.HashSet[string]]::new()
try {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })))
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8; $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in @('-NoProfile','-File',(Resolve-Path -LiteralPath $MeasureScript).Path) + $measureArgs) { $start.ArgumentList.Add($argument) }
    $measure = [Diagnostics.Process]::Start($start)
    $stdout = $measure.StandardOutput.ReadToEndAsync(); $stderr = $measure.StandardError.ReadToEndAsync()
    while (-not $measure.HasExited) {
        if ($Mode -eq 'sample') {
            foreach ($ready in @(Get-ChildItem -LiteralPath $output -Filter gc-ready.json -Recurse -File)) {
                if (-not $seen.Add($ready.FullName)) { continue }
                $target = (Get-Content -LiteralPath $ready.FullName -Raw | ConvertFrom-Json).pid
                $startTrace = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $DotnetTracePath).Path)
                $startTrace.UseShellExecute = $false; $startTrace.CreateNoWindow = $true
                $startTrace.RedirectStandardOutput = $true; $startTrace.RedirectStandardError = $true
                $startTrace.StandardOutputEncoding = [Text.Encoding]::UTF8; $startTrace.StandardErrorEncoding = [Text.Encoding]::UTF8
                foreach ($argument in @('collect','--process-id',"$target",'--providers',
                    'Microsoft-Windows-DotNETRuntime:0x19:5,Train-Issue774:0xffffffffffffffff:5',
                    '--buffersize','256','--duration','00:01:10:00','--output',(Join-Path $ready.DirectoryName 'game.nettrace'),
                    '--stopping-event-provider-name','Train-Issue774','--stopping-event-event-name','Marker',
                    '--stopping-event-payload-filter','phase:run-end')) { $startTrace.ArgumentList.Add($argument) }
                $collector = [Diagnostics.Process]::Start($startTrace)
                $collector | Add-Member NoteProperty OutTask ($collector.StandardOutput.ReadToEndAsync())
                $collector | Add-Member NoteProperty ErrTask ($collector.StandardError.ReadToEndAsync())
                $collector | Add-Member NoteProperty EvidenceDirectory $ready.DirectoryName
                $collectors.Add($collector)
            }
        }
        foreach ($collector in $collectors) {
            $signal = Join-Path $collector.EvidenceDirectory 'gc-stopped.signal'
            if ($collector.HasExited -and -not (Test-Path -LiteralPath $signal)) {
                $collector.OutTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $collector.EvidenceDirectory 'trace.stdout.log')
                $collector.ErrTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $collector.EvidenceDirectory 'trace.stderr.log')
                if ($collector.ExitCode -ne 0) { throw "Trace collector exited $($collector.ExitCode)." }
                [IO.File]::WriteAllText($signal, 'collector exited 0')
            }
        }
        Start-Sleep -Milliseconds 200
    }
    $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output 'measure.stdout.log')
    $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $output 'measure.stderr.log')
    foreach ($collector in $collectors) {
        if (-not $collector.WaitForExit(60000)) { throw 'Trace collector did not stop.' }
        $collector.OutTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $collector.EvidenceDirectory 'trace.stdout.log')
        $collector.ErrTask.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $collector.EvidenceDirectory 'trace.stderr.log')
        if ($collector.ExitCode -ne 0) { throw "Trace collector exited $($collector.ExitCode)." }
    }
    if ($Mode -eq 'sample' -and $collectors.Count -eq 0) { throw 'No game collector was started.' }
    if ($measure.ExitCode -ne 0) { throw "Measure exited $($measure.ExitCode); see retained logs." }
    @{ outcome = 'completed'; measure_exit = $measure.ExitCode; collectors = $collectors.Count;
        completed_utc = [DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content (Join-Path $output 'profile-summary.json')
} finally {
    foreach ($owned in @($measure) + $collectors.ToArray()) {
        if ($null -ne $owned -and -not $owned.HasExited) { $owned.Kill($true); $owned.WaitForExit() }
    }
}
