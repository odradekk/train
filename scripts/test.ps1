param(
    [string[]] $Layer = @('fast'),
    [string[]] $Project = @(),
    [string] $BaseRef = 'origin/master',
    [switch] $All,
    [switch] $ExportWindows
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'checks/Invoke-HiddenProcess.ps1')
. (Join-Path $PSScriptRoot 'checks/Get-TestSelection.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $root
$layers = @(($Layer -join ',') -split ',' | Select-Object -Unique)
if (@($layers | Where-Object { $_ -notin @('fast', 'integration', 'engine', 'all') }).Count) { throw 'Unknown test layer.' }
if ('all' -in $layers) { $layers = @('fast', 'integration', 'engine') }
$Project = @(($Project -join ',') -split ',' | Where-Object { $_ } | Select-Object -Unique)
if (@($Project | Where-Object { $_ -notin @('core', 'content', 'godot', 'contract-support') }).Count) { throw 'Unknown test project.' }
if ($All -and $Project.Count) { throw '-All and -Project cannot be combined.' }
if ($ExportWindows -and ('engine' -notin $layers -or $Project.Count -or -not $IsWindows)) { throw '-ExportWindows requires engine, no -Project, and Windows.' }
if ($Project.Count) { $layers = @($layers | Where-Object { $_ -ne 'engine' }) }
if (-not $layers.Count) { throw 'Selected layers match no MSTest tests.' }
$projects = @(
    @{ name = 'core'; path = 'Tests/Core/Train.Core.Tests.csproj'; fast = 245; integration = 4 },
    @{ name = 'content'; path = 'Tests/Content/Train.Content.Tests.csproj'; fast = 189; integration = 7 },
    @{ name = 'godot'; path = 'Tests/Godot/Train.Godot.Tests.csproj'; fast = 12; integration = 40; engine = 20 },
    @{ name = 'contract-support'; path = 'Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj'; fast = 13; integration = 0 }
)
$engineProject = $projects | Where-Object name -eq 'godot'
if ($Project.Count) { $projects = @($projects | Where-Object { $_.name -in $Project }) }
$commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE) { throw 'Cannot identify tested commit.' }
$results = Join-Path $root 'TestResults'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$mutex = [Threading.Mutex]::new($false, 'Train.Tests.' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($root))))
try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
if (-not $acquired) { $mutex.Dispose(); throw 'Another test run owns this worktree.' }
$runs = @(Get-ChildItem $results -Directory | Where-Object Name -match '^(fast|integration|engine)(-(fast|integration|engine))*-\d{8}T\d{9}-[0-9a-f]{7}$' | Sort-Object { ($_.Name -split '-')[-2] } -Descending)
foreach ($previous in $runs) {
    if (-not (Test-Path (Join-Path $previous.FullName 'summary.json'))) {
        Remove-Item -LiteralPath $previous.FullName -Recurse -Force
    }
}
$completed = @($runs | Where-Object { Test-Path (Join-Path $_.FullName 'summary.json') })
foreach ($previous in ($completed | Select-Object -Skip 9)) { Remove-Item -LiteralPath $previous.FullName -Recurse -Force }
$run = Join-Path $results (($layers -join '-') + '-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '-' + $commit.Substring(0, 7))
New-Item -ItemType Directory -Path (Join-Path $run 'tmp') -Force | Out-Null
$environment = @{}
foreach ($name in @('TEMP', 'TMP', 'TMPDIR')) { $environment[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, (Join-Path $run 'tmp')) }
# 编译器和 MSBuild 节点复用会沿用旧进程的 TEMP，使副产物泄漏到上次运行。
foreach ($name in @('UseSharedCompilation', 'MSBUILDDISABLENODEREUSE')) { $environment[$name] = [Environment]::GetEnvironmentVariable($name) }
$env:UseSharedCompilation = 'false'; $env:MSBUILDDISABLENODEREUSE = '1'
$userRoot = if ($IsWindows) { Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Godot/app_userdata/Train' } else { Join-Path ($env:XDG_DATA_HOME ?? (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.local/share')) 'godot/app_userdata/Train' }
function Get-Snapshot([string] $Path, [switch] $Workspace) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force | Where-Object {
        -not $Workspace -or (-not $_.FullName.StartsWith($run + [IO.Path]::DirectorySeparatorChar) -and
            [IO.Path]::GetRelativePath($root, $_.FullName) -notmatch '(^|[\\/])(bin|obj|\.godot|\.git)([\\/]|$)')
    } | ForEach-Object { if ($Workspace) { $_.FullName } else { "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" } } | Sort-Object)
}
$summary = [ordered]@{ commit = $commit; gitBefore = @(git status --porcelain=v1); selection = @{ layers = $layers; baseRef = $BaseRef; projects = @(); exportWindows = [bool] $ExportWindows }; checks = @(); layers = @(); outcome = 'failed' }
$workspaceBefore = [Collections.Generic.HashSet[string]]::new([string[]] @(Get-Snapshot $root -Workspace), [StringComparer]::Ordinal)
$userBefore = @(Get-Snapshot $userRoot) -join "`n"
$pwsh = (Get-Process -Id $PID).Path
$started = [Diagnostics.Stopwatch]::StartNew()
$engineUserRoot = $null
function Add-CheckResult([string] $Name, [string] $Group, [string] $Program, [string[]] $Arguments, $Result) {
    $result.Output | Set-Content (Join-Path $run "$Name.log")
    Write-Host $result.Output
    $check = [ordered]@{ name = $Name; layer = $Group; command = @($Program) + $Arguments; seconds = $result.DurationMs / 1000; exitCode = $result.ExitCode; timedOut = $result.TimedOut; cleanupSucceeded = $result.CleanupSucceeded; total = 0; executed = 0; passed = 0; failed = 0; skipped = 0 }
    $trx = Join-Path $run "$Name.trx"
    if (Test-Path $trx) {
        [xml] $report = Get-Content $trx -Raw
        $counts = $report.TestRun.ResultSummary.Counters
        foreach ($count in @('total', 'executed', 'passed', 'failed')) { $check[$count] = [int] $counts.$count }
        $check.skipped = [int] $counts.notExecuted + [int] $counts.inconclusive
    } elseif ($Name -eq 'engine-godot') {
        $junit = Join-Path $run "$Name.junit.xml"
        if (Test-Path $junit) {
            [xml] $report = Get-Content $junit -Raw
            $cases = @($report.SelectNodes('//testcase'))
            $check.total = $cases.Count
            $check.failed = @($cases | Where-Object { $_.SelectSingleNode('failure') -or $_.SelectSingleNode('error') }).Count
            $check.skipped = @($cases | Where-Object { $_.SelectSingleNode('skipped') }).Count
            $check.executed = $check.total - $check.skipped
            $check.passed = $check.executed - $check.failed
            $check.report = $junit
        }
        $summary.checks += [pscustomobject] $check
        $minimum = $engineProject.engine
        if (-not (Test-Path $junit) -or $check.executed -lt $minimum -or $check.failed -or $result.ExitCode -ne 0 -or $result.TimedOut -or -not $result.CleanupSucceeded) {
            throw "$Name failed (exit=$($result.ExitCode), timeout=$($result.TimedOut), executed=$($check.executed), minimum=$minimum, failed=$($check.failed)); evidence: $run"
        }
        return
    } elseif ($Name -match '^(fast|integration)-(core|content|godot|contract-support)$') {
        $summary.checks += [pscustomobject] $check
        throw "$Name did not produce its required TRX report."
    } elseif ($Group -ne 'preparation') {
        $check.total = 1; $check.executed = 1
        if ($result.ExitCode -eq 0 -and -not $result.TimedOut) { $check.passed = 1 } else { $check.failed = 1 }
    }
    $summary.checks += [pscustomobject] $check
    if ($result.ExitCode -ne 0 -or $result.TimedOut -or -not $result.CleanupSucceeded) { throw "$Name failed (exit=$($result.ExitCode), timeout=$($result.TimedOut)); evidence: $run" }
}
function Invoke-Check([string] $Name, [string] $Group, [string] $Program, [string[]] $Arguments, [int] $Seconds) {
    if ($Seconds -lt 1) { throw "$Group exhausted its time budget." }
    Add-CheckResult $Name $Group $Program $Arguments (Invoke-HiddenProcess -FilePath $Program -ArgumentList $Arguments -TimeoutSeconds $Seconds)
}
try {
    $summary.selection = Get-TestSelection -BaseRef $BaseRef -All:$All -Project $Project -Projects $projects -OutputDirectory $run
    $summary.selection.layers = $layers
    $summary.selection.exportWindows = [bool] $ExportWindows
    $projects = @($projects | Where-Object { $_.name -in $summary.selection.projects })
    $selectedLayers = @($layers | Where-Object {
        $_ -eq 'engine' -or $projects.Count -or ($_ -eq 'integration' -and ($summary.selection.contracts -or $summary.selection.schema))
    })
    if ($selectedLayers.Count -and ($projects.Count -or $summary.selection.contracts -or 'engine' -in $selectedLayers)) {
        Invoke-Check build preparation dotnet @('build', 'Train.sln', '--nologo') 300
    } elseif (-not $selectedLayers.Count) { Write-Host 'No affected test projects or checks selected; no build or tests required.' }
    foreach ($current in $layers) {
        $budget = @{ fast = 30; integration = 180; engine = 300 }[$current]
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $selected = $current -in $selectedLayers
        try {
            if (-not $selected) { Write-Host "LAYER=$current not selected"; continue }
            if ($current -ne 'engine') {
                $deadline = [DateTime]::UtcNow.AddSeconds($budget)
                $helper = Join-Path $PSScriptRoot 'checks/Invoke-HiddenProcess.ps1'
                $results = @()
                if ($projects.Count) { $results = @($projects | ForEach-Object -Parallel {
                    . $using:helper
                    $current = $using:current
                    $minimum = $_[$current]
                    $remaining = [int] [Math]::Floor(($using:deadline - [DateTime]::UtcNow).TotalSeconds)
                    if ($remaining -lt 1) { throw "$current exhausted its time budget." }
                    $filter = if ($current -eq 'fast') { 'TestCategory!=integration' } else { 'TestCategory=integration' }
                    $name = "$current-$($_.name)"
                    $arguments = @('test', '--project', $_.path, '--no-build', '--results-directory', $using:run)
                    if ($minimum) { $arguments += @('--minimum-expected-tests', "$minimum") }
                    $arguments += @('--', '--filter', $filter, '--report-trx', '--report-trx-filename', "$name.trx", '--timeout', "${remaining}s", '--hangdump', '--hangdump-type', 'Mini', '--hangdump-timeout', "$([Math]::Max(1, $remaining - 5))s")
                    if (-not $minimum) { $arguments += @('--ignore-exit-code', '8') }
                    [pscustomobject]@{ name = $name; arguments = $arguments; result = (Invoke-HiddenProcess -FilePath dotnet -ArgumentList $arguments -TimeoutSeconds $remaining) }
                } -ThrottleLimit $projects.Count) }
                $failure = $null
                foreach ($item in $results) {
                    try { Add-CheckResult $item.name $current dotnet $item.arguments $item.result }
                    catch { $failure = $_ }
                }
                if ($failure) { throw $failure }
                if ($results.Count -ne $projects.Count) { throw "$current did not complete every selected project." }
                if ($current -eq 'integration') {
                    if ($summary.selection.contracts) { Invoke-Check contracts $current $pwsh @('-NoProfile', '-File', 'scripts/checks/check-project-boundaries.ps1', '-NoSolutionBuild', '-OutputDirectory', (Join-Path $run 'contracts')) ([int] [Math]::Floor($budget - $watch.Elapsed.TotalSeconds)) }
                    if ($summary.selection.schema) { Invoke-Check schema $current $pwsh @('-NoProfile', '-File', 'scripts/checks/validate-content-examples.ps1') ([int] [Math]::Floor($budget - $watch.Elapsed.TotalSeconds)) }
                }
            } else {
                $watch.Stop()
                $paths = if ($IsWindows) { @{ APPDATA = 'appdata'; LOCALAPPDATA = 'localappdata' } } else { @{ XDG_CONFIG_HOME = 'config'; XDG_DATA_HOME = 'data'; XDG_CACHE_HOME = 'cache' } }
                foreach ($name in $paths.Keys) {
                    $environment[$name] = [Environment]::GetEnvironmentVariable($name)
                    $directory = Join-Path $run $paths[$name]
                    New-Item -ItemType Directory -Force -Path $directory | Out-Null
                    [Environment]::SetEnvironmentVariable($name, $directory)
                }
                $engineUserRoot = if ($IsWindows) { Join-Path $env:APPDATA 'Godot/app_userdata/Train' } else { Join-Path $env:XDG_DATA_HOME 'godot/app_userdata/Train' }
                Invoke-Check import preparation $env:GODOT @('--headless', '--path', $root, '--recovery-mode', '--log-file', (Join-Path $run 'import-godot.log'), '--editor', '--import') 120
                $binary = Join-Path $run 'exports/windows/train.exe'
                if ($ExportWindows) {
                    New-Item -ItemType Directory -Path (Split-Path $binary) -Force | Out-Null
                    $env:APPDATA = $environment.APPDATA
                    try { Invoke-Check export preparation $env:GODOT @('--headless', '--path', $root, '--recovery-mode', '--log-file', (Join-Path $run 'export-godot.log'), '--export-release', 'Windows Desktop', $binary) 120 }
                    finally { $env:APPDATA = Join-Path $run $paths.APPDATA }
                }
                $watch.Start()
                foreach ($name in @('TRAIN_ENGINE_TEST_ASSEMBLY', 'TRAIN_ENGINE_REPORT', 'TRAIN_LOG_LEVEL')) { $environment[$name] = [Environment]::GetEnvironmentVariable($name) }
                $env:TRAIN_ENGINE_TEST_ASSEMBLY = Join-Path $root '.godot/mono/temp/bin/Debug/Train.Engine.Tests.dll'
                $env:TRAIN_ENGINE_REPORT = Join-Path $run 'engine-godot.junit.xml'
                $env:TRAIN_LOG_LEVEL = 'info'
                Invoke-Check engine-godot $current $env:GODOT @('--headless', '--path', $root, '--scene', 'res://Tests/Engine/Bootstrap/Runner.tscn', '--log-file', (Join-Path $run 'engine-godot-native.log'), '--', '--run-tests', '--quit-on-finish') ([int] [Math]::Floor($budget - $watch.Elapsed.TotalSeconds))
                if ($ExportWindows) {
                    $arguments = @('-NoProfile', '-File', 'scripts/checks/run-export-smoke.ps1', '-ExecutablePath', $binary, '-OutputDirectory', (Join-Path $run 'export-smoke'), '-TimeoutSeconds', "$([Math]::Max(1, [int] [Math]::Floor($budget - $watch.Elapsed.TotalSeconds) - 5))")
                    Invoke-Check export-smoke $current $pwsh $arguments ([int] [Math]::Floor($budget - $watch.Elapsed.TotalSeconds))
                    Invoke-Check asset-export $current $pwsh @('-NoProfile', '-File', 'scripts/checks/run-asset-export.ps1',
                        '-OutputDirectory', (Join-Path $run 'asset-export'), '-GameExecutablePath', $binary,
                        '-TemplateAppData', $environment.APPDATA) ([int] [Math]::Floor($budget - $watch.Elapsed.TotalSeconds))
                }
            }
            if ($watch.Elapsed.TotalSeconds -gt $budget) { throw "$current took $($watch.Elapsed.TotalSeconds)s; limit ${budget}s." }
            if (-not ($summary.checks | Where-Object layer -eq $current | Measure-Object executed -Sum).Sum) { throw "$current executed no tests." }
        } finally {
            $layerSummary = @{ name = $current; selected = [bool] $selected; seconds = $watch.Elapsed.TotalSeconds; budget = $budget }
            foreach ($count in @('total', 'executed', 'passed', 'failed', 'skipped')) { $layerSummary[$count] = [int] ($summary.checks | Where-Object layer -eq $current | Measure-Object $count -Sum).Sum }
            $summary.layers += $layerSummary
        }
    }
    $summary.outcome = 'passed'
} catch { $summary.error = $_.Exception.Message; Write-Error $_ -ErrorAction Continue }
finally {
    if ($engineUserRoot -and (Test-Path -LiteralPath $engineUserRoot)) {
        try {
            $ownedUserRoot = [IO.Path]::GetFullPath($engineUserRoot)
            if (-not $ownedUserRoot.StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Engine user data escaped the run directory.' }
            $logs = Join-Path $ownedUserRoot 'logs'
            if (Test-Path -LiteralPath $logs) { Copy-Item -LiteralPath $logs -Destination (Join-Path $run 'engine-session-logs') -Recurse }
            Remove-Item -LiteralPath $ownedUserRoot -Recurse -Force
        } catch { $summary.outcome = 'failed'; $summary.error = "User data cleanup failed: $($_.Exception.Message)" }
    }
    $workspaceAfter = [Collections.Generic.HashSet[string]]::new([string[]] @(Get-Snapshot $root -Workspace), [StringComparer]::Ordinal)
    $summary.newFiles = @($workspaceAfter | Where-Object { -not $workspaceBefore.Contains($_) })
    $summary.removedFiles = @($workspaceBefore | Where-Object { -not $workspaceAfter.Contains($_) })
    $summary.userDataChanged = $userBefore -ne (@(Get-Snapshot $userRoot) -join "`n")
    $summary.gitAfter = @(git status --porcelain=v1)
    $summary.dirty = [bool] $summary.gitBefore.Count
    $summary.commitAfter = (git rev-parse HEAD).Trim()
    $summary.identityChanged = $summary.commit -ne $summary.commitAfter -or ($summary.gitBefore -join "`n") -ne ($summary.gitAfter -join "`n")
    $summary.seconds = $started.Elapsed.TotalSeconds
    $summary.dumps = @(Get-ChildItem $run -Filter '*.dmp' -File -Recurse | ForEach-Object { @{ path = $_.FullName; bytes = $_.Length } })
    if ($summary.newFiles.Count -or $summary.removedFiles.Count -or $summary.userDataChanged -or $summary.identityChanged) { $summary.outcome = 'failed' }
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name]) }
    $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'summary.json')
    if ($summary.outcome -eq 'passed') {
        $retained = @('summary.json')
        if ($ExportWindows) { $retained += @('exports', 'export-smoke', 'asset-export', 'export.log', 'export-godot.log', 'asset-export.log', 'export-smoke.log') }
        try { foreach ($artifact in (Get-ChildItem -LiteralPath $run -Force | Where-Object Name -notin $retained)) { Remove-Item -LiteralPath $artifact.FullName -Recurse -Force } }
        catch { $summary.outcome = 'failed'; $summary.error = "Cleanup failed: $($_.Exception.Message)"; $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'summary.json') }
    }
    $mutex.ReleaseMutex()
    $mutex.Dispose()
    Write-Host "SUMMARY=$run/summary.json outcome=$($summary.outcome) seconds=$($summary.seconds)"
}
if ($summary.outcome -ne 'passed') { exit 1 }
