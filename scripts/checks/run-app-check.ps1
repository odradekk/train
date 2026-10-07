# 统一运行已登记的 App 自检，并验证进程退出状态与可观察标记。
# 调用方负责提供隔离输出目录；导出检查还必须提供同一次 runner 准备的可执行文件。

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
    $contentRoot = Join-Path $output "content-input"
    if (-not (Test-Path -LiteralPath (Join-Path $contentRoot "initial") -PathType Container) -or
        -not (Test-Path -LiteralPath (Join-Path $contentRoot "added") -PathType Container)) {
        [Console]::Error.WriteLine("App check blocked: content input is incomplete: $contentRoot")
        exit 2
    }
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
    "content" { @("CONTENT_FERROUS_ORE_LOADED", "CONTENT_PNG_DECODED", "CONTENT_INIT_OK", "CONTENT_RELOAD_ACTION_PRESENT", "CONTENT_VFS_JSON_READABLE", "CONTENT_VFS_PNG_READABLE", "CONTENT_SMOKE_RELOADED", "CONTENT_SMOKE_REMOVED") }
    "mods-export" { @("MODS_EXPORT_FERROUS_ORE_LOADED", "MODS_EXPORT_PNG_DECODED", "MODS_EXPORT_INIT_OK") }
    "content-export" { @("CONTENT_EXPORT_FERROUS_ORE_LOADED", "CONTENT_EXPORT_PNG_DECODED", "CONTENT_EXPORT_INIT_OK", "CONTENT_EXPORT_JSON_READABLE", "CONTENT_EXPORT_PNG_READABLE", "CONTENT_EXPORT_JSONL_CREATED") }
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
