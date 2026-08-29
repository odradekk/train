# Exercises scripts/run-godot-mods-export-smoke.ps1 with controlled fake programs.

$ErrorActionPreference = "Stop"
$smoke = Join-Path $PSScriptRoot "run-godot-mods-export-smoke.ps1"
$failed = 0

function Invoke-Smoke {
    param(
        [string] $GodotPath,
        [string] $ExportBin,
        [switch] $ForceUnavailable
    )
    $previousGodot = $env:GODOT
    $previousBin = $env:TRAIN_MODS_EXPORT_BIN
    $previousNoSearch = $env:TRAIN_MODS_EXPORT_SMOKE_NO_SEARCH
    try {
        if ($ForceUnavailable) {
            Remove-Item Env:GODOT -ErrorAction SilentlyContinue
            Remove-Item Env:TRAIN_MODS_EXPORT_BIN -ErrorAction SilentlyContinue
            $env:TRAIN_MODS_EXPORT_SMOKE_NO_SEARCH = "1"
        }
        else {
            if ($GodotPath) { $env:GODOT = $GodotPath } else { Remove-Item Env:GODOT -ErrorAction SilentlyContinue }
            if ($ExportBin) { $env:TRAIN_MODS_EXPORT_BIN = $ExportBin } else { Remove-Item Env:TRAIN_MODS_EXPORT_BIN -ErrorAction SilentlyContinue }
            Remove-Item Env:TRAIN_MODS_EXPORT_SMOKE_NO_SEARCH -ErrorAction SilentlyContinue
        }
        $process = Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoProfile", "-File", $smoke) -Wait -PassThru -NoNewWindow
        return [int] $process.ExitCode
    }
    finally {
        if ($null -eq $previousGodot) { Remove-Item Env:GODOT -ErrorAction SilentlyContinue } else { $env:GODOT = $previousGodot }
        if ($null -eq $previousBin) { Remove-Item Env:TRAIN_MODS_EXPORT_BIN -ErrorAction SilentlyContinue } else { $env:TRAIN_MODS_EXPORT_BIN = $previousBin }
        if ($null -eq $previousNoSearch) { Remove-Item Env:TRAIN_MODS_EXPORT_SMOKE_NO_SEARCH -ErrorAction SilentlyContinue } else { $env:TRAIN_MODS_EXPORT_SMOKE_NO_SEARCH = $previousNoSearch }
    }
}

function New-FakeProgram {
    param([int] $ExitCode, [string[]] $Lines)
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("train-fake-mods-export-" + [guid]::NewGuid().ToString("N") + ".cmd")
    $body = @(
        "@echo off"
    ) + ($Lines | ForEach-Object { "echo $_" }) + @(
        "exit /b $ExitCode"
    )
    Set-Content -LiteralPath $path -Value $body -Encoding Ascii
    return $path
}

function Assert-SmokeExit {
    param([string] $Name, [int] $Expected, [int] $Actual)
    if ($Actual -ne $Expected) {
        Write-Host "FAIL $Name : expected exit $Expected, got $Actual"
        $script:failed++
        return
    }
    Write-Host "ok $Name"
}

$allMarkers = @(
    "MODS_EXPORT_IRON_ORE_LOADED",
    "MODS_EXPORT_INIT_OK",
    "MODS_EXPORT_ROOT_EMPTY"
)

$failingExport = New-FakeProgram -ExitCode 7 -Lines @("export failed")
$passingBin = New-FakeProgram -ExitCode 0 -Lines $allMarkers
$unavailableRoot = New-FakeProgram -ExitCode 0 -Lines @("MODS_EXPORT_IRON_ORE_LOADED", "MODS_EXPORT_INIT_OK", "MODS_EXPORT_ROOT_UNAVAILABLE")
$missingBin = New-FakeProgram -ExitCode 0 -Lines @("MODS_EXPORT_IRON_ORE_LOADED")
try {
    Assert-SmokeExit -Name "unavailable Godot" -Expected 2 -Actual (Invoke-Smoke -ForceUnavailable)
    Assert-SmokeExit -Name "failed export" -Expected 1 -Actual (Invoke-Smoke -GodotPath $failingExport)
    Assert-SmokeExit -Name "exported binary with empty mods root" -Expected 0 -Actual (Invoke-Smoke -ExportBin $passingBin)
    Assert-SmokeExit -Name "exported binary with unavailable mods root" -Expected 0 -Actual (Invoke-Smoke -ExportBin $unavailableRoot)
    Assert-SmokeExit -Name "exported binary missing a marker" -Expected 1 -Actual (Invoke-Smoke -ExportBin $missingBin)
}
finally {
    Remove-Item -LiteralPath $failingExport, $passingBin, $unavailableRoot, $missingBin -ErrorAction SilentlyContinue
}

if ($failed -gt 0) {
    exit 1
}
exit 0
