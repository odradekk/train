param(
    [string[]] $Configuration = @("straight-rail-train", "straight-rail-empty"),
    [string] $OutputDirectory
)

# Real-Godot automatic entrance for the long-lived world editor (#121).
# For each named fixed configuration it runs two isolated processes that share
# one fresh user-data directory:
#   1. enters through the start-menu world editor button, checks default pause,
#      world/track/vehicle display, Godot-physics collision on a rail cell,
#      paused frames not advancing, runs the registered validation sequences on
#      independent sessions, drives the scene's own step / N-step / run / pause
#      controls, submits paused world edits through the edit panel and the real
#      console at one boundary, queues a structural refit and a removal (plus,
#      with a train, a panel stop and a console rail repair) a few steps
#      ahead, writes an ordinary save, and prints a deterministic continuation
#      that runs the queued edits; with a train it then queues rail removal,
#      repair and start through the panel and a console stop, and checks
#      Godot physics on an immediately removed and repaired rail cell, while
#      without a train the panel's construct is rejected and its assemble
#      forms the fixture train;
#   2. loads that save in a new process and checks the source, pause, display,
#      collision, the restored edit queue, one single step, and the same
#      continuation, which must match the first process line for line.
# Every run writes to a new timestamped directory; historical reports and user
# saves are never reused.
#
# Command (repository root, $env:GODOT pointing at the Godot .NET console exe):
#   pwsh -NoProfile -File scripts/run-godot-world-editor-smoke.ps1
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
    $OutputDirectory = Join-Path $root ".godot/validation/issue-122/$stamp"
}
if (Test-Path -LiteralPath $OutputDirectory) {
    [Console]::Error.WriteLine("Output directory already exists: $OutputDirectory")
    exit 1
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Write-Host "Using Godot: $env:GODOT"
Write-Host "Output: $OutputDirectory"

foreach ($name in @(
    "TRAIN_VOXEL_SMOKE", "TRAIN_VOXEL_SMOKE_REENTER", "TRAIN_VOXEL_SMOKE_RESUME", "TRAIN_VOXEL_SMOKE_SAVE_ID",
    "TRAIN_VOXEL_OBSERVE", "TRAIN_VOXEL_OBSERVE_RESUME", "TRAIN_VOXEL_BASELINE", "TRAIN_CONTENT_SMOKE",
    "TRAIN_CONTENT_EXPORT_SMOKE", "TRAIN_MODS_EXPORT_SMOKE", "TRAIN_LOG_SMOKE", "TRAIN_MODS_SMOKE_USER_DATA",
    "TRAIN_WORLD_EDITOR_SMOKE", "TRAIN_WORLD_EDITOR_SMOKE_MODE", "TRAIN_WORLD_EDITOR_CONFIG", "TRAIN_WORLD_EDITOR_SAVE_ID",
    "TRAIN_WORLD_EDITOR_CAPTURE", "TRAIN_WORLD_EDITOR_CAPTURE_STEPS",
    "TRAIN_AUTHORITY_CHECKPOINT", "TRAIN_AUTHORITY_CHECKPOINT_DIR", "TRAIN_AUTHORITY_BUILD"
)) {
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
    foreach ($bad in @("WORLD_EDITOR_SMOKE_FAILED", "WORLD_EDITOR_ESTABLISH_FAILED", "Unhandled exception", "System.InvalidOperationException", "ERROR:")) {
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
foreach ($config in $Configuration) {
    $userData = Join-Path $OutputDirectory "user-data-$config"
    New-Item -ItemType Directory -Path $userData | Out-Null
    $env:TRAIN_MODS_SMOKE_USER_DATA = $userData
    $env:TRAIN_WORLD_EDITOR_SMOKE = "1"
    $env:TRAIN_WORLD_EDITOR_CONFIG = $config
    Remove-Item Env:TRAIN_WORLD_EDITOR_SAVE_ID -ErrorAction SilentlyContinue

    $establish = Invoke-Godot (Join-Path $OutputDirectory "$config-establish.log")
    $markers = @(
        "WORLD_EDITOR_SMOKE_MENU_ENTER config=$config",
        "WORLD_EDITOR_SMOKE_ESTABLISHED config=$config version=1 kind=fixed_configuration",
        "paused=true step=0",
        "WORLD_EDITOR_SMOKE_DISPLAY",
        "WORLD_EDITOR_SMOKE_COLLISION godot_rail_hit=true godot_empty_hit=false",
        "WORLD_EDITOR_SMOKE_PAUSE_HELD frames=10 step=0 console_opened=true",
        "WORLD_EDITOR_SMOKE_SEQUENCES",
        "WORLD_EDITOR_SMOKE_MANUAL",
        "WORLD_EDITOR_SMOKE_AFTER_STEPS_COLLISION godot_rail_hit=true",
        "WORLD_EDITOR_SMOKE_EDITS step=",
        "WORLD_EDITOR_SMOKE_EDITS_PENDING ",
        "WORLD_EDITOR_SMOKE_EDITED_COLLISION godot_probe_hit=true godot_console_cell_hit=true",
        "WORLD_EDITOR_SMOKE_SAVED id=",
        "WORLD_EDITOR_SMOKE_SAVED_QUEUE ",
        "WORLD_EDITOR_SMOKE_CONTINUATION ",
        "WORLD_EDITOR_SMOKE_RUN advanced=",
        "WORLD_EDITOR_SMOKE_PAUSE_AFTER_RUN",
        "WORLD_EDITOR_SMOKE_DONE"
    )
    if ($config -eq "straight-rail-train") {
        $markers += "WORLD_EDITOR_SMOKE_SEQUENCE_PASSED straight-rail-run-and-halt"
        $markers += "WORLD_EDITOR_SMOKE_SEQUENCE_PASSED straight-rail-rejections"
        $markers += "WORLD_EDITOR_SMOKE_SEQUENCE_PASSED straight-rail-ordered-edits"
        $markers += "WORLD_EDITOR_SMOKE_SEQUENCE_PASSED straight-rail-repair-restart"
        $markers += "WORLD_EDITOR_SMOKE_MANUAL_MATCHES_SEQUENCE straight-rail-run-and-halt"
        $markers += "WORLD_EDITOR_SMOKE_RAIL_QUEUE step="
        $markers += "WORLD_EDITOR_SMOKE_RAIL_COLLISION godot_gap_hit=false godot_repaired_hit=true"
    }
    else {
        $markers += "WORLD_EDITOR_SMOKE_ASSEMBLED_COLLISION godot_rail_hit=true godot_empty_hit=false"
        $markers += "WORLD_EDITOR_SMOKE_PANEL_ASSEMBLY executed="
    }
    $ok = Require-Markers $establish $markers "$config establish"
    if (-not $ok) { $failed = $true; continue }

    $match = [regex]::Match($establish.Text, "WORLD_EDITOR_SMOKE_SAVED id=([0-9a-fA-F-]{36}) step=(\d+)")
    $saveId = $match.Groups[1].Value
    $savedStep = [int] $match.Groups[2].Value
    $savePath = Get-ChildItem -LiteralPath (Join-Path $userData "saves") -Recurse -Filter "save.json" | Select-Object -First 1
    if (-not $savePath -or -not (Get-Content -LiteralPath $savePath.FullName -Raw).Contains('"world_source"')) {
        [Console]::Error.WriteLine("$config save does not contain world_source.")
        $failed = $true
        continue
    }

    $env:TRAIN_WORLD_EDITOR_SAVE_ID = $saveId
    $resume = Invoke-Godot (Join-Path $OutputDirectory "$config-resume.log")
    $ok = Require-Markers $resume @(
        "WORLD_EDITOR_SMOKE_RESTORED config=$config version=1 kind=fixed_configuration",
        "paused=true step=$savedStep",
        "WORLD_EDITOR_SMOKE_RESTORED_DISPLAY",
        "WORLD_EDITOR_SMOKE_RESTORED_COLLISION godot_rail_hit=true godot_empty_hit=false",
        "WORLD_EDITOR_SMOKE_RESTORED_QUEUE ",
        "WORLD_EDITOR_SMOKE_RESTORED_EDITED_COLLISION godot_probe_hit=true godot_console_cell_hit=true",
        "WORLD_EDITOR_SMOKE_RESTORED_STEP step=$($savedStep + 1)",
        "WORLD_EDITOR_SMOKE_CONTINUATION ",
        "WORLD_EDITOR_SMOKE_DONE"
    ) "$config resume"
    if (-not $ok) { $failed = $true; continue }

    $savedQueue = [regex]::Match($establish.Text, "WORLD_EDITOR_SMOKE_SAVED_QUEUE (.+)").Groups[1].Value.Trim()
    $restoredQueue = [regex]::Match($resume.Text, "WORLD_EDITOR_SMOKE_RESTORED_QUEUE (.+)").Groups[1].Value.Trim()
    $savedContinuation = [regex]::Match($establish.Text, "WORLD_EDITOR_SMOKE_CONTINUATION (.+)").Groups[1].Value.Trim()
    $restoredContinuation = [regex]::Match($resume.Text, "WORLD_EDITOR_SMOKE_CONTINUATION (.+)").Groups[1].Value.Trim()
    if ($savedQueue -ne $restoredQueue) {
        [Console]::Error.WriteLine("$config restored queue differs: saved '$savedQueue' restored '$restoredQueue'.")
        $failed = $true
        continue
    }
    if ($savedContinuation -ne $restoredContinuation) {
        [Console]::Error.WriteLine("$config continuation differs across processes: '$savedContinuation' vs '$restoredContinuation'.")
        $failed = $true
        continue
    }
    Write-Host "WORLD_EDITOR_SMOKE_CROSS_PROCESS_MATCH $config queue='$savedQueue' continuation='$savedContinuation'"
    Write-Host "WORLD_EDITOR_SMOKE_CONFIG_PASSED $config save=$saveId step=$savedStep"
}

foreach ($name in @("TRAIN_WORLD_EDITOR_SMOKE", "TRAIN_WORLD_EDITOR_CONFIG", "TRAIN_WORLD_EDITOR_SAVE_ID", "TRAIN_MODS_SMOKE_USER_DATA")) {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}

if ($failed) {
    Write-Host "WORLD_EDITOR_SMOKE_FAILED (logs in $OutputDirectory)"
    exit 1
}
Write-Host "WORLD_EDITOR_SMOKE_PASSED (logs in $OutputDirectory)"
exit 0
