# 以假的本机进程验证统一 App 检查脚本的参数、标记和退出码编排。
# 本文件不启动 Godot，也不证明游戏内断言或 Windows 导出本身。

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ProcessFixtures.ps1")
$pwsh = Get-TestPowerShellPath
$subject = Join-Path $PSScriptRoot "run-app-check.ps1"
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("train-app-check harness " + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $root | Out-Null
$failed = 0
$owned = [System.Collections.Generic.List[string]]::new()
$ownedDirectories = [System.Collections.Generic.List[object]]::new()

function Invoke-Check {
    param([string] $Check, [string] $Program, [switch] $Export, [switch] $WithContent, [int] $TimeoutSeconds = 30)
    $output = Join-Path $root ("output " + [guid]::NewGuid().ToString("N"))
    if ($WithContent) {
        New-Item -ItemType Directory -Force -Path (Join-Path $output "content-input/initial") | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $output "content-input/added") | Out-Null
    }
    $beforeGodot = $env:GODOT
    try {
        $arguments = @("-NoProfile", "-File", $subject, "-Check", $Check, "-OutputDirectory", $output, "-TimeoutSeconds", $TimeoutSeconds)
        if ($Export) { $arguments += @("-ExecutablePath", $Program) } else { $env:GODOT = $Program }
        & $pwsh @arguments 2>&1 | Out-Host
        return $LASTEXITCODE
    }
    finally {
        if ($null -eq $beforeGodot) { Remove-Item Env:GODOT -ErrorAction SilentlyContinue } else { $env:GODOT = $beforeGodot }
    }
}

function Assert-Exit([string] $Name, [int] $Expected, [int] $Actual) {
    if ($Expected -ne $Actual) { Write-Host "FAIL $Name expected=$Expected actual=$Actual"; $script:failed++ }
    else { Write-Host "PASS $Name" }
}

function Test-Alive([int] $ProcessId) {
    try { return -not (Get-Process -Id $ProcessId -ErrorAction Stop).HasExited } catch { return $false }
}

