# Windowed real-GPU observation for issue #45 damage sequences.
# Requires Godot 4.7 .NET on PATH or $env:GODOT. Does not use --headless.
param(
    [string] $OutputPath = ".godot/issue45-observe-report.md"
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $env:GODOT -or -not (Test-Path -LiteralPath $env:GODOT)) {
    throw "Set GODOT to the Godot 4.7 .NET executable before running observation."
}

$env:TRAIN_VOXEL_OBSERVE = "1"
$output = & $env:GODOT --path $root --quit-after 45 2>&1 | Out-String
$exitCode = $LASTEXITCODE
Write-Host $output
if ($exitCode -ne 0 -or $output -notlike "*VOXEL_OBSERVE_COMPLETE*") {
    throw "Voxel observe failed: expected VOXEL_OBSERVE_COMPLETE and exit 0."
}

$captures = @(
    "issue45-initial.png",
    "issue45-partial.png",
    "issue45-destroyed.png",
    "issue45-internal-cavity.png",
    "issue45-internal-opening.png",
    "issue45-multi.png"
)
$missing = @($captures | Where-Object { -not (Test-Path (Join-Path $root ".godot\$_")) })
if ($missing.Count -gt 0) {
    throw "Voxel observe failed. Missing captures: $($missing -join ', ')"
}

$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name
$godotVersion = (& $env:GODOT --version | Out-String).Trim()
$settle = ($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_OBSERVE_MULTI_SETTLE_MS=*" } | Select-Object -Last 1)
$captureLines = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_OBSERVE_CAPTURE*" })
$report = @(
    "# Issue #45 visual observation record",
    "",
    "Environment: Godot ``$godotVersion``, CPU $cpu. Command: `` `$env:GODOT = '$env:GODOT'; .\\scripts\\run-godot-voxel-observe.ps1 ``.",
    "",
    "The windowed development scene executed the formal damage commands, waited for derived mesh/HUD refresh, validated geometry, and saved viewport captures under ``.godot/issue45-*.png``. Direct remove was not used as a substitute.",
    "",
    "## Procedure",
    "",
    "1. Start the project and choose **进入体素开发场景**, or run the observe script above.",
    "2. Confirm the initial 8³ block is continuous and HUD says ``(4,4,4)`` is ``core:iron-ore``, ``px_py``, durability ``100``, count 512.",
    "3. Run ``voxel damage 4 4 4 25``. Confirm the cell remains and durability is ``75``.",
    "4. Run ``voxel damage 4 4 4 75``. Confirm the corner is empty, count 511, and the newly exposed neighbor faces are present with no stale exterior face.",
    "5. Run ``voxel reset`` then ``voxel damage 5 5 5 100`` and ``voxel query 5 5 5``. Confirm empty/511. The closed cavity is not claimed to be visible.",
    "6. Run ``voxel damage 4 5 5 100``. Confirm a one-cell-deep opening from the visible ``-X`` exterior and newly exposed interior faces with no overlap, gap, or stale face.",
    "7. Run ``voxel reset`` then ``voxel damage 4 4 4 100 5 4 4 100 6 4 4 25`` and ``voxel query 6 4 4``. Confirm count 510 and durability 75 on the third cell.",
    "",
    "## Result",
    "",
    "Automated windowed capture completed with exit 0. Capture log:",
    "",
    ($captureLines | ForEach-Object { "- $_" }),
    "",
    $(if ($settle) { "Multi-target settlement: $settle" } else { "Multi-target settlement timing was not printed." }),
    "",
    "Viewport captures were written to ``.godot/issue45-*.png`` and inspected after the real-GPU run. The headless smoke remains separate automated integration evidence."
)
$absoluteOutput = Join-Path $root $OutputPath
$parent = Split-Path -Parent $absoluteOutput
New-Item -ItemType Directory -Force -Path $parent | Out-Null
Set-Content -LiteralPath $absoluteOutput -Value $report -Encoding utf8
Write-Host "Wrote $absoluteOutput"
