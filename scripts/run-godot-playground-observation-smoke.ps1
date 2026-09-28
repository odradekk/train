param(
    [string[]] $Configuration = @("straight-rail-train", "straight-rail-empty"),
    [string] $OutputDirectory
)

# Real-Godot observation entrance for the Playground (#124).
# For each named fixed configuration it runs two isolated processes that share
# one fresh user-data directory:
#   1. enters through the start-menu Playground button in observation mode and
#      drives the real camera, panel, keys, clicks and console: free camera with
#      no selection, W flight while paused, list and click selection that never
#      move the camera, Godot-physics picks checked against domain collision,
#      eased focus / follow, follow against the drawn body node while running
#      and paused, right-drags whose release is lost to the console, the panel or
#      a focus loss that must not keep turning the camera, a smooth local target
#      switch, a coupler split while moving
#      and an active-locomotive change that keep the target, and a destroyed
#      follow target that leaves the camera free in place; then saves;
#   2. loads that save in a new process and checks observation is back to its
#      default and the authority fingerprint matches the saving process.
# The empty configuration covers free observation, rail picks and focus without
# any vehicle, and a focused voxel removed and re-added with the same definition
# through the console (immediately and as two edits due at one step) that must
# still count as lost. Godot runs with --fixed-fps 45 so display time is reproducible and
# each frame covers 4/3 simulation steps: following while running must then read
# interpolated display poses that differ from the authoritative step positions.
# Every run writes to a new timestamped directory.
#
# Command (repository root, $env:GODOT pointing at the Godot .NET console exe):
#   pwsh -NoProfile -File scripts/run-godot-playground-observation-smoke.ps1
# Exit 2 if Godot is unavailable; exit 1 for a failed process or missing marker.

$ErrorActionPreference = "Continue"
$FramesPerSecond = 45
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $env:GODOT -or -not (Test-Path -LiteralPath $env:GODOT)) {
    [Console]::Error.WriteLine("GODOT must point at the Godot .NET console executable.")
    exit 2
}

if (-not $OutputDirectory) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $OutputDirectory = Join-Path $root ".godot/validation/issue-124/$stamp"
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
    $output = & $env:GODOT --headless --path . --fixed-fps $FramesPerSecond --quit-after 6000 2>&1 | ForEach-Object { "$_" }
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
    foreach ($bad in @("OBSERVE_SMOKE_FAILED", "PLAYGROUND_SMOKE_FAILED", "PLAYGROUND_ESTABLISH_FAILED", "Unhandled exception", "System.InvalidOperationException", "ERROR:")) {
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
    foreach ($config in $Configuration) {
        $userData = Join-Path $OutputDirectory "user-data-$config"
        New-Item -ItemType Directory -Path $userData | Out-Null
        $env:TRAIN_MODS_SMOKE_USER_DATA = $userData
        $env:TRAIN_PLAYGROUND_SMOKE = "1"
        $env:TRAIN_PLAYGROUND_SMOKE_MODE = "observation"
        $env:TRAIN_PLAYGROUND_CONFIG = $config
        Remove-Item Env:TRAIN_PLAYGROUND_SAVE_ID -ErrorAction SilentlyContinue

        $establish = Invoke-Godot (Join-Path $OutputDirectory "$config-establish.log")
        $markers = @(
            "PLAYGROUND_SMOKE_MENU_ENTER config=$config",
            "OBSERVE_FREE ",
            "mode free selection none",
            "OBSERVE_FREE_FLY moved=",
            "OBSERVE_CLICK_RAIL cell=",
            "godot_hit=true domain=Hit",
            "OBSERVE_TRANSITION focus mode=Focus",
            "OBSERVE_SAVED id=",
            "OBSERVE_SMOKE_DONE"
        )
        if ($config -eq "straight-rail-train") {
            $markers += @(
                "OBSERVE_LIST_SELECT vehicle",
                "OBSERVE_CLICK_VEHICLE picked=vehicle",
                "OBSERVE_TRANSITION follow mode=Follow",
                "OBSERVE_FOLLOW_RUNNING vehicle=",
                "OBSERVE_FOLLOW_PAUSED frames=10",
                "camera_constant=true",
                "OBSERVE_ORBIT_ZOOM vehicle=",
                "OBSERVE_TRUNCATED_DRAGS_IGNORED console_release=still panel_release=still focus_out=still",
                "OBSERVE_TRANSITION switch mode=Follow",
                "OBSERVE_TRANSITION switch-back mode=Follow",
                "OBSERVE_SPLIT_KEEP assemblies=2",
                "OBSERVE_SPLIT_CONTINUITY",
                "OBSERVE_FOLLOW_AFTER_SPLIT vehicle=",
                "OBSERVE_ACTIVE_LOCOMOTIVE_CHANGE_KEEP active_locomotive=none mode follow",
                "OBSERVE_TRANSITION follow-carriage mode=Follow",
                "OBSERVE_DESTROY vehicle=",
                "OBSERVE_TARGET_LOST target-lost vehicle",
                "mode follow camera free-in-place camera_moved=false mode free selection none",
                "OBSERVE_CLICK_FRAGMENT cell="
            )
        } else {
            $markers += @(
                "OBSERVE_SEGMENT_FOLLOW_REFUSED mode=Free",
                "OBSERVE_REBUILD_LOST immediate target-lost cell",
                "OBSERVE_REBUILD_LOST scheduled target-lost cell"
            )
        }
        $ok = Require-Markers $establish $markers "$config establish"
        if (-not $ok) { $failed = $true; continue }
        if ($config -eq "straight-rail-train" -and -not [regex]::IsMatch($establish.Text, "OBSERVE_FOLLOW_RUNNING .* interpolated_frames=[1-9]")) {
            [Console]::Error.WriteLine("$config follow never read an interpolated display pose.")
            $failed = $true
            continue
        }

        $saved = [regex]::Match($establish.Text, "OBSERVE_SAVED id=([0-9a-fA-F-]{36}) step=(\d+) .* fingerprint=(\S+)")
        $saveId = $saved.Groups[1].Value
        $savedStep = $saved.Groups[2].Value
        $savedFingerprint = $saved.Groups[3].Value

        $env:TRAIN_PLAYGROUND_SAVE_ID = $saveId
        $resume = Invoke-Godot (Join-Path $OutputDirectory "$config-resume.log")
        $ok = Require-Markers $resume @(
            "OBSERVE_RESTORED step=$savedStep paused=true mode free selection none",
            "OBSERVE_SMOKE_DONE"
        ) "$config resume"
        if (-not $ok) { $failed = $true; continue }

        $restoredFingerprint = [regex]::Match($resume.Text, "OBSERVE_RESTORED .* fingerprint=(\S+)").Groups[1].Value
        if ($savedFingerprint -ne $restoredFingerprint) {
            [Console]::Error.WriteLine("$config fingerprint differs across processes: '$savedFingerprint' vs '$restoredFingerprint'.")
            $failed = $true
            continue
        }
        Write-Host "OBSERVE_SMOKE_CROSS_PROCESS_MATCH $config step=$savedStep fingerprint=$savedFingerprint"
        Write-Host "OBSERVE_SMOKE_CONFIG_PASSED $config save=$saveId"
    }
} finally {
    foreach ($name in $owned) {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }
}

if ($failed) {
    Write-Host "OBSERVE_SMOKE_FAILED (logs in $OutputDirectory)"
    exit 1
}
Write-Host "OBSERVE_SMOKE_PASSED (logs in $OutputDirectory)"
exit 0
