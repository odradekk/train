# Repeatable Godot content-load smoke for issue #19.
#
# Command (from the repository root, with Godot 4.7 .NET on PATH or $env:GODOT):
#   $env:TRAIN_CONTENT_SMOKE = "1"
#   & $godot --headless --path . --quit-after 20
#
# Requires stdout to contain:
#   CONTENT_IRON_ORE_LOADED
#   CONTENT_PNG_DECODED
#   CONTENT_INIT_OK
#   CONTENT_RELOAD_ACTION_PRESENT
#   CONTENT_VFS_JSON_READABLE
#   CONTENT_VFS_PNG_READABLE
#   CONTENT_SMOKE_INPUT_RELOADED
#   CONTENT_SMOKE_REMOVED
#
# Exit 2 if Godot is unavailable; do not treat that as a passing smoke.
# Exit 1 if Godot is nonzero or a required marker is missing.

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Find-Godot {
    if ($env:GODOT -and (Test-Path -LiteralPath $env:GODOT)) {
        return $env:GODOT
    }

    if ($env:TRAIN_CONTENT_SMOKE_NO_SEARCH -eq "1") {
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
$command = '$env:TRAIN_CONTENT_SMOKE="1"; & <godot> --headless --path . --quit-after 20'
if (-not $godot) {
    [Console]::Error.WriteLine("Godot executable not found (PATH, GODOT, GODOT_HOME, or standard Program Files).")
    [Console]::Error.WriteLine("Unrun command: $command")
    exit 2
}

Write-Host "Using Godot: $godot"
$env:TRAIN_CONTENT_SMOKE = "1"
$raw = & $godot --headless --path $root --quit-after 20 2>&1
$godotExitCode = $LASTEXITCODE
$output = $raw | Out-String
Write-Host $output

$required = @(
    "CONTENT_IRON_ORE_LOADED",
    "CONTENT_PNG_DECODED",
    "CONTENT_INIT_OK",
    "CONTENT_RELOAD_ACTION_PRESENT",
    "CONTENT_VFS_JSON_READABLE",
    "CONTENT_VFS_PNG_READABLE",
    "CONTENT_SMOKE_INPUT_RELOADED",
    "CONTENT_SMOKE_REMOVED"
)
$missing = @($required | Where-Object { $output -notlike "*$_*" })
if ($godotExitCode -ne 0) {
    $detail = "Godot exited with code $godotExitCode."
    if ($missing.Count -gt 0) {
        $detail += " Missing: $($missing -join ', ')."
    }
    [Console]::Error.WriteLine("Godot content smoke failed. $detail")
    exit 1
}

if ($missing.Count -gt 0) {
    [Console]::Error.WriteLine("Godot content smoke failed. Missing: $($missing -join ', ')")
    exit 1
}

Write-Host "Godot content smoke passed."
exit 0
