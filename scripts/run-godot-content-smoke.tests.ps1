# Exercises scripts/run-godot-content-smoke.ps1 with controlled fake Godot programs.

$ErrorActionPreference = "Stop"
$smoke = Join-Path $PSScriptRoot "run-godot-content-smoke.ps1"
$failed = 0

function Invoke-Smoke {
    param(
        [string] $GodotPath,
        [switch] $ForceUnavailable
    )
    $previous = $env:GODOT
    $previousNoSearch = $env:TRAIN_CONTENT_SMOKE_NO_SEARCH
    try {
        if ($ForceUnavailable) {
            Remove-Item Env:GODOT -ErrorAction SilentlyContinue
            $env:TRAIN_CONTENT_SMOKE_NO_SEARCH = "1"
        }
        else {
            $env:GODOT = $GodotPath
            Remove-Item Env:TRAIN_CONTENT_SMOKE_NO_SEARCH -ErrorAction SilentlyContinue
        }
        $process = Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoProfile", "-File", $smoke) -Wait -PassThru -NoNewWindow
        return [int] $process.ExitCode
    }
    finally {
        if ($null -eq $previous) {
            Remove-Item Env:GODOT -ErrorAction SilentlyContinue
        }
        else {
            $env:GODOT = $previous
        }
        if ($null -eq $previousNoSearch) {
            Remove-Item Env:TRAIN_CONTENT_SMOKE_NO_SEARCH -ErrorAction SilentlyContinue
        }
        else {
            $env:TRAIN_CONTENT_SMOKE_NO_SEARCH = $previousNoSearch
        }
    }
}

function New-FakeGodot {
    param([int] $ExitCode, [string[]] $Lines)
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("train-fake-godot-content-" + [guid]::NewGuid().ToString("N") + ".cmd")
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
    "CONTENT_IRON_ORE_LOADED",
    "CONTENT_PNG_DECODED",
    "CONTENT_INIT_OK",
    "CONTENT_RELOAD_ACTION_PRESENT",
    "CONTENT_VFS_JSON_READABLE",
    "CONTENT_VFS_PNG_READABLE",
    "CONTENT_SMOKE_INPUT_RELOADED",
    "CONTENT_SMOKE_REMOVED"
)

$legacyMarkers = @(
    "CONTENT_IRON_ORE_LOADED",
    "CONTENT_PNG_DECODED",
    "CONTENT_INIT_OK",
    "CONTENT_RELOAD_ACTION_PRESENT",
    "CONTENT_VFS_JSON_READABLE",
    "CONTENT_VFS_PNG_READABLE",
    "CONTENT_SMOKE_ADDED",
    "CONTENT_SMOKE_REMOVED"
)
$failingFake = New-FakeGodot -ExitCode 7 -Lines $allMarkers
$passingFake = New-FakeGodot -ExitCode 0 -Lines $allMarkers
$missingFake = New-FakeGodot -ExitCode 0 -Lines @("CONTENT_IRON_ORE_LOADED")
$legacyFake = New-FakeGodot -ExitCode 0 -Lines $legacyMarkers
try {
    Assert-SmokeExit -Name "nonzero Godot with all markers" -Expected 1 -Actual (Invoke-Smoke -GodotPath $failingFake)
    Assert-SmokeExit -Name "unavailable Godot" -Expected 2 -Actual (Invoke-Smoke -ForceUnavailable)
    Assert-SmokeExit -Name "zero-exit Godot with all markers" -Expected 0 -Actual (Invoke-Smoke -GodotPath $passingFake)
    Assert-SmokeExit -Name "zero-exit Godot missing a marker" -Expected 1 -Actual (Invoke-Smoke -GodotPath $missingFake)
    Assert-SmokeExit -Name "zero-exit Godot with only direct-reload add marker" -Expected 1 -Actual (Invoke-Smoke -GodotPath $legacyFake)
}
finally {
    Remove-Item -LiteralPath $failingFake, $passingFake, $missingFake, $legacyFake -ErrorAction SilentlyContinue
}

if ($failed -gt 0) {
    exit 1
}
exit 0
