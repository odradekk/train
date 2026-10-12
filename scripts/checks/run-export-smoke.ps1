param(
    [Parameter(Mandatory)][string] $ExecutablePath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateRange(1, 2147478)][int] $TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-HiddenProcess.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$binary = [IO.Path]::GetFullPath($ExecutablePath)
if (-not $IsWindows -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw 'Windows export executable required.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$environment = @{}
foreach ($name in @('APPDATA', 'LOCALAPPDATA', 'TRAIN_LOG_LEVEL')) { $environment[$name] = [Environment]::GetEnvironmentVariable($name) }
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    $env:TRAIN_LOG_LEVEL = 'info'
    $emptyAccepted = $null
    foreach ($case in @('empty', 'mod')) {
        $ownsUserData = $false
        $userData = $null
        try {
            $directory = Join-Path $output $case
            $env:APPDATA = Join-Path $directory 'appdata'
            $env:LOCALAPPDATA = Join-Path $directory 'localappdata'
            foreach ($path in @($env:APPDATA, $env:LOCALAPPDATA)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
            $userData = Join-Path $env:APPDATA 'Godot/app_userdata/Train'
            if (Test-Path -LiteralPath $userData) { throw "Smoke requires a fresh run directory: $userData" }
            $ownsUserData = $true
            if ($case -eq 'mod') {
                $mod = Join-Path $userData 'mods/smoke-fixture'
                $resources = Join-Path $mod 'resources/voxels'
                New-Item -ItemType Directory -Path (Join-Path $resources 'grain') -Force | Out-Null
                @{ schema_version = 1; id = '550e8400-e29b-41d4-a716-446655440000'; name = 'Export smoke'; version = '1.0.0'; authors = @('Tests') } |
                    ConvertTo-Json | Set-Content (Join-Path $mod 'mod.json')
                $definition = Get-Content (Join-Path $root 'builtin/core/resources/voxels/grain.voxels.json') -Raw | ConvertFrom-Json
                $definition.voxels[0].id = 'smoke-grain'
                $definition | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $resources 'smoke-grain.voxels.json')
                Copy-Item -LiteralPath (Join-Path $root 'builtin/core/resources/voxels/grain/placeholder.png') -Destination (Join-Path $resources 'grain/placeholder.png')
            }
            $remaining = [int] [Math]::Floor($TimeoutSeconds - $watch.Elapsed.TotalSeconds)
            if ($remaining -lt 1) { throw 'Export smoke exhausted its time budget.' }
            $arguments = @('--headless', '--log-file', (Join-Path $directory 'godot.log'), '--', '--smoke')
            $result = Invoke-HiddenProcess -FilePath $binary -ArgumentList $arguments -TimeoutSeconds $remaining
            $result.Output | Set-Content (Join-Path $directory 'process.log')
            Write-Host $result.Output
            if ($result.ExitCode -ne 0 -or $result.TimedOut -or -not $result.CleanupSucceeded) { throw "Export smoke $case failed (exit=$($result.ExitCode), timeout=$($result.TimedOut))." }
            $logs = @(Get-ChildItem -LiteralPath (Join-Path $userData 'logs') -Filter 'train-session-*.jsonl' -File)
            if ($logs.Count -ne 1) { throw "Export smoke $case did not produce one JSONL session." }
            $events = @(Get-Content -LiteralPath $logs[0].FullName | ForEach-Object { $_ | ConvertFrom-Json })
            $reload = $events | Where-Object { $_.level -eq 'info' -and $_.event_id -eq 'content_reload_summary' } | Select-Object -Last 1
            if (-not $reload -or $reload.attributes.status -notin @('Committed', 'CommittedWithWarnings')) { throw "Export smoke $case did not commit content." }
            Write-Host "$case reload: $($reload | ConvertTo-Json -Depth 10 -Compress)"
            if ($case -eq 'empty') { $emptyAccepted = [int] $reload.attributes.accepted }
            else {
                $mods = $events | Where-Object { $_.level -eq 'info' -and $_.event_id -eq 'content_mods_scan_summary' -and $_.attributes.PSObject.Properties.Name -contains 'isolated_count' } | Select-Object -Last 1
                Write-Host "mod scan: $($mods | ConvertTo-Json -Depth 10 -Compress)"
                if (-not $mods -or $mods.attributes.accepted_count -ne 1 -or $mods.attributes.isolated_count -ne 0 -or [int] $reload.attributes.accepted -ne $emptyAccepted + 1) {
                    throw 'Export smoke did not load the valid Mod fixture into published content.'
                }
            }
        }
        finally {
            if ($ownsUserData -and (Test-Path -LiteralPath $userData)) {
                $ownedUserRoot = [IO.Path]::GetFullPath($userData)
                if (-not $ownedUserRoot.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Smoke user data escaped its output directory.' }
                $logs = Join-Path $ownedUserRoot 'logs'
                if (Test-Path -LiteralPath $logs) { Copy-Item -LiteralPath $logs -Destination (Join-Path $directory 'session-logs') -Recurse }
                Remove-Item -LiteralPath $ownedUserRoot -Recurse -Force
            }
        }
    }
}
finally { foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name]) } }
