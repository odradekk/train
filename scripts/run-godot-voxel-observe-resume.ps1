# Windowed save → quit → new-process load observation for issue #53.
# Process 1 writes the slice 2 fixed sequence (including ledge) and exits.
# Process 2 is a new Godot process that loads the same UUID and captures
# restored / ground-hole / ledge / continue-damage frames.
# Requires Godot 4.7 .NET on PATH or $env:GODOT. Does not use --headless.
# On Godot 4.7.2 .NET, --quit-after is a main-loop frame budget, not seconds.
param(
    [string] $OutputPath = ".godot/issue53-resume-observe-report.md"
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $env:GODOT -or -not (Test-Path -LiteralPath $env:GODOT)) {
    throw "Set GODOT to the Godot 4.7 .NET executable before running resume observation."
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

$ironJson = Join-Path $root "builtin\core\resources\voxels\iron-ore.voxels.json"
$ledgeJson = Join-Path $root "builtin\core\resources\voxels\ledge.voxels.json"
$ironTop = Join-Path $root "builtin\core\resources\voxels\iron-ore\top.png"
$beforeJson = (Get-FileHash -LiteralPath $ironJson -Algorithm SHA256).Hash
$beforeLedge = (Get-FileHash -LiteralPath $ledgeJson -Algorithm SHA256).Hash
$beforeTop = (Get-FileHash -LiteralPath $ironTop -Algorithm SHA256).Hash

$captures = @(
    "issue53-resume-restored.png",
    "issue53-resume-chunk-boundary.png",
    "issue53-resume-ground-hole.png",
    "issue53-resume-ledge.png",
    "issue53-resume-continue-damage.png"
)
$captureDir = Join-Path $root ".godot"
$recordDir = Join-Path $root "docs\validation\issue-53-resume"
New-Item -ItemType Directory -Force -Path $captureDir | Out-Null
New-Item -ItemType Directory -Force -Path $recordDir | Out-Null

$userData = Join-Path ([System.IO.Path]::GetTempPath()) ("train-issue53-observe-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $userData | Out-Null
$env:TRAIN_MODS_SMOKE_USER_DATA = $userData
$env:TRAIN_VOXEL_SLICE = "2"
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
    $write = Invoke-ResumeObserve -QuitAfter 90 -Label "write"
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
    $resume = Invoke-ResumeObserve -QuitAfter 180 -Label "load"
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
if ($beforeJson -ne $afterJson -or $beforeLedge -ne $afterLedge -or $beforeTop -ne $afterTop) {
    throw "Resume observe rewrote worktree built-in iron-ore or ledge content."
}

$missing = @($captures | Where-Object { -not (Test-Path (Join-Path $captureDir $_)) })
if ($missing.Count -gt 0) {
    throw "Resume observe failed. Missing captures: $($missing -join ', ')"
}

$hashes = @()
foreach ($name in $captures) {
    $source = Join-Path $captureDir $name
    Copy-Item -LiteralPath $source -Destination (Join-Path $recordDir $name) -Force
    $hashes += [pscustomobject]@{
        Name = $name
        Hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    }
}

$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name
$godotVersion = (& $env:GODOT --version | Out-String).Trim()
$captureLines = @($combined -split "`r?`n" | Where-Object { $_ -like "*VOXEL_OBSERVE_CAPTURE*" -or $_ -like "*VOXEL_OBSERVE_RESUME_*" })
$written = ($combined -split "`r?`n" | Where-Object { $_ -like "*VOXEL_OBSERVE_RESUME_WRITTEN*" } | Select-Object -Last 1)
$loaded = ($combined -split "`r?`n" | Where-Object { $_ -like "*VOXEL_OBSERVE_RESUME_LOADED*" } | Select-Object -Last 1)

$report = @(
    "# Issue #53 resume observation (automated facts)",
    "",
    "Environment: Godot ``$godotVersion``, CPU $cpu. Command: `` `$env:GODOT = '$env:GODOT'; .\\scripts\\run-godot-voxel-observe-resume.ps1 ``.",
    "",
    "Automated facts only: process 1 set ``TRAIN_VOXEL_OBSERVE_RESUME=1`` with isolated ``TRAIN_MODS_SMOKE_USER_DATA``, wrote the slice 2 fixed sequence, printed a UUID, and exited. Process 2 was a new Godot process with the same user-data and ``TRAIN_VOXEL_OBSERVE_SAVE_ID``. Required ``VOXEL_OBSERVE_RESUME_COMPLETE``, exit 0, four ``issue53-resume-*.png`` files, and unchanged worktree iron-ore/ledge hashes. The script does not inspect PNG pixels and does not reuse ``issue50-*.png`` or ``issue52-*.png``.",
    "",
    "- $written",
    "- $loaded",
    "",
    "Copied captures:",
    ($hashes | ForEach-Object { "- ``docs/validation/issue-53-resume/$($_.Name)`` SHA256 ``$($_.Hash)``" }),
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
