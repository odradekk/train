param(
    [ValidateSet(1)]
    [int] $Slice = 1
)

# Repeatable real-Godot voxel smoke for issue #39 slice 1.
#
# Command (from the repository root, with Godot 4.7 .NET on PATH or $env:GODOT):
#   .\scripts\run-godot-voxel-smoke.ps1 -Slice 1
#
# It loads the formal voxel development scene, removes and re-adds (4,4,4),
# applies formal single-target and multi-target damage, requires the scene's
# query-driven display and derived mesh to refresh, then closes the console,
# returns to the start menu with Esc, and re-enters through the formal menu
# button. Direct remove is not accepted as a substitute for the damage path.
# Unexpected C# exceptions fail the smoke.
# Exit 2 if Godot is unavailable; exit 1 for a failed process or missing marker.

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Find-Godot {
    if ($env:GODOT -and (Test-Path -LiteralPath $env:GODOT)) {
        return $env:GODOT
    }

    if ($env:TRAIN_VOXEL_SMOKE_NO_SEARCH -eq "1") {
        return $null
    }

    foreach ($name in @("godot", "godot-mono", "Godot_v4.7.2-stable_mono_win64_console", "Godot_v4.7.2-stable_mono_win64")) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }
    }

    $roots = @(
        $env:GODOT_HOME,
        "$env:LOCALAPPDATA\Programs\Godot",
        "$env:ProgramFiles\Godot",
        "${env:ProgramFiles(x86)}\Godot"
    ) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($directory in $roots) {
        $match = Get-ChildItem -LiteralPath $directory -Filter "Godot*_mono*_console.exe" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $match) {
            $match = Get-ChildItem -LiteralPath $directory -Filter "Godot*.exe" -File -ErrorAction SilentlyContinue |
                Select-Object -First 1
        }
        if ($match) {
            return $match.FullName
        }
    }

    return $null
}

$godot = Find-Godot
$command = '$env:TRAIN_VOXEL_SMOKE="1"; & <godot> --headless --path . --quit-after 45'
if (-not $godot) {
    [Console]::Error.WriteLine("Godot executable not found (PATH, GODOT, GODOT_HOME, or standard Program Files).")
    [Console]::Error.WriteLine("Unrun command: $command")
    exit 2
}

Write-Host "Using Godot: $godot"
$env:TRAIN_VOXEL_SMOKE = "1"
$raw = & $godot --headless --path $root --quit-after 45 2>&1
$godotExitCode = $LASTEXITCODE
$output = $raw | Out-String
Write-Host $output

$required = @(
    "VOXEL_SMOKE_GEOMETRY_VALID",
    "VOXEL_SMOKE_SCENE_READY",
    "VOXEL_SMOKE_REMOVED",
    "VOXEL_SMOKE_MESH_UPDATED",
    "VOXEL_SMOKE_ADDED",
    "VOXEL_SMOKE_DISPLAY_UPDATED",
    "VOXEL_SMOKE_ORIENTATION_UPDATED",
    "VOXEL_SMOKE_DAMAGED",
    "VOXEL_SMOKE_DAMAGE_DISPLAY_UPDATED",
    "VOXEL_SMOKE_DESTROYED",
    "VOXEL_SMOKE_DESTROY_DISPLAY_UPDATED",
    "VOXEL_SMOKE_RESTORED_AFTER_DAMAGE",
    "VOXEL_SMOKE_INTERNAL_FACE_UPDATED",
    "VOXEL_SMOKE_INTERNAL_DESTROYED",
    "VOXEL_SMOKE_INTERNAL_DESTROY_DISPLAY_UPDATED",
    "VOXEL_SMOKE_MULTI_DAMAGED",
    "VOXEL_SMOKE_MULTI_DISPLAY_UPDATED",
    "VOXEL_SMOKE_CONSOLE_CLOSED",
    "VOXEL_SMOKE_RETURNED_TO_MENU",
    "VOXEL_SMOKE_MENU_REENTERED"
)
$missing = @($required | Where-Object { $output -notlike "*$_*" })
if ($output -match 'NullReferenceException' -or $output -match 'ERROR: System\.') {
    [Console]::Error.WriteLine("Godot voxel smoke failed. Unexpected exception in engine output.")
    exit 1
}
if ($godotExitCode -ne 0) {
    $detail = "Godot exited with code $godotExitCode."
    if ($missing.Count -gt 0) {
        $detail += " Missing: $($missing -join ', ')."
    }
    [Console]::Error.WriteLine("Godot voxel smoke failed. $detail")
    exit 1
}

if ($missing.Count -gt 0) {
    [Console]::Error.WriteLine("Godot voxel smoke failed. Missing: $($missing -join ', ')")
    exit 1
}

Write-Host "Godot voxel smoke passed for slice $Slice."
exit 0
