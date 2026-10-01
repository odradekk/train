param(
    [string] $Configuration = "straight-rail-train",
    [string] $OutputDirectory
)

# Real-Godot entrance for Playground save/switch protection (#125).
# One isolated user-data directory, two processes:
#   1. enters through the start-menu Playground button, exercises cancel,
#      discard, a failing save, a rejected candidate, and save-then-switch,
#      then saves the switched world;
#   2. loads that save and continues one step.
# Screenshots and a headless exit are not visual acceptance.
#
# Command (repository root, $env:GODOT pointing at the Godot .NET console exe):
#   pwsh -NoProfile -File scripts/run-godot-playground-switch-smoke.ps1
# Exit 2 if Godot is unavailable; exit 1 for a failed process or missing marker.

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $env:GODOT -or -not (Test-Path -LiteralPath $env:GODOT)) {
    [Console]::Error.WriteLine("GODOT must point at the Godot .NET console executable.")
    exit 2
}

if (-not $OutputDirectory) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $OutputDirectory = Join-Path $root ".godot/validation/issue-125/$stamp"
}
if (Test-Path -LiteralPath $OutputDirectory) {
    [Console]::Error.WriteLine("Output directory already exists: $OutputDirectory")
    exit 1
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Write-Host "Using Godot: $env:GODOT"
Write-Host "Output: $OutputDirectory"

$owned = @("TRAIN_PLAYGROUND_SMOKE", "TRAIN_PLAYGROUND_SMOKE_MODE", "TRAIN_PLAYGROUND_CONFIG", "TRAIN_PLAYGROUND_SAVE_ID", "TRAIN_MODS_SMOKE_USER_DATA")
foreach ($name in @(
    "TRAIN_VOXEL_SMOKE", "TRAIN_VOXEL_SMOKE_REENTER", "TRAIN_VOXEL_SMOKE_RESUME", "TRAIN_VOXEL_SMOKE_SAVE_ID",
    "TRAIN_VOXEL_OBSERVE", "TRAIN_VOXEL_OBSERVE_RESUME", "TRAIN_VOXEL_BASELINE", "TRAIN_CONTENT_SMOKE",
    "TRAIN_CONTENT_EXPORT_SMOKE", "TRAIN_MODS_EXPORT_SMOKE", "TRAIN_LOG_SMOKE",
    "TRAIN_PLAYGROUND_CAPTURE", "TRAIN_PLAYGROUND_CAPTURE_STEPS"
) + $owned) {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}

function Invoke-Godot([string] $log) {
    $output = & $env:GODOT --headless --path . --quit-after 3000 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $output | Set-Content -LiteralPath $log -Encoding utf8
    return @{ Code = $code; Text = ($output -join "`n") }
}

function Require-Markers([hashtable] $run, [string[]] $markers, [string] $label) {
    $ok = $true
    if ($run.Code -ne 0) {
        [Console]::Error.WriteLine("$label exited with $($run.Code).")
        $ok = $false
    }
    foreach ($bad in @("SWITCH_SMOKE_FAILED", "PLAYGROUND_SMOKE_FAILED", "PLAYGROUND_ESTABLISH_FAILED", "Unhandled exception", "System.InvalidOperationException", "ERROR:")) {
        if ($run.Text.Contains($bad)) {
            [Console]::Error.WriteLine("$label printed $bad.")
            $ok = $false
        }
    }
    foreach ($marker in $markers) {
        if (-not $run.Text.Contains($marker)) {
            [Console]::Error.WriteLine("$label missing marker $marker.")
            $ok = $false
        }
    }
    return $ok
}

$failed = $false
try {
    $userData = Join-Path $OutputDirectory "user-data"
    New-Item -ItemType Directory -Path $userData | Out-Null
    $env:TRAIN_MODS_SMOKE_USER_DATA = $userData
    $env:TRAIN_PLAYGROUND_SMOKE = "1"
    $env:TRAIN_PLAYGROUND_SMOKE_MODE = "switch"
    $env:TRAIN_PLAYGROUND_CONFIG = $Configuration
    Remove-Item Env:TRAIN_PLAYGROUND_SAVE_ID -ErrorAction SilentlyContinue

    $establish = Invoke-Godot (Join-Path $OutputDirectory "establish.log")
    $ok = Require-Markers $establish @(
        "PLAYGROUND_SMOKE_MENU_ENTER config=$Configuration",
        "SWITCH_CANCEL ",
        "SWITCH_DISCARD ",
        "SWITCH_SAVE_FAILED",
        "SWITCH_CANDIDATE_FAILED",
        "SWITCH_COMPLETED ",
        "SWITCH_SAVED id=",
        "SWITCH_SMOKE_DONE"
    ) "establish"
    if (-not $ok) { $failed = $true }
    else {
        $saved = [regex]::Match($establish.Text, "SWITCH_SAVED id=([0-9a-fA-F-]{36}) step=(\d+) source=(\S+) paused=true fingerprint=(\S+)")
        if (-not $saved.Success) {
            [Console]::Error.WriteLine("establish did not print a restorable save.")
            $failed = $true
        } else {
            $env:TRAIN_PLAYGROUND_SAVE_ID = $saved.Groups[1].Value
            $resume = Invoke-Godot (Join-Path $OutputDirectory "resume.log")
            $ok = Require-Markers $resume @(
                "SWITCH_RESTORED step=$($saved.Groups[2].Value) source=$($saved.Groups[3].Value) paused=true fingerprint=$($saved.Groups[4].Value)",
                "SWITCH_CONTINUED step=$([int]$saved.Groups[2].Value + 1)",
                "SWITCH_SMOKE_DONE"
            ) "resume"
            if (-not $ok) { $failed = $true }
            else { Write-Host "SWITCH_SMOKE_CROSS_PROCESS_MATCH source=$($saved.Groups[3].Value) step=$($saved.Groups[2].Value)" }
        }
    }
} finally {
    foreach ($name in $owned) {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
}

if ($failed) {
    Write-Host "SWITCH_SMOKE_FAILED (logs in $OutputDirectory)"
    exit 1
}
Write-Host "SWITCH_SMOKE_PASSED (logs in $OutputDirectory)"
exit 0
