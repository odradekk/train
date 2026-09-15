# Reproducible measurement baseline. The real Godot scene performs one
# slice 1 warm-up, three recorded slice 1 100-cell y=0 add/remove runs (10 s each),
# three slice 2 cross-boundary 100-pair add/remove runs in the same process,
# one unrecorded save/load warm-up and three recorded world save/load timings,
# then one unrecorded slice 3 persist warm-up and three recorded parked-vehicle
# 100-pair move/return (mutation_ms), bridge damage/structural resolution
# (structure_ms), display Refresh (refresh_ms), and save/load timings
# (10 s frames each). structure_ms is the Damage call only; refresh_ms is
# not reported as structural settlement.
# Results are written to docs/validation/issue-62-voxel-baseline.md by default.
# This script does not overwrite docs/validation/issue-53-voxel-baseline.md.
# -Slice 4 is a separate 1800-step straight-rail presentation baseline and
# writes docs/validation/issue-74-rail-presentation.md by default. It does
# not mix slice 1–3 mutation load into the same recorded numbers.
param(
    [ValidateSet(0, 4)]
    [int] $Slice = 0,
    [string] $OutputPath = ""
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
. (Join-Path $PSScriptRoot "Get-HostHardwareInfo.ps1")
if (-not $env:GODOT -or -not (Test-Path -LiteralPath $env:GODOT)) {
    throw "Set GODOT to the Godot 4.7 .NET executable before running this baseline."
}

foreach ($name in @(
    "TRAIN_VOXEL_SMOKE",
    "TRAIN_VOXEL_SMOKE_REENTER",
    "TRAIN_VOXEL_SMOKE_RESUME",
    "TRAIN_VOXEL_SMOKE_SAVE_ID",
    "TRAIN_VOXEL_SLICE",
    "TRAIN_VOXEL_OBSERVE",
    "TRAIN_CONTENT_SMOKE",
    "TRAIN_CONTENT_EXPORT_SMOKE",
    "TRAIN_MODS_EXPORT_SMOKE",
    "TRAIN_LOG_SMOKE"
)) {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}

if (-not $OutputPath) {
    $OutputPath = if ($Slice -eq 4) { "docs/validation/issue-74-rail-presentation.md" } else { "docs/validation/issue-62-voxel-baseline.md" }
}
$userPrefix = if ($Slice -eq 4) { "train-issue74-baseline-" } else { "train-issue53-baseline-" }
$userData = Join-Path ([System.IO.Path]::GetTempPath()) ($userPrefix + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $userData | Out-Null
$env:TRAIN_VOXEL_BASELINE = "1"
$env:TRAIN_MODS_SMOKE_USER_DATA = $userData
if ($Slice -eq 4) {
    $env:TRAIN_VOXEL_SLICE = "4"
}
try {
    $output = & $env:GODOT --headless --path $root 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item "Env:TRAIN_VOXEL_BASELINE" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_VOXEL_SLICE" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_MODS_SMOKE_USER_DATA" -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $userData) {
        Remove-Item -LiteralPath $userData -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host $output
if ($Slice -eq 4) {
    $slice4Runs = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_SLICE4_RUN*" })
    if ($exitCode -ne 0 -or $slice4Runs.Count -ne 3 -or $output -notlike "*VOXEL_BASELINE_COMPLETE*") {
        throw "Voxel baseline failed: expected three slice 4 1800-step presentation runs and the completion marker."
    }
} else {
    $runs = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_RUN run=*" })
    $slice2Runs = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_SLICE2_RUN*" })
    $saveRuns = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_SAVE_RUN*" })
    $slice3Runs = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_SLICE3_RUN*" })
    if ($exitCode -ne 0 -or $runs.Count -ne 3 -or $slice2Runs.Count -ne 3 -or $saveRuns.Count -ne 3 -or $slice3Runs.Count -ne 3 -or $output -notlike "*VOXEL_BASELINE_COMPLETE*") {
        throw "Voxel baseline failed: expected three slice 1 runs, three slice 2 boundary runs, three save/load runs, three slice 3 persist runs, and the completion marker."
    }
}

$cpu = Get-HostCpuName
$memory = Get-HostPhysicalMemoryBytes
$runtime = (& dotnet --list-runtimes | Select-String -Pattern "^Microsoft.NETCore.App 10\." | Select-Object -Last 1).ToString().Trim()
$godotVersion = (& $env:GODOT --version | Out-String).Trim()
$report = if ($Slice -eq 4) {
    @(
        "# Issue #74 straight-rail presentation baseline",
        "",
        ("Command: `$env:GODOT = '" + $env:GODOT + "'; .\\scripts\\run-godot-voxel-baseline.ps1 -Slice 4"),
        "",
        "- Godot: $godotVersion",
        "- .NET: $runtime",
        "- CPU: $cpu",
        "- Physical memory bytes: $memory",
        "- Sequence: one unrecorded warm-up of construct + assemble + start + 1800 steps + single gap damage + save/load; then three recorded runs of the same formal commands. Each recorded line reports voxels, sim_ms (1800-step Advance), mutation_ms (single gap damage), refresh_ms (display Refresh), save_ms / load_ms / file_bytes, and a 10 s paused display-frame sample. Isolated temporary user-data. Fedora numbers are not Windows x64 evidence.",
        "",
        "## Slice 4 recorded runs",
        "",
        $slice4Runs
    )
} else {
    @(
    "# Issue #62 voxel baseline",
    "",
    ("Command: `$env:GODOT = '" + $env:GODOT + "'; .\\scripts\\run-godot-voxel-baseline.ps1"),
    "",
    "- Godot: $godotVersion",
    "- .NET: $runtime",
    "- CPU: $cpu",
    "- Physical memory bytes: $memory",
    "- Sequence: one warm-up of the slice 1 100-cell y=0 add/remove pairs; three recorded slice 1 runs (10 s each); then the same process switches to the slice 2 fixture and records three 100-pair cross-boundary add/remove runs (x=15/16, y=1); then one unrecorded save/load warm-up and three recorded world save/load timings; then one unrecorded slice 3 persist warm-up and three recorded 100-pair move/return (mutation_ms), bridge damage/structural resolution (structure_ms), display Refresh (refresh_ms), and save/restore timings (10 s frames each). Isolated temporary user-data; does not write the player's saves. No FPS or vehicle-count threshold is asserted.",
    "",
    "## Slice 1 recorded runs",
    "",
    $runs,
    "",
    "## Slice 2 cross-boundary recorded runs",
    "",
    $slice2Runs,
    "",
    "## World save / load recorded runs",
    "",
    $saveRuns,
    "",
    "## Slice 3 persist recorded runs",
    "",
    $slice3Runs
    )
}
$absoluteOutput = Join-Path $root $OutputPath
$parent = Split-Path -Parent $absoluteOutput
New-Item -ItemType Directory -Force -Path $parent | Out-Null
Set-Content -LiteralPath $absoluteOutput -Value $report -Encoding utf8
Write-Host "Wrote $absoluteOutput"
