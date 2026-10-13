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
    $emptyPackages = $null
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
                New-Item -ItemType Directory -Path (Join-Path $resources 'shared') -Force | Out-Null
                @{ schema_version = 2; id = '550e8400-e29b-41d4-a716-446655440000'; name = 'Export smoke'; version = '1.0.0'; authors = @('Tests') } |
                    ConvertTo-Json | Set-Content (Join-Path $mod 'mod.json')
                $definition = Get-Content (Join-Path $root 'builtin/core/resources/voxels/staple-food.voxel.json') -Raw | ConvertFrom-Json
                $definition.id = 'smoke-food'
                $definition | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $resources 'smoke-food.voxel.json')
                foreach ($image in @('organic-icon.png', 'organic-face.png')) {
                    Copy-Item -LiteralPath (Join-Path $root "builtin/core/resources/voxels/shared/$image") -Destination (Join-Path $resources "shared/$image")
                }
            }
            $remaining = [int] [Math]::Floor($TimeoutSeconds - $watch.Elapsed.TotalSeconds)
            if ($remaining -lt 1) { throw 'Export smoke exhausted its time budget.' }
            $arguments = @('--headless', '--log-file', (Join-Path $directory 'godot.log'), '--', '--smoke')
            $result = Invoke-HiddenProcess -FilePath $binary -ArgumentList $arguments -TimeoutSeconds $remaining
            $result.Output | Set-Content (Join-Path $directory 'process.log')
            Write-Host $result.Output
            if ($result.ExitCode -ne 0 -or $result.TimedOut -or -not $result.CleanupSucceeded) { throw "Export smoke $case failed (exit=$($result.ExitCode), timeout=$($result.TimedOut))." }
            $errors = @(Get-Content -LiteralPath (Join-Path $directory 'godot.log') | Where-Object { $_ -match '^\s*(?:SCRIPT |USER |FATAL )?ERROR:' })
            if ($errors.Count) { throw "Export smoke $case reported engine or script errors: $($errors -join '; ')" }
            $logs = @(Get-ChildItem -LiteralPath (Join-Path $userData 'logs') -Filter 'train-session-*.jsonl' -File)
            if ($logs.Count -ne 1) { throw "Export smoke $case did not produce one JSONL session." }
            $events = @(Get-Content -LiteralPath $logs[0].FullName | ForEach-Object { $_ | ConvertFrom-Json })
            $reload = $events | Where-Object { $_.level -eq 'info' -and $_.event_id -eq 'content_reload_summary' } | Select-Object -Last 1
            if (-not $reload -or $reload.attributes.status -notin @('Committed', 'CommittedWithWarnings')) { throw "Export smoke $case did not commit content." }
            Write-Host "$case reload: $($reload | ConvertTo-Json -Depth 10 -Compress)"
            $smoke = $events | Where-Object { $_.level -eq 'info' -and $_.event_id -eq 'application_smoke_summary' } | Select-Object -Last 1
            if (-not $smoke -or $smoke.attributes.passed -ne $true -or 'core' -notin $smoke.attributes.packages -or
                'core:staple-food' -notin $smoke.attributes.voxels) { throw "Export smoke $case did not validate the production content and PNG." }
            $sourceId = [Guid]::Empty
            $saveId = [Guid]::Empty
            if (-not [Guid]::TryParse([string]$smoke.attributes.example_source_id, [ref]$sourceId) -or
                -not [Guid]::TryParse([string]$smoke.attributes.save_id, [ref]$saveId) -or $sourceId -eq $saveId -or
                $smoke.attributes.restored -ne $true -or $smoke.attributes.completed_steps -ne 200 -or
                $smoke.attributes.sequence -ne 0 -or $smoke.attributes.paused -ne $true -or
                $smoke.attributes.unsaved -ne $true -or $smoke.attributes.example_button_visible -ne $false) {
                throw "Export smoke $case did not restore the packaged example with a new unsaved identity and hidden player entry."
            }
            if ($case -eq 'empty') { $emptyPackages = @($smoke.attributes.packages).Count }
            else {
                Write-Host "mod content: $($smoke | ConvertTo-Json -Depth 10 -Compress)"
                if ('550e8400-e29b-41d4-a716-446655440000' -notin $smoke.attributes.packages -or
                    '550e8400-e29b-41d4-a716-446655440000:smoke-food' -notin $smoke.attributes.voxels -or
                    @($smoke.attributes.packages).Count -ne $emptyPackages + 1) {
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
