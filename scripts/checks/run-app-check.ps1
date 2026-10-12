
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("logging", "content", "mods-export", "content-export", "main-menu", "simulation-host")]
    [string] $Check,
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,
    [string] $ExecutablePath = "",
    [ValidateRange(1, 2147478)]
    [int] $TimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot "Invoke-HiddenProcess.ps1")

function New-ContentCheckInput {
    param([string] $CaseDirectory)
    $contentRoot = Join-Path $CaseDirectory "content-input"
    $sourceResources = Join-Path $root "builtin/core/resources"
    foreach ($state in @("initial", "added")) {
        $resources = Join-Path $contentRoot $state
        $voxels = Join-Path $resources "voxels"
        $images = Join-Path $voxels "grain"
        New-Item -ItemType Directory -Force -Path $images | Out-Null
        Copy-Item -LiteralPath (Join-Path $sourceResources "voxels/grain.voxels.json") -Destination $voxels
        Copy-Item -LiteralPath (Join-Path $sourceResources "voxels/grain/placeholder.png") -Destination $images
        $sensitivity = if ($state -eq "added") { 0.75 } else { 0.5 }
        $capacity = if ($state -eq "added") { 3 } else { 2 }
        $market = [ordered]@{
            schema_version = 1
            market_tables = @([ordered]@{
                id = "smoke-prices"
                goods = @([ordered]@{ definition = "core:grain"; sensitivity = $sensitivity; min_price = 1; max_price = 1000 })
            })
        }
        $settlement = [ordered]@{
            schema_version = 1
            settlement_types = @([ordered]@{
                id = "smoke-settlement"; name = "Smoke settlement"
                supply = @([ordered]@{ definition = "core:grain"; capacity = $capacity; restore_per_tick = 1 })
                demand = @(); preferred_terrain = @(); weight = 1
                population_min = 1; population_max = 1; initial_platform_count = 0
            })
        }
        foreach ($directory in @("markets", "settlement-types")) {
            New-Item -ItemType Directory -Force -Path (Join-Path $resources $directory) | Out-Null
        }
        [System.IO.File]::WriteAllText((Join-Path $resources "markets/smoke.market-tables.json"), ($market | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText((Join-Path $resources "settlement-types/smoke.settlement-types.json"), ($settlement | ConvertTo-Json -Depth 10), [System.Text.UTF8Encoding]::new($false))
    }
    $sourceDefinition = Join-Path $sourceResources "voxels/grain.voxels.json"
    $probe = [System.IO.File]::ReadAllText($sourceDefinition) | ConvertFrom-Json
    $probe.voxels[0].id = "smoke-ore"
    $probe.voxels[0].name = "Smoke Ore"
    $smokeDefinition = $probe | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $contentRoot "added/voxels/smoke-ore.voxels.json"), $smokeDefinition, [System.Text.UTF8Encoding]::new($false))
    return $contentRoot
}

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$userData = Join-Path $output "user-data"
New-Item -ItemType Directory -Force -Path $userData | Out-Null

$isExportCheck = $Check -in @("mods-export", "content-export")
$hasExecutable = -not [string]::IsNullOrWhiteSpace($ExecutablePath)
if ($hasExecutable) {
    if (-not $IsWindows) {
        [Console]::Error.WriteLine("App check blocked: Windows executable checks require Windows.")
        exit 2
    }
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        [Console]::Error.WriteLine("App check blocked: exported executable not found: $ExecutablePath")
        exit 2
    }
    $program = [System.IO.Path]::GetFullPath($ExecutablePath)
    $arguments = @("--headless")
}
elseif ($isExportCheck) {
    [Console]::Error.WriteLine("App check blocked: -ExecutablePath is required for export checks.")
    exit 2
}
else {
    if ([string]::IsNullOrWhiteSpace($env:GODOT) -or -not [System.IO.Path]::IsPathRooted($env:GODOT) -or -not (Test-Path -LiteralPath $env:GODOT -PathType Leaf)) {
        [Console]::Error.WriteLine("App check blocked: GODOT must be an absolute executable path.")
        exit 2
    }
    $program = $env:GODOT
    $arguments = @("--headless", "--path", $root)
}

