param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('starting-trade', 'lease-expiry', 'rail-repair', 'conquest')]
    [string] $Example,
    [string] $Root = (Join-Path $PSScriptRoot '../..')
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Root)
Set-Location $root
# Native child processes must also resolve global.json in the tested checkout.
[Environment]::CurrentDirectory = $root
$processHelper = Join-Path $PSScriptRoot 'Invoke-HiddenProcess.ps1'
$commit = (git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify tested commit.' }
$run = Join-Path $root "TestResults/long-example-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))-$($commit.Substring(0, 7))"
New-Item -ItemType Directory -Path $run | Out-Null
$recipe = Join-Path $root "examples/$Example/recipe.json"
$cli = Join-Path $root 'Tools/Cli/bin/Release/net10.0/Train.Cli.dll'
$saveStep = switch ($Example) {
    'starting-trade' { '432000' }
    'lease-expiry' { '5183999' }
    'rail-repair' { '959400' }
    'conquest' { '8000' }
}
$continuous = Join-Path $run 'continuous.json'
$prefix = Join-Path $run 'prefix.save.json'
$resumed = Join-Path $run 'resumed.json'
$remade = Join-Path $run 'remade.save.json'
$commands = [ordered]@{
    build = @{ arguments = @('build', 'Tools/Cli/Train.Cli.csproj', '-c', 'Release'); requires = @() }
    replay = @{ arguments = @($cli, 'replay', $root, $recipe, $continuous); requires = @('build') }
    save = @{ arguments = @($cli, 'save', $root, $recipe, $prefix, $saveStep); requires = @('build') }
    resume = @{ arguments = @($cli, 'resume', $root, $recipe, $prefix, $resumed); requires = @('save') }
    make = @{ arguments = @($cli, 'make', $root, $recipe, $remade); requires = @('build') }
    check = @{ arguments = @($cli, 'check', $root, $recipe, $remade); requires = @('make') }
    compare = @{ arguments = @($cli, 'compare', $continuous, $resumed); requires = @('replay', 'resume') }
}
$records = [ordered]@{}
foreach ($name in $commands.Keys) {
    $records[$name] = @{ name = $name; command = @('dotnet') + $commands[$name].arguments; status = 'not_started'; exitCode = $null; seconds = 0; cleanupSucceeded = $null }
}
$state = [hashtable]::Synchronized(@{ records = $records })
$summaryPath = Join-Path $run 'summary.json'
$identity = [ordered]@{ commit = $commit; gitBefore = @(git status --porcelain=v1); example = $Example; configuration = 'Release' }
([ordered]@{ identity = $identity; checks = @($records.Values); outcome = 'failed' } | ConvertTo-Json -Depth 10) |
    Set-Content -LiteralPath $summaryPath -Encoding utf8
$watch = [Diagnostics.Stopwatch]::StartNew()

# Each batch finishes before the next; only the three independent simulation groups overlap.
foreach ($batch in @(
    @{ groups = @(@{ names = @('build') }) },
    @{ groups = @(@{ names = @('replay') }, @{ names = @('save', 'resume') }, @{ names = @('make', 'check') }) },
    @{ groups = @(@{ names = @('compare') }) }
)) {
    $batch.groups | ForEach-Object -Parallel {
        $ErrorActionPreference = 'Stop'
        . $using:processHelper
        $shared = $using:state
        $definitions = $using:commands
        $directory = $using:run
        $summaryFile = $using:summaryPath
        $runIdentity = $using:identity
        function Write-Record($Record) {
            $Record | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $directory "$($Record.name).record.json") -Encoding utf8
            [Threading.Monitor]::Enter($shared.SyncRoot)
            try {
                $shared.records[$Record.name] = $Record.Clone()
                $summary = [ordered]@{ identity = $runIdentity; checks = @($shared.records.Values); outcome = 'failed' }
                $temporary = "$summaryFile.tmp"
                $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $temporary -Encoding utf8
                [IO.File]::Move($temporary, $summaryFile, $true)
            } finally {
                [Threading.Monitor]::Exit($shared.SyncRoot)
            }
        }
        foreach ($name in $_.names) {
            $definition = $definitions[$name]
            [Threading.Monitor]::Enter($shared.SyncRoot)
            try {
                $ready = @($definition.requires | Where-Object { $shared.records[$_].status -ne 'passed' }).Count -eq 0
            } finally {
                [Threading.Monitor]::Exit($shared.SyncRoot)
            }
            if (-not $ready) { continue }
            $record = @{ name = $name; command = @('dotnet') + $definition.arguments; status = 'running'; exitCode = $null; seconds = 0; cleanupSucceeded = $null }
            Write-Record $record
            try {
                $result = Invoke-HiddenProcess -FilePath 'dotnet' -ArgumentList $definition.arguments
                $record.exitCode = $result.ExitCode
                $record.seconds = $result.DurationMs / 1000
                $record.cleanupSucceeded = $result.CleanupSucceeded
                $record.status = if ($result.ExitCode -eq 0 -and $result.CleanupSucceeded) { 'passed' } else { 'failed' }
                $result.Output | Set-Content -LiteralPath (Join-Path $directory "$name.log") -Encoding utf8
            } catch {
                $record.status = 'failed'
                $_ | Out-String | Set-Content -LiteralPath (Join-Path $directory "$name.log") -Encoding utf8
            }
            Write-Record $record
            Write-Host "$name status=$($record.status) exit=$($record.exitCode) seconds=$($record.seconds)"
        }
    } -ThrottleLimit 3
}

$watch.Stop()
$passed = @($state.records.Values | Where-Object { $_.status -ne 'passed' }).Count -eq 0
$summary = [ordered]@{ identity = $identity; checks = @($state.records.Values); seconds = $watch.Elapsed.TotalSeconds; outcome = $(if ($passed) { 'passed' } else { 'failed' }) }
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding utf8
Write-Host "SUMMARY=$summaryPath outcome=$($summary.outcome) seconds=$($summary.seconds)"
if (-not $passed) { exit 1 }