try {
    $logging = New-FakeNativeProgram "app-check-logging" 0 @("[application][application_ready]", "smoke_worker_event", "SMOKE_PROCESS_DRAINED", "SMOKE_LOGGER_DISPOSED", "APP_CHECK_PASSED logging")
    $content = New-FakeNativeProgram "app-check-content" 0 @("CONTENT_FERROUS_ORE_LOADED", "CONTENT_PNG_DECODED", "CONTENT_INIT_OK", "CONTENT_RELOAD_ACTION_PRESENT", "CONTENT_VFS_JSON_READABLE", "CONTENT_VFS_PNG_READABLE", "CONTENT_SMOKE_RELOADED", "CONTENT_SMOKE_REMOVED", "APP_CHECK_PASSED content")
    $menu = New-FakeNativeProgram "app-check-menu" 0 @("MAIN_MENU_READY", "MAIN_MENU_QUIT_ONLY", "MAIN_MENU_QUIT_REQUESTED", "APP_CHECK_PASSED main-menu")
    $simulation = New-FakeNativeProgram "app-check-simulation" 0 @("SIMULATION_NO_SESSION", "SIMULATION_SESSION_ATTACHED", "SIMULATION_MAIN_THREAD", "SIMULATION_AUTO_ADVANCED", "SIMULATION_PAUSED", "SIMULATION_CONSOLE_STEP", "SIMULATION_COMMAND_COMPLETED", "SIMULATION_CHECKPOINT", "SIMULATION_SNAPSHOT_STABLE", "SIMULATION_SESSION_DETACHED", "APP_CHECK_PASSED simulation-host")
    $contentExport = New-FakeNativeProgram "app-check-content-export" 0 @("CONTENT_EXPORT_FERROUS_ORE_LOADED", "CONTENT_EXPORT_PNG_DECODED", "CONTENT_EXPORT_INIT_OK", "CONTENT_EXPORT_JSON_READABLE", "CONTENT_EXPORT_PNG_READABLE", "CONTENT_EXPORT_JSONL_CREATED", "APP_CHECK_PASSED content-export")
    $modsExport = New-FakeNativeProgram "app-check-mods-export" 0 @("MODS_EXPORT_FERROUS_ORE_LOADED", "MODS_EXPORT_PNG_DECODED", "MODS_EXPORT_INIT_OK", "MODS_EXPORT_ROOT_EMPTY", "APP_CHECK_PASSED mods-export")
    $missing = New-FakeNativeProgram "app-check-missing" 0 @("APP_CHECK_PASSED logging")
    $missingReady = New-FakeNativeProgram "app-check-missing-ready" 0 @("smoke_worker_event", "SMOKE_PROCESS_DRAINED", "SMOKE_LOGGER_DISPOSED", "APP_CHECK_PASSED logging")
    $failedProgram = New-FakeNativeProgram "app-check-failed" 7 @("APP_CHECK_FAILED logging")
    foreach ($path in @($logging, $content, $menu, $simulation, $contentExport, $modsExport, $missing, $missingReady, $failedProgram)) { $owned.Add($path) }

    Assert-Exit "logging markers" 0 (Invoke-Check logging $logging)
    Assert-Exit "logging through supplied executable" 0 (Invoke-Check logging $logging -Export)
    Assert-Exit "content isolated input" 0 (Invoke-Check content $content -WithContent)
    Assert-Exit "main menu markers" 0 (Invoke-Check main-menu $menu)
    Assert-Exit "simulation host markers" 0 (Invoke-Check simulation-host $simulation)
    Assert-Exit "content export markers and spaced paths" 0 (Invoke-Check content-export $contentExport -Export)
    Assert-Exit "mods export empty root" 0 (Invoke-Check mods-export $modsExport -Export)
    Assert-Exit "missing marker" 1 (Invoke-Check logging $missing)
    Assert-Exit "missing literal application ready marker" 1 (Invoke-Check logging $missingReady)
    Assert-Exit "nonzero child" 1 (Invoke-Check logging $failedProgram)
    Assert-Exit "missing exported executable" 2 (Invoke-Check content-export (Join-Path $root "missing.exe") -Export)
    if ($IsWindows) {
        $pidFile = Join-Path $root "hang-pids.txt"
        $hangScript = Join-Path $root "hang.ps1"
        $hangProgram = Join-Path $root "hang.cmd"
        $hangSource = @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Ignored)
$child = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 120") -PassThru -WindowStyle Hidden
Set-Content -LiteralPath $env:APP_CHECK_HANG_PIDS -Value "$PID,$($child.Id)" -Encoding ascii
Start-Sleep -Seconds 120
'@
        [System.IO.File]::WriteAllText($hangScript, $hangSource)
        [System.IO.File]::WriteAllText($hangProgram, "@echo off`r`n`"$pwsh`" -NoProfile -File `"$hangScript`"`r`n")
        $previousPidFile = $env:APP_CHECK_HANG_PIDS
        try {
            $env:APP_CHECK_HANG_PIDS = $pidFile
            Assert-Exit "hung process is blocked" 2 (Invoke-Check logging $hangProgram -TimeoutSeconds 3)
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            while (-not (Test-Path -LiteralPath $pidFile) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
            if (-not (Test-Path -LiteralPath $pidFile)) {
                Write-Host "FAIL hung process did not publish owned pids"
                $script:failed++
            }
            else {
                foreach ($ownedPid in ([System.IO.File]::ReadAllText($pidFile).Split(','))) {
                    if (Test-Alive ([int] $ownedPid)) { Write-Host "FAIL owned process still alive $ownedPid"; $script:failed++ }
                    else { Write-Host "PASS owned process stopped $ownedPid" }
                }
            }
        }
        finally {
            if ($null -eq $previousPidFile) { Remove-Item Env:APP_CHECK_HANG_PIDS -ErrorAction SilentlyContinue } else { $env:APP_CHECK_HANG_PIDS = $previousPidFile }
        }
    }
    if ($IsWindows) {
        $guiPass = New-FakeWindowsGuiExe "app-check-gui-pass" 0 @("CONTENT_EXPORT_FERROUS_ORE_LOADED", "CONTENT_EXPORT_PNG_DECODED", "CONTENT_EXPORT_INIT_OK", "CONTENT_EXPORT_JSON_READABLE", "CONTENT_EXPORT_PNG_READABLE", "CONTENT_EXPORT_JSONL_CREATED", "APP_CHECK_PASSED content-export")
        $guiSilent = New-FakeWindowsGuiExe "app-check-gui-silent" 0 @()
        $guiFailure = New-FakeWindowsGuiExe "app-check-gui-failure" 9 @("APP_CHECK_FAILED content-export")
        $ownedDirectories.Add([pscustomobject]@{ Path = $guiPass.Root; Prefix = "app-check-gui-pass-" })
        $ownedDirectories.Add([pscustomobject]@{ Path = $guiSilent.Root; Prefix = "app-check-gui-silent-" })
        $ownedDirectories.Add([pscustomobject]@{ Path = $guiFailure.Root; Prefix = "app-check-gui-failure-" })
        Assert-Exit "GUI executable stdout" 0 (Invoke-Check content-export $guiPass.FilePath -Export)
        Assert-Exit "GUI executable missing markers" 1 (Invoke-Check content-export $guiSilent.FilePath -Export)
        Assert-Exit "GUI executable nonzero" 1 (Invoke-Check content-export $guiFailure.FilePath -Export)
    }
}
finally {
    foreach ($path in $owned) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction Stop }
        if (Test-Path -LiteralPath $path) { throw "Native fixture file remains after cleanup: $path" }
    }
    foreach ($directory in $ownedDirectories) { Remove-ProcessFixtureDirectory -Path $directory.Path -ExpectedPrefix $directory.Prefix }
    Remove-ProcessFixtureDirectory -Path $root -ExpectedPrefix "train-app-check harness "
}

if ($failed -gt 0) { exit 1 }
exit 0
