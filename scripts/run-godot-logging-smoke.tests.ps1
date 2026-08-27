# Exercises scripts/run-godot-logging-smoke.ps1 with controlled fake Godot programs.
# Does not reimplement marker/exit rules in assertions.

$ErrorActionPreference = "Stop"
$smoke = Join-Path $PSScriptRoot "run-godot-logging-smoke.ps1"
$failed = 0

function Invoke-Smoke {
    param(
        [string] $GodotPath,
        [switch] $ForceUnavailable
    )
    $previous = $env:GODOT
    $previousNoSearch = $env:TRAIN_LOG_SMOKE_NO_SEARCH
    try {
        if ($ForceUnavailable) {
            Remove-Item Env:GODOT -ErrorAction SilentlyContinue
            $env:TRAIN_LOG_SMOKE_NO_SEARCH = "1"
        }
        else {
            $env:GODOT = $GodotPath
            Remove-Item Env:TRAIN_LOG_SMOKE_NO_SEARCH -ErrorAction SilentlyContinue
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
            Remove-Item Env:TRAIN_LOG_SMOKE_NO_SEARCH -ErrorAction SilentlyContinue
        }
        else {
            $env:TRAIN_LOG_SMOKE_NO_SEARCH = $previousNoSearch
        }
    }
}

function New-FakeGodot {
    param([int] $ExitCode, [string[]] $Lines)
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("train-fake-godot-" + [guid]::NewGuid().ToString("N") + ".cmd")
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
    "application/application_ready",
    "smoke_worker_event",
    "SMOKE_PROCESS_DRAINED",
    "SMOKE_LOGGER_DISPOSED"
)

$failingFake = New-FakeGodot -ExitCode 7 -Lines $allMarkers
$passingFake = New-FakeGodot -ExitCode 0 -Lines $allMarkers
$missingFake = New-FakeGodot -ExitCode 0 -Lines @("application/application_ready")
try {
    Assert-SmokeExit -Name "nonzero Godot with all markers" -Expected 1 -Actual (Invoke-Smoke -GodotPath $failingFake)
    Assert-SmokeExit -Name "unavailable Godot" -Expected 2 -Actual (Invoke-Smoke -ForceUnavailable)
    Assert-SmokeExit -Name "zero-exit Godot with all markers" -Expected 0 -Actual (Invoke-Smoke -GodotPath $passingFake)
    Assert-SmokeExit -Name "zero-exit Godot missing a marker" -Expected 1 -Actual (Invoke-Smoke -GodotPath $missingFake)
}
finally {
    Remove-Item -LiteralPath $failingFake, $passingFake, $missingFake -ErrorAction SilentlyContinue
}

if ($failed -gt 0) {
    exit 1
}
exit 0
