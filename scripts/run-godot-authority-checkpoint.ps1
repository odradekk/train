param(
    [string] $OutputDirectory
)

# Cross-process authority checkpoint for the formal world editor sequence (#126).
# Two isolated processes share one fresh user-data directory:
#   1. enters through the start-menu world editor button, runs
#      authority-manufacture-fracture on the live session, saves, writes the
#      canonical authority document and the first process's comparison evidence,
#      then adds one voxel and writes the continuation;
#   2. loads that save, decodes the first process's evidence, and repeats the same add.
#   The script then tampers that evidence and expects the restore process to name
#      the mismatch instead of reporting Equal.
# The script hashes the written files itself and compares those digests to the
# digests printed by the game. Historical reports are never reused.
#
# Command (repository root, $env:GODOT pointing at the Godot .NET console exe):
#   pwsh -NoProfile -File scripts/run-godot-authority-checkpoint.ps1
# Exit 2 if Godot is unavailable; exit 1 for a failed process, mismatched digest, or missing marker.
# Saves stay in the output directory via TRAIN_MODS_SMOKE_USER_DATA. This Godot
# build has no --user-data-dir flag. The unified entry sets APPDATA for this process
# so the engine user:// stays inside that run.

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
    foreach ($bad in @("AUTHORITY_CHECKPOINT_FAILED", "WORLD_EDITOR_ESTABLISH_FAILED", "Unhandled exception", "System.InvalidOperationException", "ERROR:")) {
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
$env:TRAIN_WORLD_EDITOR_CONFIG = "straight-rail-train"
Remove-Item Env:TRAIN_WORLD_EDITOR_SAVE_ID -ErrorAction SilentlyContinue

$establish = Invoke-Godot (Join-Path $OutputDirectory "establish.log")
$ok = Require-Markers $establish @(
    "WORLD_EDITOR_SMOKE_MENU_ENTER config=straight-rail-train",
    "AUTHORITY_CHECKPOINT_SEQUENCE_PASSED authority-manufacture-fracture",
    "AUTHORITY_CHECKPOINT_SAVED id=",
    "vehicles=5 fault=null",
    "AUTHORITY_CHECKPOINT_BUILD id=",
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
$buildMatch = [regex]::Match($establish.Text, "AUTHORITY_CHECKPOINT_BUILD id=([0-9A-F]{64}) file=(.+)")
if (-not $savedDigest -or -not $continuationDigest -or -not $saveMatch.Success -or -not $buildMatch.Success) {
    [Console]::Error.WriteLine("establish did not print a save id, both digests, and a build file.")
    exit 1
}
$buildId = $buildMatch.Groups[1].Value
$buildFile = $buildMatch.Groups[2].Value.Trim()
if ((Get-Sha256 $buildFile) -ne $buildId) {
    [Console]::Error.WriteLine("printed build id does not match the SHA-256 of $buildFile.")
    exit 1
}
if (-not $establish.Text.Contains("build=$buildId")) {
    [Console]::Error.WriteLine("establish observation did not report the assembly build id.")
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

$env:TRAIN_WORLD_EDITOR_SAVE_ID = $saveMatch.Groups[1].Value
$resume = Invoke-Godot (Join-Path $OutputDirectory "resume.log")
$ok = Require-Markers $resume @(
    "WORLD_EDITOR_SMOKE_MENU_ENTER config=straight-rail-train",
    "AUTHORITY_CHECKPOINT_RESTORED id=$($env:TRAIN_WORLD_EDITOR_SAVE_ID)",
    "vehicles=5 fault=null",
    "AUTHORITY_CHECKPOINT_COMPARE saved difference Equal",
    "AUTHORITY_CHECKPOINT_COMPARE continuation difference Equal",
    "AUTHORITY_CHECKPOINT_BUILD id=",
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
if (-not $resume.Text.Contains("AUTHORITY_CHECKPOINT_BUILD id=$buildId ") -or -not $resume.Text.Contains("build=$buildId")) {
    [Console]::Error.WriteLine("resume did not report the same assembly build id.")
    exit 1
}
$savedIndex = [regex]::Match($resume.Text, "AUTHORITY_CHECKPOINT_COMPARE saved checkpoint ([1-9][0-9]*) input (.+) boundary ")
$continuedIndex = [regex]::Match($resume.Text, "AUTHORITY_CHECKPOINT_COMPARE continuation checkpoint ([1-9][0-9]*) input (.+) boundary ")
$savedInput = $savedIndex.Groups[2].Value
$continuedInput = $continuedIndex.Groups[2].Value
if (-not $savedIndex.Success -or -not $continuedIndex.Success -or
    [int]$continuedIndex.Groups[1].Value -ne [int]$savedIndex.Groups[1].Value + 1 -or
    $savedInput -notlike '*"kind":"damage_latest"*' -or
    $savedInput -notlike '*"x":3,"y":1,"z":1*' -or
    $savedInput -notlike '*"amount":100*' -or
    $savedInput -notlike '*"vehicle_id":*' -or
    $savedInput -eq "damage latest 3,1,1 100" -or
    $continuedInput -notlike '*"kind":"voxel.add"*' -or
    $continuedInput -notlike '*"x":10,"y":10,"z":10*' -or
    $continuedInput -eq "edit voxel.add") {
    [Console]::Error.WriteLine("resume did not keep the original checkpoint index and canonical input.")
    exit 1
}

function Set-EvidenceProperty([string] $docs, [string] $property, [string] $value) {
    $path = Join-Path $docs "saved-evidence.json"
    $node = [System.Text.Json.Nodes.JsonNode]::Parse([System.IO.File]::ReadAllText($path))
    $node[$property] = $value
    [System.IO.File]::WriteAllText($path, $node.ToJsonString())
}

function Invoke-NegativeCase([string] $name, [string] $property, [string] $value, [string] $kind, [string] $field) {
    $caseRoot = Join-Path $OutputDirectory "negative-$name"
    $caseDocs = Join-Path $caseRoot "documents"
    New-Item -ItemType Directory -Path $caseDocs | Out-Null
    Copy-Item -Path (Join-Path $documents "*") -Destination $caseDocs
    Set-EvidenceProperty $caseDocs $property $value
    $env:TRAIN_AUTHORITY_CHECKPOINT_DIR = $caseDocs
    $run = Invoke-Godot (Join-Path $caseRoot "resume.log")
    $failed = $false
    if ($run.Code -eq 0) {
        [Console]::Error.WriteLine("$name exited 0.")
        $failed = $true
    }
    if ($run.Text -notmatch "AUTHORITY_CHECKPOINT_COMPARE saved checkpoint [1-9]") {
        [Console]::Error.WriteLine("$name did not report the original checkpoint index.")
        $failed = $true
    }
    foreach ($marker in @(
        "AUTHORITY_CHECKPOINT_FAILED",
        "AUTHORITY_CHECKPOINT_COMPARE saved difference $kind",
        "AUTHORITY_CHECKPOINT_COMPARE saved field $field"
    )) {
        if (-not $run.Text.Contains($marker)) {
            [Console]::Error.WriteLine("$name is missing marker: $marker")
            $failed = $true
        }
    }
    if ($run.Text.Contains("AUTHORITY_CHECKPOINT_COMPARE saved difference Equal")) {
        [Console]::Error.WriteLine("$name reported Equal.")
        $failed = $true
    }
    return -not $failed
}

if (-not (Invoke-NegativeCase "build" "build" "other-build" "EnvironmentMismatch" "build")) { exit 1 }
if (-not (Invoke-NegativeCase "content" "content_digest" ("C" * 64) "ContentAdaptation" "content")) { exit 1 }
if (-not (Invoke-NegativeCase "source" "configuration_id" "straight-rail-empty" "SourceMismatch" "source")) { exit 1 }

Write-Host "Authority checkpoint matched across processes."
Write-Host "saved $savedDigest"
Write-Host "continuation $continuationDigest"
Write-Host "build $buildId"
exit 0
