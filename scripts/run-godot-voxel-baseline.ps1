# Reproducible measurement baseline. The real Godot scene performs one
# slice 1 warm-up, three recorded slice 1 100-cell y=0 add/remove runs (10 s each),
# three slice 2 cross-boundary 100-pair add/remove runs in the same process,
# one unrecorded save/load warm-up and three recorded world save/load timings,
# then one unrecorded slice 3 persist warm-up and three recorded parked-vehicle
# 100-pair move/return + bridge damage + save/load timings (10 s frames each).
# Results are written to docs/validation/issue-62-voxel-baseline.md by default.
# This script does not overwrite docs/validation/issue-53-voxel-baseline.md.
param(
    [string] $OutputPath = "docs/validation/issue-62-voxel-baseline.md"
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
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

$userData = Join-Path ([System.IO.Path]::GetTempPath()) ("train-issue53-baseline-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $userData | Out-Null
$env:TRAIN_VOXEL_BASELINE = "1"
$env:TRAIN_MODS_SMOKE_USER_DATA = $userData
try {
    $output = & $env:GODOT --headless --path $root 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item "Env:TRAIN_VOXEL_BASELINE" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_MODS_SMOKE_USER_DATA" -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $userData) {
        Remove-Item -LiteralPath $userData -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host $output
$runs = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_RUN run=*" })
$slice2Runs = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_SLICE2_RUN*" })
$saveRuns = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_SAVE_RUN*" })
$slice3Runs = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_SLICE3_RUN*" })
if ($exitCode -ne 0 -or $runs.Count -ne 3 -or $slice2Runs.Count -ne 3 -or $saveRuns.Count -ne 3 -or $slice3Runs.Count -ne 3 -or $output -notlike "*VOXEL_BASELINE_COMPLETE*") {
    throw "Voxel baseline failed: expected three slice 1 runs, three slice 2 boundary runs, three save/load runs, three slice 3 persist runs, and the completion marker."
}

$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name
$memory = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
$runtime = (& dotnet --list-runtimes | Select-String -Pattern "^Microsoft.NETCore.App 8\." | Select-Object -Last 1).ToString().Trim()
$godotVersion = (& $env:GODOT --version | Out-String).Trim()
$report = @(
    "# Issue #62 voxel baseline",
    "",
    ("Command: `$env:GODOT = '" + $env:GODOT + "'; .\\scripts\\run-godot-voxel-baseline.ps1"),
    "",
    "- Godot: $godotVersion",
    "- .NET: $runtime",
    "- CPU: $cpu",
    "- Physical memory bytes: $memory",
    "- Sequence: one warm-up of the slice 1 100-cell y=0 add/remove pairs; three recorded slice 1 runs (10 s each); then the same process switches to the slice 2 fixture and records three 100-pair cross-boundary add/remove runs (x=15/16, y=1); then one unrecorded save/load warm-up and three recorded world save/load timings; then one unrecorded slice 3 persist warm-up and three recorded 100-pair move/return + bridge damage + save/restore timings (10 s frames each). Isolated temporary user-data; does not write the player's saves. No FPS or vehicle-count threshold is asserted.",
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
$absoluteOutput = Join-Path $root $OutputPath
$parent = Split-Path -Parent $absoluteOutput
New-Item -ItemType Directory -Force -Path $parent | Out-Null
Set-Content -LiteralPath $absoluteOutput -Value $report -Encoding utf8
Write-Host "Wrote $absoluteOutput"
