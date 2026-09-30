# Repeatable Godot lifecycle smoke for issue #18.
#
# Command (from the repository root, with Godot 4.7 .NET on PATH or $env:GODOT):
#   $env:TRAIN_LOG_SMOKE = "1"
#   & $godot --headless --path . --quit-after 15
#
# The script resolves $godot, runs that command, and requires stdout to contain:
#   [application][application_ready] (Main._Ready -> GodotLogOutput)
#   smoke_worker_event              (background log reached Godot output)
#   SMOKE_PROCESS_DRAINED           (Main._Process drained the queued event)
#   SMOKE_LOGGER_DISPOSED           (Main._ExitTree disposed the logger)
#
# Exit 2 if Godot is unavailable; do not treat that as a passing smoke.
# Exit 1 if Godot is nonzero or a required marker is missing.
# This Godot build has no --user-data-dir flag. The unified entry sets APPDATA on
# this process so the engine user:// stays inside that run. Direct invocation uses
# the caller's own user directory.

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Find-Godot {
    if ($env:GODOT -and (Test-Path -LiteralPath $env:GODOT)) {
        return $env:GODOT
    }

    if ($env:TRAIN_LOG_SMOKE_NO_SEARCH -eq "1") {
        return $null
    }

    foreach ($name in @("godot-dotnet", "godot", "godot-mono", "Godot_v4.7.2-stable_mono_win64_console", "Godot_v4.7.2-stable_mono_win64")) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) {
            return $cmd.Source
        }
    }

    $roots = @(
        $env:GODOT_HOME,
        "$env:LOCALAPPDATA\Programs\Godot",
        "$env:ProgramFiles\Godot",
        "${env:ProgramFiles(x86)}\Godot"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($dir in $roots) {
        $hit = Get-ChildItem -LiteralPath $dir -Filter "Godot*_mono*_console.exe" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $hit) {
            $hit = Get-ChildItem -LiteralPath $dir -Filter "Godot*.exe" -File -ErrorAction SilentlyContinue |
                Select-Object -First 1
        }
        if ($hit) {
            return $hit.FullName
        }
    }

    return $null
}

$godot = Find-Godot
$command = '$env:TRAIN_LOG_SMOKE="1"; & <godot> --headless --path . --quit-after 15'
if (-not $godot) {
    [Console]::Error.WriteLine("Godot executable not found (PATH, GODOT, GODOT_HOME, or standard Program Files).")
    [Console]::Error.WriteLine("Unrun command: $command")
    exit 2
}

Write-Host "Using Godot: $godot"
$env:TRAIN_LOG_SMOKE = "1"
$raw = & $godot --headless --path $root --quit-after 15 2>&1
$godotExitCode = $LASTEXITCODE
$output = $raw | Out-String
Write-Host $output

$required = @(
    "[application][application_ready]",
    "smoke_worker_event",
    "SMOKE_PROCESS_DRAINED",
    "SMOKE_LOGGER_DISPOSED"
)
$missing = @($required | Where-Object { $output -notlike "*$_*" })
if ($godotExitCode -ne 0) {
    $detail = "Godot exited with code $godotExitCode."
    if ($missing.Count -gt 0) {
        $detail += " Missing: $($missing -join ', ')."
    }
    [Console]::Error.WriteLine("Godot logging smoke failed. $detail")
    exit 1
}

if ($missing.Count -gt 0) {
    [Console]::Error.WriteLine("Godot logging smoke failed. Missing: $($missing -join ', ')")
    exit 1
}

Write-Host "Godot logging smoke passed."
exit 0