# Windows 上 Godot 的默认 user:// 路径不会随 APPDATA 覆盖改变；原生日志也要显式定向。
$arguments += @("--log-file", (Join-Path $output "godot.log"), "--", "--check=$Check", "--check-user-data=$userData")
if ($Check -eq "content") {
    $contentRoot = New-ContentCheckInput -CaseDirectory $output
    $arguments += "--check-content-root=$contentRoot"
}

try {
    $run = Invoke-HiddenProcess -FilePath $program -ArgumentList $arguments -TimeoutSeconds $TimeoutSeconds
}
catch {
    [Console]::Error.WriteLine("App check blocked: $Check process could not be completed: $($_.Exception.Message)")
    exit 2
}
$text = [string] $run.Output
Write-Host $text
if ($run.TimedOut) {
    $cleanup = if ($run.CleanupSucceeded) { "owned process tree cleaned" } else { "owned process cleanup not proven" }
    [Console]::Error.WriteLine("App check blocked: $Check timed out after $TimeoutSeconds seconds; $cleanup.")
    exit 2
}
if (-not $run.CleanupSucceeded) {
    [Console]::Error.WriteLine("App check blocked: $Check owned process cleanup was not proven.")
    exit 2
}

$required = switch ($Check) {
    "logging" { @("[application][application_ready]", "smoke_worker_event", "SMOKE_PROCESS_DRAINED", "SMOKE_LOGGER_DISPOSED") }
    "content" { @("CONTENT_GRAIN_LOADED", "CONTENT_PNG_DECODED", "CONTENT_INIT_OK", "CONTENT_RELOAD_ACTION_PRESENT", "CONTENT_VFS_JSON_READABLE", "CONTENT_VFS_PNG_READABLE", "CONTENT_MARKET_SETTLEMENT_READY", "CONTENT_SMOKE_RELOADED", "CONTENT_SMOKE_REMOVED") }
    "mods-export" { @("MODS_EXPORT_GRAIN_LOADED", "MODS_EXPORT_PNG_DECODED", "MODS_EXPORT_INIT_OK") }
    "content-export" { @("CONTENT_EXPORT_GRAIN_LOADED", "CONTENT_EXPORT_PNG_DECODED", "CONTENT_EXPORT_INIT_OK", "CONTENT_EXPORT_JSON_READABLE", "CONTENT_EXPORT_PNG_READABLE", "CONTENT_EXPORT_JSONL_CREATED") }
    "main-menu" { @("MAIN_MENU_READY", "MAIN_MENU_QUIT_ONLY", "MAIN_MENU_QUIT_REQUESTED") }
    "simulation-host" { @("SIMULATION_NO_SESSION", "SIMULATION_SESSION_ATTACHED", "SIMULATION_MAIN_THREAD", "SIMULATION_AUTO_ADVANCED", "SIMULATION_PAUSED", "SIMULATION_CONSOLE_STEP", "SIMULATION_COMMAND_COMPLETED", "SIMULATION_CHECKPOINT", "SIMULATION_SNAPSHOT_STABLE", "SIMULATION_SESSION_DETACHED", "SIMULATION_AUTO_FAULT_DIAGNOSTIC", "SIMULATION_MANUAL_FAULT_DIAGNOSTIC", "SIMULATION_COMMAND_FAULT_DIAGNOSTIC") }
}
$required += "APP_CHECK_PASSED $Check"
$missing = @($required | Where-Object { $text.IndexOf($_, [StringComparison]::Ordinal) -lt 0 })
if ($Check -eq "mods-export") {
    if ($text -notlike "*MODS_EXPORT_ROOT_EMPTY*" -and $text -notlike "*MODS_EXPORT_ROOT_UNAVAILABLE*") {
        $missing += "MODS_EXPORT_ROOT_EMPTY|MODS_EXPORT_ROOT_UNAVAILABLE"
    }
    if ($text -like "*MODS_EXPORT_ROOT_PRESENT*") {
        $missing += "missing/empty mods root"
    }
}

if ($run.ExitCode -ne 0 -or $text -like "*APP_CHECK_FAILED*" -or $missing.Count -gt 0) {
    $detail = "exit=$($run.ExitCode)"
    if ($missing.Count -gt 0) { $detail += "; missing=$($missing -join ', ')" }
    [Console]::Error.WriteLine("App check failed: $Check ($detail)")
    exit 1
}

Write-Host "App check passed: $Check"
exit 0
