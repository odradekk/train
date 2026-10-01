# Windowed save → quit → new-process load observation.
# Slice 2 (issue #53): process 1 writes the fixed world sequence; process 2
# captures restored / ground-hole / ledge / continue-damage frames.
# Slice 3 (issue #62): process 1 writes the parked-vehicle persist fixtures;
# process 2 loads the same UUID and captures hollow / refit / fracture / dropped
# frames after the restored session is actually displayed.
# Requires Godot 4.7 .NET on PATH or $env:GODOT. Does not use --headless.
# On Godot 4.7.2 .NET, --quit-after is a main-loop frame budget, not seconds.
param(
    [ValidateSet(2, 3, 4)]
    [int] $Slice = 2,
    [string] $OutputPath = "",
    [string] $CaptureOutputPath = ""
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
. (Join-Path $PSScriptRoot "Get-HostHardwareInfo.ps1")
if (-not $env:GODOT -or -not (Test-Path -LiteralPath $env:GODOT)) {
    throw "Set GODOT to the Godot 4.7 .NET executable before running resume observation."
}

if (-not $OutputPath) {
    $OutputPath = if ($Slice -eq 4) { ".godot/validation/issue74/resume-observe-report.md" } elseif ($Slice -eq 3) { ".godot/issue62-resume-observe-report.md" } else { ".godot/issue53-resume-observe-report.md" }
}
$recordName = if ($Slice -eq 4) { "issue74" } elseif ($Slice -eq 3) { "issue-62-resume" } else { "issue-53-resume" }
if (-not $CaptureOutputPath) {
    $CaptureOutputPath = if ($Slice -eq 4) { ".godot/validation/issue74" } else { ".godot/validation/$recordName" }
}

foreach ($name in @(
    "TRAIN_VOXEL_SMOKE",
    "TRAIN_VOXEL_SMOKE_REENTER",
    "TRAIN_VOXEL_SMOKE_RESUME",
    "TRAIN_VOXEL_SMOKE_SAVE_ID",
    "TRAIN_VOXEL_SMOKE_ISOLATION_ABORT",
    "TRAIN_VOXEL_BASELINE",
    "TRAIN_VOXEL_OBSERVE",
    "TRAIN_VOXEL_OBSERVE_RESUME",
    "TRAIN_VOXEL_OBSERVE_SAVE_ID",
    "TRAIN_CONTENT_SMOKE",
    "TRAIN_CONTENT_EXPORT_SMOKE",
    "TRAIN_MODS_EXPORT_SMOKE",
    "TRAIN_LOG_SMOKE"
)) {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}

$ironJson = Join-Path $root "builtin\core\resources\voxels\ferrous-ore.voxels.json"
$ledgeJson = Join-Path $root "builtin\core\resources\voxels\ledge.voxels.json"
$ironTop = Join-Path $root "builtin\core\resources\voxels\ferrous-ore\top.png"
$markerJson = Join-Path $root "builtin\core\resources\voxels\refit-marker.voxels.json"
$hollowJson = Join-Path $root "builtin\core\resources\composites\dev-hollow-shell.composites.json"
$beforeJson = (Get-FileHash -LiteralPath $ironJson -Algorithm SHA256).Hash
$beforeLedge = (Get-FileHash -LiteralPath $ledgeJson -Algorithm SHA256).Hash
$beforeTop = (Get-FileHash -LiteralPath $ironTop -Algorithm SHA256).Hash
$beforeMarker = (Get-FileHash -LiteralPath $markerJson -Algorithm SHA256).Hash
$beforeHollow = (Get-FileHash -LiteralPath $hollowJson -Algorithm SHA256).Hash

$captures = if ($Slice -eq 4) {
    @(
        "issue74-resume-running.png"
    )
} elseif ($Slice -eq 3) {
    @(
        "issue62-resume-hollow.png",
        "issue62-resume-refit.png",
        "issue62-resume-fracture.png",
        "issue62-resume-dropped.png"
    )
} else {
    @(
        "issue53-resume-restored.png",
        "issue53-resume-chunk-boundary.png",
        "issue53-resume-ground-hole.png",
        "issue53-resume-ledge.png",
        "issue53-resume-continue-damage.png"
    )
}
$captureDir = if ($Slice -eq 4) { Join-Path $root ".godot/validation/issue74" } else { Join-Path $root ".godot" }
$recordDir = Join-Path $root $CaptureOutputPath
New-Item -ItemType Directory -Force -Path $captureDir | Out-Null
New-Item -ItemType Directory -Force -Path $recordDir | Out-Null

$userPrefix = if ($Slice -eq 4) { "train-issue74-observe-" } elseif ($Slice -eq 3) { "train-issue62-observe-" } else { "train-issue53-observe-" }
$userData = Join-Path ([System.IO.Path]::GetTempPath()) ($userPrefix + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $userData | Out-Null
$env:TRAIN_MODS_SMOKE_USER_DATA = $userData
$env:TRAIN_VOXEL_SLICE = "$Slice"
$env:TRAIN_VOXEL_OBSERVE_RESUME = "1"

function Invoke-ResumeObserve {
    param(
        [int] $QuitAfter,
        [string] $Label
    )
    Write-Host "Starting Godot voxel resume observe process: $Label"
    $raw = & $env:GODOT --path $root --quit-after $QuitAfter 2>&1
    return @{
        Output = ($raw | Out-String)
        ExitCode = $LASTEXITCODE
    }
}

$combined = ""
try {
    $writeAfter = if ($Slice -eq 4) { 180 } elseif ($Slice -eq 3) { 120 } else { 90 }
    $loadAfter = if ($Slice -eq 4) { 180 } elseif ($Slice -eq 3) { 200 } else { 180 }
    $write = Invoke-ResumeObserve -QuitAfter $writeAfter -Label "write"
    Write-Host $write.Output
    if ($write.Output -notmatch "VOXEL_OBSERVE_RESUME_WRITTEN id=([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})") {
        throw "Resume observe failed. Process 1 did not print a save UUID."
    }
    $saveId = $Matches[1]
    $savePath = Join-Path $userData (Join-Path "saves" (Join-Path $saveId "save.json"))
    if (-not (Test-Path -LiteralPath $savePath)) {
        throw "Resume observe failed. Save file was not on disk: $savePath"
    }
    $env:TRAIN_VOXEL_OBSERVE_SAVE_ID = $saveId
    $resume = Invoke-ResumeObserve -QuitAfter $loadAfter -Label "load"
    Write-Host $resume.Output
    $combined = $write.Output + "`n" + $resume.Output
    if ($write.ExitCode -ne 0) {
        throw "Resume observe write process exited $($write.ExitCode)."
    }
    if ($resume.ExitCode -ne 0 -or $combined -notlike "*VOXEL_OBSERVE_RESUME_COMPLETE*") {
        throw "Resume observe failed: expected VOXEL_OBSERVE_RESUME_COMPLETE and load exit 0."
    }
}
finally {
    Remove-Item "Env:TRAIN_VOXEL_OBSERVE_RESUME" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_VOXEL_OBSERVE_SAVE_ID" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_VOXEL_SLICE" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_MODS_SMOKE_USER_DATA" -ErrorAction SilentlyContinue
    if ($userData -and (Test-Path -LiteralPath $userData)) {
        Remove-Item -LiteralPath $userData -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$afterJson = (Get-FileHash -LiteralPath $ironJson -Algorithm SHA256).Hash
$afterLedge = (Get-FileHash -LiteralPath $ledgeJson -Algorithm SHA256).Hash
$afterTop = (Get-FileHash -LiteralPath $ironTop -Algorithm SHA256).Hash
$afterMarker = (Get-FileHash -LiteralPath $markerJson -Algorithm SHA256).Hash
$afterHollow = (Get-FileHash -LiteralPath $hollowJson -Algorithm SHA256).Hash
if ($beforeJson -ne $afterJson -or $beforeLedge -ne $afterLedge -or $beforeTop -ne $afterTop -or $beforeMarker -ne $afterMarker -or $beforeHollow -ne $afterHollow) {
    throw "Resume observe rewrote worktree built-in content."
}

$missing = @($captures | Where-Object { -not (Test-Path (Join-Path $captureDir $_)) })
if ($missing.Count -gt 0) {
    throw "Resume observe failed. Missing captures: $($missing -join ', ')"
}

$hashes = @()
foreach ($name in $captures) {
    $source = (Resolve-Path -LiteralPath (Join-Path $captureDir $name)).Path
    $destination = Join-Path $recordDir $name
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    $sameFile = (Test-Path -LiteralPath $destination) -and ((Resolve-Path -LiteralPath $destination).Path -eq $source)
    if (-not $sameFile) {
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    $hashes += [pscustomobject]@{
        Name = $name
        Hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    }
}

$cpu = Get-HostCpuName
$godotVersion = (& $env:GODOT --version | Out-String).Trim()
$captureLines = @($combined -split "`r?`n" | Where-Object { $_ -like "*VOXEL_OBSERVE_CAPTURE*" -or $_ -like "*VOXEL_OBSERVE_RESUME_*" })
$written = ($combined -split "`r?`n" | Where-Object { $_ -like "*VOXEL_OBSERVE_RESUME_WRITTEN*" } | Select-Object -Last 1)
$loaded = ($combined -split "`r?`n" | Where-Object { $_ -like "*VOXEL_OBSERVE_RESUME_LOADED*" } | Select-Object -Last 1)
$issue = if ($Slice -eq 4) { "#74" } elseif ($Slice -eq 3) { "#62" } else { "#53" }
$facts = if ($Slice -eq 4) {
    "Automated facts only: process 1 set ``TRAIN_VOXEL_OBSERVE_RESUME=1`` and ``TRAIN_VOXEL_SLICE=4`` with isolated ``TRAIN_MODS_SMOKE_USER_DATA``, advanced the formal fixture to step 900, printed a UUID, and exited. Process 2 was a new Godot process with the same user-data and ``TRAIN_VOXEL_OBSERVE_SAVE_ID``. Required ``VOXEL_OBSERVE_RESUME_COMPLETE``, exit 0, ``issue74-resume-running.png`` after the restored session loaded, and unchanged worktree built-in hashes. The script does not inspect PNG pixels and is not a human visual confirmation."
} elseif ($Slice -eq 3) {
    "Automated facts only: process 1 set ``TRAIN_VOXEL_OBSERVE_RESUME=1`` and ``TRAIN_VOXEL_SLICE=3`` with isolated ``TRAIN_MODS_SMOKE_USER_DATA``, wrote the parked-vehicle persist fixtures, printed a UUID, and exited. Process 2 was a new Godot process with the same user-data and ``TRAIN_VOXEL_OBSERVE_SAVE_ID``. Required ``VOXEL_OBSERVE_RESUME_COMPLETE``, exit 0, four ``issue62-resume-*.png`` files generated after the restored session loaded, and unchanged worktree built-in hashes. The script does not inspect PNG pixels and does not reuse ``issue58-*.png``, ``issue60-*.png``, or ``issue61-*.png``."
} else {
    "Automated facts only: process 1 set ``TRAIN_VOXEL_OBSERVE_RESUME=1`` with isolated ``TRAIN_MODS_SMOKE_USER_DATA``, wrote the slice 2 fixed sequence, printed a UUID, and exited. Process 2 was a new Godot process with the same user-data and ``TRAIN_VOXEL_OBSERVE_SAVE_ID``. Required ``VOXEL_OBSERVE_RESUME_COMPLETE``, exit 0, five ``issue53-resume-*.png`` files, and unchanged worktree ferrous-ore/ledge hashes. The script does not inspect PNG pixels and does not reuse ``issue50-*.png`` or ``issue52-*.png``."
}

$report = @(
    "# Issue $issue resume observation (automated facts)",
    "",
    "Environment: Godot ``$godotVersion``, CPU $cpu. Command: `` `$env:GODOT = '$env:GODOT'; .\\scripts\\run-godot-voxel-observe-resume.ps1 -Slice $Slice ``.",
    "",
    $facts,
    "",
    "- $written",
    "- $loaded",
    "",
    "Copied captures:",
    ($hashes | ForEach-Object { "- ``$CaptureOutputPath/$($_.Name)`` SHA256 ``$($_.Hash)``" }),
    "",
    "Capture log:",
    "",
    ($captureLines | ForEach-Object { "- $_" })
)
$absoluteOutput = Join-Path $root $OutputPath
$parent = Split-Path -Parent $absoluteOutput
New-Item -ItemType Directory -Force -Path $parent | Out-Null
Set-Content -LiteralPath $absoluteOutput -Value $report -Encoding utf8
Write-Host "Wrote $absoluteOutput"
Write-Host "Godot voxel resume observe passed."
exit 0
