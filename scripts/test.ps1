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
if (@($layers | Where-Object { $_ -notin @('fast', 'integration', 'engine', 'stage', 'all') }).Count) { throw 'Unknown test layer.' }
if ('all' -in $layers) { $layers = @('fast', 'integration', 'engine') + @($layers | Where-Object { $_ -eq 'stage' }) }
$Project = @(($Project -join ',') -split ',' | Where-Object { $_ } | Select-Object -Unique)
if (@($Project | Where-Object { $_ -notin @('core', 'content', 'godot', 'contract-support', 'contract-checks') }).Count) { throw 'Unknown test project.' }
if ($All -and $Project.Count) { throw '-All and -Project cannot be combined.' }
if ($ExportWindows -and ('engine' -notin $layers -or $Project.Count -or -not $IsWindows)) { throw '-ExportWindows requires engine, no -Project, and Windows.' }
if ($Project.Count) { $layers = @($layers | Where-Object { $_ -notin @('engine', 'stage') }) }
if (-not $layers.Count) { throw 'Selected layers match no MSTest tests.' }
$projects = @(
    @{ name = 'core'; path = 'Tests/Core/Train.Core.Tests.csproj'; fast = 245; integration = 4 },
    @{ name = 'content'; path = 'Tests/Content/Train.Content.Tests.csproj'; fast = 189; integration = 7 },
    @{ name = 'godot'; path = 'Tests/Godot/Train.Godot.Tests.csproj'; fast = 12; integration = 35 },
    @{ name = 'contract-support'; path = 'Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj'; fast = 13; integration = 0 },
    @{ name = 'contract-checks'; path = 'Tests/ContractChecks.Tests/Train.ContractChecks.Tests.csproj'; fast = 1; integration = 4 }
)
if ($Project.Count) { $projects = @($projects | Where-Object { $_.name -in $Project }) }
$commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE) { throw 'Cannot identify tested commit.' }
$results = Join-Path $root 'TestResults'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$mutex = [Threading.Mutex]::new($false, 'Train.Tests.' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($root))))
try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
if (-not $acquired) { $mutex.Dispose(); throw 'Another test run owns this worktree.' }
$runs = @(Get-ChildItem $results -Directory | Where-Object Name -match '^(fast|integration|engine|stage)(-(fast|integration|engine|stage))*-\d{8}T\d{9}-[0-9a-f]{7}$' | Sort-Object { ($_.Name -split '-')[-2] } -Descending)
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
$contractTool = Join-Path $root 'Tests/ContractChecks/bin/Debug/net10.0/Train.ContractChecks.dll'
$contractBaseline = Join-Path $root 'docs/contracts/public-api.txt'
$started = [Diagnostics.Stopwatch]::StartNew()
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
    } elseif ($Name -match '^(fast|integration)-(core|content|godot|contract-support|contract-checks)$') {
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
function Remove-IsolationTrees([string] $Output) {
    # 外层超时会强杀工具，无法执行其 finally；只清理本次输出内的隔离树。
    if (Test-Path -LiteralPath $Output) {
        foreach ($tree in (Get-ChildItem -LiteralPath $Output -Directory | Where-Object Name -match '^isolated-[0-9a-f]{8}$')) {
            if (-not ([IO.Path]::GetFullPath($tree.FullName).StartsWith($run + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal))) { throw 'Isolation cleanup escaped the run directory.' }
            Remove-Item -LiteralPath $tree.FullName -Recurse -Force
        }
    }
}
function Invoke-Isolation([string] $Name, [string] $Group, [string] $Output, [int] $Seconds) {
    try { Invoke-Check $Name $Group dotnet @($contractTool, 'isolate', $root, 'Debug', $Output) $Seconds }
    finally { Remove-IsolationTrees $Output }
}
try {
    if ($layers.Count -eq 1 -and $layers[0] -eq 'stage') {
        $summary.selection = @{ mode = 'stage'; baseRef = $BaseRef; projects = @(); contracts = $true; isolation = $true; schema = $false }
    } else {
        $summary.selection = Get-TestSelection -BaseRef $BaseRef -All:$All -Project $Project -Projects $projects -OutputDirectory $run
    }
    $summary.selection.layers = $layers
    $summary.selection.exportWindows = [bool] $ExportWindows
    $projects = @($projects | Where-Object { $_.name -in $summary.selection.projects })
    $selectedLayers = @($layers | Where-Object {
        $_ -in @('engine', 'stage') -or $projects.Count -or ($_ -eq 'integration' -and ($summary.selection.contracts -or $summary.selection.isolation -or $summary.selection.schema))
    })
    if ($selectedLayers.Count -and ($projects.Count -or $summary.selection.contracts -or $summary.selection.isolation -or 'engine' -in $selectedLayers -or 'stage' -in $selectedLayers)) {
        Invoke-Check build preparation dotnet @('build', 'Train.sln', '--nologo') 300
    } elseif (-not $selectedLayers.Count) { Write-Host 'No affected test projects or checks selected; no build or tests required.' }
    foreach ($current in $layers) {
        $budget = @{ fast = 30; integration = 180; engine = 300 }[$current]
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $selected = $current -in $selectedLayers
        try {
            if (-not $selected) { Write-Host "LAYER=$current not selected"; continue }
            if ($current -eq 'stage') {
                Invoke-Check stage-structure $current dotnet @($contractTool, 'structure', $root, 'Debug', (Join-Path $run 'stage-structure')) 300
                Invoke-Check stage-verify $current dotnet @($contractTool, 'verify', $root, 'Debug', $contractBaseline) 120
                Invoke-Isolation stage-isolate $current (Join-Path $run 'stage-isolate') 300
            } elseif ($current -ne 'engine') {
                $deadline = [DateTime]::UtcNow.AddSeconds($budget)
                $helper = Join-Path $PSScriptRoot 'checks/Invoke-HiddenProcess.ps1'
                $tasks = @($projects | ForEach-Object {
                    @{ name = "$current-$($_.name)"; program = 'dotnet'; project = $_;
                        priority = if ($current -eq 'integration' -and $_.name -eq 'contract-checks') { 3 } else { 0 } }
                })
                $isolationOutput = Join-Path $run 'isolation'
                if ($current -eq 'integration') {
                    if ($summary.selection.contracts) {
                        $tasks += @{ name = 'structure'; program = 'dotnet'; priority = 2; arguments = @($contractTool, 'structure', $root, 'Debug', (Join-Path $run 'contracts')) }
                        $tasks += @{ name = 'verify'; program = 'dotnet'; priority = 0; arguments = @($contractTool, 'verify', $root, 'Debug', $contractBaseline) }
                    }
                    if ($summary.selection.isolation) { $tasks += @{ name = 'isolate'; program = 'dotnet'; priority = 2; arguments = @($contractTool, 'isolate', $root, 'Debug', $isolationOutput) } }
                    if ($summary.selection.schema) { $tasks += @{ name = 'schema'; program = $pwsh; priority = 0; arguments = @('-NoProfile', '-File', 'scripts/checks/validate-content-examples.ps1') } }
                }
                $throttle = if ($current -eq 'integration') { 4 } else { [Math]::Max(1, $projects.Count) }
                $results = @()
                $failure = $null
                try { if ($tasks.Count) { $results = @($tasks | Sort-Object { $_.priority } -Descending -Stable | ForEach-Object -Parallel {
                    $task = $_
                    . $using:helper
                    $current = $using:current
                    $remaining = [int] [Math]::Floor(($using:deadline - [DateTime]::UtcNow).TotalSeconds)
                    $arguments = $task.arguments
                    if ($task.project) {
                        $minimum = $task.project[$current]
                        $filter = if ($current -eq 'fast') { 'TestCategory!=integration' } else { 'TestCategory=integration' }
                        $arguments = @('test', '--project', $task.project.path, '--no-build', '--results-directory', $using:run)
                        if ($minimum) { $arguments += @('--minimum-expected-tests', "$minimum") }
                        $arguments += @('--', '--filter', $filter, '--report-trx', '--report-trx-filename', "$($task.name).trx", '--timeout', "$([Math]::Max(1, $remaining))s", '--hangdump', '--hangdump-type', 'Mini', '--hangdump-timeout', "$([Math]::Max(1, $remaining - 5))s")
                        if (-not $minimum) { $arguments += @('--ignore-exit-code', '8') }
                    }
                    $result = if ($remaining -lt 1) {
                        [pscustomobject]@{ ExitCode = -1; Output = "$current exhausted its time budget."; TimedOut = $true; CleanupSucceeded = $true; DurationMs = 0 }
                    } else {
                        try { Invoke-HiddenProcess -FilePath $task.program -ArgumentList $arguments -TimeoutSeconds $remaining }
                        catch { [pscustomobject]@{ ExitCode = -1; Output = $_.Exception.Message; TimedOut = $false; CleanupSucceeded = $false; DurationMs = 0 } }
                    }
                    [pscustomobject]@{ name = $task.name; program = $task.program; arguments = $arguments; result = $result }
                } -ThrottleLimit $throttle) } }
                finally {
                    if ($current -eq 'integration' -and $summary.selection.isolation) {
                        try { Remove-IsolationTrees $isolationOutput }
                        catch { $failure = $_ }
                    }
                }
                foreach ($item in $results) {
                    try { Add-CheckResult $item.name $current $item.program $item.arguments $item.result }
                    catch { if (-not $failure) { $failure = $_ } }
                }
                if ($failure) { throw $failure }
                if ($results.Count -ne $tasks.Count) { throw "$current did not complete every selected task." }
            } else {
                $watch.Stop()
                $paths = if ($IsWindows) { @{ APPDATA = 'appdata'; LOCALAPPDATA = 'localappdata' } } else { @{ XDG_CONFIG_HOME = 'config'; XDG_DATA_HOME = 'data'; XDG_CACHE_HOME = 'cache' } }
                foreach ($name in $paths.Keys) {
                    $environment[$name] = [Environment]::GetEnvironmentVariable($name)
                    $directory = Join-Path $run $paths[$name]
                    New-Item -ItemType Directory -Force -Path $directory | Out-Null
                    [Environment]::SetEnvironmentVariable($name, $directory)
                }
                Invoke-Check import preparation $env:GODOT @('--headless', '--path', $root, '--recovery-mode', '--log-file', (Join-Path $run 'import-godot.log'), '--editor', '--import') 120
                $checks = @('logging', 'content', 'main-menu', 'simulation-host')
                $binary = Join-Path $run 'exports/windows/train.exe'
                if ($ExportWindows) {
                    New-Item -ItemType Directory -Path (Split-Path $binary) -Force | Out-Null
                    $env:APPDATA = $environment.APPDATA
                    try { Invoke-Check export preparation $env:GODOT @('--headless', '--path', $root, '--recovery-mode', '--log-file', (Join-Path $run 'export-godot.log'), '--export-release', 'Windows Desktop', $binary) 120 }
                    finally { $env:APPDATA = Join-Path $run $paths.APPDATA }
                    $checks += @('content-export', 'mods-export')
                }
                $watch.Start()
                foreach ($check in $checks) {
                    $arguments = @('-NoProfile', '-File', 'scripts/checks/run-app-check.ps1', '-Check', $check, '-OutputDirectory', (Join-Path $run $check), '-TimeoutSeconds', "$([Math]::Max(1, [int] [Math]::Floor($budget - $watch.Elapsed.TotalSeconds) - 5))")
                    if ($check -like '*-export') { $arguments += @('-ExecutablePath', $binary) }
                    Invoke-Check $check $current $pwsh $arguments ([int] [Math]::Floor($budget - $watch.Elapsed.TotalSeconds))
                }
            }
            if ($current -ne 'stage' -and $watch.Elapsed.TotalSeconds -gt $budget) { throw "$current took $($watch.Elapsed.TotalSeconds)s; limit ${budget}s." }
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
        try { foreach ($artifact in (Get-ChildItem -LiteralPath $run -Force | Where-Object Name -ne 'summary.json')) { Remove-Item -LiteralPath $artifact.FullName -Recurse -Force } }
        catch { $summary.outcome = 'failed'; $summary.error = "Cleanup failed: $($_.Exception.Message)"; $summary | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $run 'summary.json') }
    }
    $mutex.ReleaseMutex()
    $mutex.Dispose()
    Write-Host "SUMMARY=$run/summary.json outcome=$($summary.outcome) seconds=$($summary.seconds)"
}
if ($summary.outcome -ne 'passed') { exit 1 }
