# Reproducible issue #50 measurement baseline. The real Godot scene performs one
# slice 1 warm-up, three recorded slice 1 100-cell y=0 add/remove runs (10 s each),
# then three slice 2 cross-boundary 100-pair add/remove runs in the same process.
# Results are written to docs/validation/issue-50-voxel-baseline.md by default.
param(
    [string] $OutputPath = "docs/validation/issue-50-voxel-baseline.md"
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $env:GODOT -or -not (Test-Path -LiteralPath $env:GODOT)) {
    throw "Set GODOT to the Godot 4.7 .NET executable before running this baseline."
}

$env:TRAIN_VOXEL_BASELINE = "1"
$output = & $env:GODOT --headless --path $root 2>&1 | Out-String
$exitCode = $LASTEXITCODE
Write-Host $output
$runs = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_RUN run=*" })
$slice2Runs = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_SLICE2_RUN*" })
if ($exitCode -ne 0 -or $runs.Count -ne 3 -or $slice2Runs.Count -ne 3 -or $output -notlike "*VOXEL_BASELINE_COMPLETE*") {
    throw "Voxel baseline failed: expected three slice 1 runs, three slice 2 boundary runs, and the completion marker."
}

$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name
$memory = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
$runtime = (& dotnet --list-runtimes | Select-String -Pattern "^Microsoft.NETCore.App 8\." | Select-Object -Last 1).ToString().Trim()
$godotVersion = (& $env:GODOT --version | Out-String).Trim()
$report = @(
    "# Issue #50 voxel baseline",
    "",
    ("Command: `$env:GODOT = '" + $env:GODOT + "'; .\\scripts\\run-godot-voxel-baseline.ps1"),
    "",
    "- Godot: $godotVersion",
    "- .NET: $runtime",
    "- CPU: $cpu",
    "- Physical memory bytes: $memory",
    "- Sequence: one warm-up of the slice 1 100-cell y=0 add/remove pairs; three recorded slice 1 runs (10 s each); then the same process switches to the slice 2 fixture and records three 100-pair cross-boundary add/remove runs (x=15/16, y=1) through the formal world command boundary.",
    "",
    "## Slice 1 recorded runs",
    "",
    $runs,
    "",
    "## Slice 2 cross-boundary recorded runs",
    "",
    $slice2Runs
)
$absoluteOutput = Join-Path $root $OutputPath
$parent = Split-Path -Parent $absoluteOutput
New-Item -ItemType Directory -Force -Path $parent | Out-Null
Set-Content -LiteralPath $absoluteOutput -Value $report -Encoding utf8
Write-Host "Wrote $absoluteOutput"
