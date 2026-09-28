param(
    [string] $OutputDirectory
)

# Cross-process authority checkpoint for the formal Playground sequence (#126).
# Two isolated processes share one fresh user-data directory:
#   1. enters through the start-menu Playground button, runs
#      authority-manufacture-fracture on the live session, saves, writes the
#      canonical authority document, then adds one voxel and writes the continuation;
#   2. loads that save and repeats the same add.
# The script hashes the written files itself and compares those digests to the
# digests printed by the game. Historical reports are never reused.
#
# Command (repository root, $env:GODOT pointing at the Godot .NET console exe):
#   pwsh -NoProfile -File scripts/run-godot-authority-checkpoint.ps1
# Exit 2 if Godot is unavailable; exit 1 for a failed process, mismatched digest, or missing marker.

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $env:GODOT -or -not (Test-Path -LiteralPath $env:GODOT)) {
    [Console]::Error.WriteLine("GODOT must point at the Godot .NET console executable.")
    exit 2
}

if (-not $OutputDirectory) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $OutputDirectory = Join-Path $root ".godot/validation/issue-126/$stamp"
}
if (Test-Path -LiteralPath $OutputDirectory) {
    [Console]::Error.WriteLine("Output directory already exists: $OutputDirectory")
    exit 1
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$documents = Join-Path $OutputDirectory "documents"
New-Item -ItemType Directory -Path $documents | Out-Null
Write-Host "Using Godot: $env:GODOT"
Write-Host "Output: $OutputDirectory"

foreach ($name in @(
    "TRAIN_VOXEL_SMOKE", "TRAIN_VOXEL_SMOKE_REENTER", "TRAIN_VOXEL_SMOKE_RESUME", "TRAIN_VOXEL_SMOKE_SAVE_ID",
    "TRAIN_VOXEL_OBSERVE", "TRAIN_VOXEL_OBSERVE_RESUME", "TRAIN_VOXEL_BASELINE", "TRAIN_CONTENT_SMOKE",
    "TRAIN_CONTENT_EXPORT_SMOKE", "TRAIN_MODS_EXPORT_SMOKE", "TRAIN_LOG_SMOKE", "TRAIN_MODS_SMOKE_USER_DATA",
    "TRAIN_PLAYGROUND_SMOKE", "TRAIN_PLAYGROUND_SMOKE_MODE", "TRAIN_PLAYGROUND_CONFIG", "TRAIN_PLAYGROUND_SAVE_ID",
    "TRAIN_PLAYGROUND_CAPTURE", "TRAIN_PLAYGROUND_CAPTURE_STEPS",
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
    foreach ($bad in @("AUTHORITY_CHECKPOINT_FAILED", "PLAYGROUND_ESTABLISH_FAILED", "Unhandled exception", "System.InvalidOperationException", "ERROR:")) {
        if ($run.Text.Contains($bad)) {
            [Console]::Error.WriteLine("$label printed $bad.")
            $ok = $false
        }
    }
    foreach ($marker in $markers) {
        if (-not $run.Text.Contains($marker)) {
            [Console]::Error.WriteLine("$label is missing marker: $marker")
            $ok = $false
        }
    }
    return $ok
}

function Get-Sha256([string] $path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
}

function Read-Digest([string] $text, [string] $marker) {
    $match = [regex]::Match($text, "$([regex]::Escape($marker))[^\r\n]*digest=([0-9A-F]{64})")
    if (-not $match.Success) {
        return $null
    }
    return $match.Groups[1].Value
}

$userData = Join-Path $OutputDirectory "user-data"
New-Item -ItemType Directory -Path $userData | Out-Null
$env:TRAIN_MODS_SMOKE_USER_DATA = $userData
$env:TRAIN_AUTHORITY_CHECKPOINT = "1"
$env:TRAIN_AUTHORITY_CHECKPOINT_DIR = $documents
$env:TRAIN_AUTHORITY_BUILD = "issue-126"
$env:TRAIN_PLAYGROUND_CONFIG = "straight-rail-train"
Remove-Item Env:TRAIN_PLAYGROUND_SAVE_ID -ErrorAction SilentlyContinue

$establish = Invoke-Godot (Join-Path $OutputDirectory "establish.log")
$ok = Require-Markers $establish @(
    "PLAYGROUND_SMOKE_MENU_ENTER config=straight-rail-train",
    "AUTHORITY_CHECKPOINT_SEQUENCE_PASSED authority-manufacture-fracture",
    "AUTHORITY_CHECKPOINT_SAVED id=",
    "vehicles=5 fault=null",
    "AUTHORITY_CHECKPOINT_LINE authority format=1 build=issue-126",
    "source=fixed_configuration straight-rail-train",
    "seed=4",
    "arch=X64",
    "AUTHORITY_CHECKPOINT_LINE guarantee Comparable on the same build, Windows x64, and the same .NET runtime.",
    "AUTHORITY_CHECKPOINT_CONTINUATION digest="
) "establish"
if (-not $ok) { exit 1 }

$savedDigest = Read-Digest $establish.Text "AUTHORITY_CHECKPOINT_SAVED"
$continuationDigest = Read-Digest $establish.Text "AUTHORITY_CHECKPOINT_CONTINUATION"
$saveMatch = [regex]::Match($establish.Text, "AUTHORITY_CHECKPOINT_SAVED id=([0-9a-fA-F-]{36})")
if (-not $savedDigest -or -not $continuationDigest -or -not $saveMatch.Success) {
    [Console]::Error.WriteLine("establish did not print a save id and both digests.")
    exit 1
}
$savedFile = Join-Path $documents "saved-canonical.json"
$continuationFile = Join-Path $documents "continuation-canonical.json"
if ((Get-Sha256 $savedFile) -ne $savedDigest -or (Get-Sha256 $continuationFile) -ne $continuationDigest) {
    [Console]::Error.WriteLine("printed digest does not match the SHA-256 of the written canonical file.")
    exit 1
}
if ($savedDigest -eq $continuationDigest) {
    [Console]::Error.WriteLine("continuation digest matched the saved digest.")
    exit 1
}

$env:TRAIN_PLAYGROUND_SAVE_ID = $saveMatch.Groups[1].Value
$resume = Invoke-Godot (Join-Path $OutputDirectory "resume.log")
$ok = Require-Markers $resume @(
    "PLAYGROUND_SMOKE_MENU_ENTER config=straight-rail-train",
    "AUTHORITY_CHECKPOINT_RESTORED id=$($env:TRAIN_PLAYGROUND_SAVE_ID)",
    "vehicles=5 fault=null",
    "AUTHORITY_CHECKPOINT_COMPARE saved difference Equal",
    "AUTHORITY_CHECKPOINT_COMPARE continuation difference Equal",
    "AUTHORITY_CHECKPOINT_LINE authority format=1 build=issue-126",
    "source=fixed_configuration straight-rail-train",
    "AUTHORITY_CHECKPOINT_CONTINUATION digest="
) "resume"
if (-not $ok) { exit 1 }

$restoredDigest = Read-Digest $resume.Text "AUTHORITY_CHECKPOINT_RESTORED"
$resumedContinuation = Read-Digest $resume.Text "AUTHORITY_CHECKPOINT_CONTINUATION"
$restoredFile = Join-Path $documents "restored-canonical.json"
$resumedContinuationFile = Join-Path $documents "restored-continuation-canonical.json"
if ((Get-Sha256 $restoredFile) -ne $restoredDigest -or (Get-Sha256 $resumedContinuationFile) -ne $resumedContinuation) {
    [Console]::Error.WriteLine("restored printed digest does not match the SHA-256 of the written file.")
    exit 1
}
if ($restoredDigest -ne $savedDigest -or $resumedContinuation -ne $continuationDigest) {
    [Console]::Error.WriteLine("cross-process digests diverged.")
    exit 1
}
if (-not [System.Linq.Enumerable]::SequenceEqual(
        [System.IO.File]::ReadAllBytes($savedFile),
        [System.IO.File]::ReadAllBytes($restoredFile)) -or
    -not [System.Linq.Enumerable]::SequenceEqual(
        [System.IO.File]::ReadAllBytes($continuationFile),
        [System.IO.File]::ReadAllBytes($resumedContinuationFile))) {
    [Console]::Error.WriteLine("cross-process canonical bytes diverged.")
    exit 1
}

Write-Host "Authority checkpoint matched across processes."
Write-Host "saved $savedDigest"
Write-Host "continuation $continuationDigest"
exit 0
