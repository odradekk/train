# Reproducible issue #44 measurement baseline. The real Godot scene performs one
# warm-up, then three 100-cell y=0 add/remove runs and observes each for 10 seconds.
# Results are written to docs/validation/issue-44-voxel-baseline.md by default.
param(
    [string] $OutputPath = "docs/validation/issue-44-voxel-baseline.md"
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
$runs = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_BASELINE_RUN*" })
if ($exitCode -ne 0 -or $runs.Count -ne 3 -or $output -notlike "*VOXEL_BASELINE_COMPLETE*") {
    throw "Voxel baseline failed: expected three run records and Voxel baseline completion marker."
}

$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name
$memory = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
$runtime = (& dotnet --list-runtimes | Select-String -Pattern "^Microsoft.NETCore.App 8\." | Select-Object -Last 1).ToString().Trim()
$godotVersion = (& $env:GODOT --version | Out-String).Trim()
$report = @(
    "# Issue #44 voxel baseline",
    "",
    ("Command: `$env:GODOT = '" + $env:GODOT + "'; .\\scripts\\run-godot-voxel-baseline.ps1"),
    "",
    "- Godot: $godotVersion",
    "- .NET: $runtime",
    "- CPU: $cpu",
    "- Physical memory bytes: $memory",
    "- Sequence: reset; warm up once; y=0 positions ordered by x then z, first 100 cells; each cell is added then removed through the formal world command boundary; three recorded runs; each run observes frame timing for at least 10 seconds.",
    "",
    "## Recorded runs",
    "",
    $runs
)
$absoluteOutput = Join-Path $root $OutputPath
$parent = Split-Path -Parent $absoluteOutput
New-Item -ItemType Directory -Force -Path $parent | Out-Null
Set-Content -LiteralPath $absoluteOutput -Value $report -Encoding utf8
Write-Host "Wrote $absoluteOutput"
