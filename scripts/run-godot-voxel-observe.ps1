# Windowed real-GPU observation for issue #39 slice 1 parent sequences plus issue #46 live reload.
# Requires Godot 4.7 .NET on PATH or $env:GODOT. Does not use --headless.
# On Godot 4.7.2 .NET, --quit-after is a main-loop frame budget, not seconds.
# The scene quits itself after VOXEL_OBSERVE_COMPLETE; 300 is only a fail-closed
# safety timeout for the full hold/capture sequence.
param(
    [ValidateSet(1, 2)]
    [int] $Slice = 1,
    [string] $OutputPath = ""
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $env:GODOT -or -not (Test-Path -LiteralPath $env:GODOT)) {
    throw "Set GODOT to the Godot 4.7 .NET executable before running observation."
}

foreach ($name in @(
    "TRAIN_VOXEL_SMOKE",
    "TRAIN_VOXEL_SMOKE_REENTER",
    "TRAIN_VOXEL_BASELINE",
    "TRAIN_CONTENT_SMOKE",
    "TRAIN_CONTENT_EXPORT_SMOKE",
    "TRAIN_MODS_EXPORT_SMOKE",
    "TRAIN_LOG_SMOKE"
)) {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}
if (-not $OutputPath) {
    $OutputPath = if ($Slice -eq 2) { ".godot/issue50-observe-report.md" } else { ".godot/issue45-observe-report.md" }
}
$env:TRAIN_VOXEL_OBSERVE = "1"
$env:TRAIN_VOXEL_SLICE = "$Slice"
$ironJson = Join-Path $root "builtin\core\resources\voxels\iron-ore.voxels.json"
$ironTop = Join-Path $root "builtin\core\resources\voxels\iron-ore\top.png"
$beforeJson = (Get-FileHash -LiteralPath $ironJson -Algorithm SHA256).Hash
$beforeTop = (Get-FileHash -LiteralPath $ironTop -Algorithm SHA256).Hash
$output = & $env:GODOT --path $root --quit-after 300 2>&1 | Out-String
$exitCode = $LASTEXITCODE
Write-Host $output
$afterJson = (Get-FileHash -LiteralPath $ironJson -Algorithm SHA256).Hash
$afterTop = (Get-FileHash -LiteralPath $ironTop -Algorithm SHA256).Hash
if ($beforeJson -ne $afterJson -or $beforeTop -ne $afterTop) {
    throw "Voxel observe rewrote worktree built-in iron-ore content."
}
if ($exitCode -ne 0 -or $output -notlike "*VOXEL_OBSERVE_COMPLETE*") {
    throw "Voxel observe failed: expected VOXEL_OBSERVE_COMPLETE and exit 0."
}

$captures = if ($Slice -eq 2) {
    @(
        "issue50-initial.png",
        "issue50-x-boundary.png",
        "issue50-y-boundary.png",
        "issue50-z-boundary.png",
        "issue50-junction.png",
        "issue50-cross-damage.png",
        "issue50-reload-150.png",
        "issue50-rejected.png"
    )
} else {
    @(
        "issue45-initial.png",
        "issue45-partial.png",
        "issue45-destroyed.png",
        "issue45-internal-cavity.png",
        "issue45-internal-opening.png",
        "issue45-multi.png",
        "issue46-damaged-80.png",
        "issue46-reload-150.png",
        "issue46-display.png",
        "issue46-oriented.png",
        "issue46-reload-60.png",
        "issue46-rejected.png"
    )
}
$missing = @($captures | Where-Object { -not (Test-Path (Join-Path $root ".godot\$_")) })
if ($missing.Count -gt 0) {
    throw "Voxel observe failed. Missing captures: $($missing -join ', ')"
}

$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name
$godotVersion = (& $env:GODOT --version | Out-String).Trim()
$settle = ($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_OBSERVE_MULTI_SETTLE_MS=*" } | Select-Object -Last 1)
$captureLines = @($output -split "`r?`n" | Where-Object { $_ -like "*VOXEL_OBSERVE_CAPTURE*" })
$report = if ($Slice -eq 2) {
    @(
        "# Issue #50 visual observation record (automated facts)",
        "",
        "Environment: Godot ``$godotVersion``, CPU $cpu. Command: `` `$env:GODOT = '$env:GODOT'; .\\scripts\\run-godot-voxel-observe.ps1 -Slice 2 ``.",
        "",
        "Automated facts only: the script set ``TRAIN_VOXEL_OBSERVE=1`` and ``TRAIN_VOXEL_SLICE=2``, launched the windowed project, required ``VOXEL_OBSERVE_COMPLETE`` plus exit 0, required the eight ``.godot/issue50-*.png`` files to exist, and required the worktree iron-ore files to keep their pre-run hashes. It does not inspect PNG pixels.",
        "",
        "## Procedure",
        "",
        "1. Start the project and choose **进入切片 2 体素场景**, or run the observe script above.",
        "2. Confirm the y=0 ground covering ``0..31`` plus the floating ``14..17`` cube, HUD target ``(15,16,15)`` occupied, count 1088.",
        "3. Run ``voxel remove 16 0 8``. Confirm a one-cell hole in the ground at the X=15/16 line and the +X wall of ``(15,0,8)`` with no leftover face.",
        "4. ``voxel reset``, add ``(24,15,8)`` and ``(24,16,8)``, then ``voxel remove 24 16 8``. Confirm a one-cell pillar near the camera whose top is the newly exposed +Y face.",
        "5. ``voxel reset`` then ``voxel remove 8 0 16``. Confirm a ground hole at the Z=15/16 line.",
        "6. ``voxel reset`` then ``voxel remove 16 0 16``. Confirm the four-chunk ground meeting hole without ghosting.",
        "7. ``voxel reset`` then ``voxel damage 15 0 8 100 16 0 8 100 15 0 16 25``. Confirm count 1086 and durability 75 on ``(15,0,16)``.",
        "8. Reset, damage ``(15,16,15)`` by 20 so durability is 80, reload max 150, then reject a missing probe definition and confirm the published world stays.",
        "",
        "## Result",
        "",
        "Automated windowed capture completed with exit 0. Capture log:",
        "",
        ($captureLines | ForEach-Object { "- $_" }),
        "",
        "Automated capture wrote ``.godot/issue50-*.png``. This report records script exit, COMPLETE, capture logs, and file presence only; it does not claim a human visually confirmed the PNGs. Headless smoke is separate automated integration evidence. Collision and save were not exercised."
    )
} else {
    @(
        "# Issue #46 visual observation record (automated facts)",
        "",
        "Environment: Godot ``$godotVersion``, CPU $cpu. Command: `` `$env:GODOT = '$env:GODOT'; .\\scripts\\run-godot-voxel-observe.ps1 ``.",
        "",
        "Automated facts only: the script set ``TRAIN_VOXEL_OBSERVE=1``, launched the windowed project, required ``VOXEL_OBSERVE_COMPLETE`` plus exit 0, required the six historical ``.godot/issue45-*.png`` plus six current ``.godot/issue46-*.png`` files to exist, and required the worktree iron-ore files to keep their pre-run hashes. It does not inspect PNG pixels.",
        "",
        "## Procedure",
        "",
        "1. Start the project and choose **进入切片 1 体素场景**, or run the observe script above.",
        "2. Confirm the initial 8³ block is continuous and HUD says ``(4,4,4)`` is ``core:iron-ore``, ``px_py``, durability ``100``, count 512.",
        "3. Run ``voxel damage 4 4 4 25``. Confirm the cell remains and durability is ``75``.",
        "4. Run ``voxel damage 4 4 4 75``. Confirm the corner is empty, count 511, and the newly exposed neighbor faces are present with no stale exterior face.",
        "5. Run ``voxel reset`` then ``voxel damage 5 5 5 100`` and ``voxel query 5 5 5``. Confirm empty/511. The closed cavity is not claimed to be visible.",
        "6. Run ``voxel damage 4 5 5 100``. Confirm a one-cell-deep opening from the visible ``-X`` exterior and newly exposed interior faces with no overlap, gap, or stale face.",
        "7. Run ``voxel reset`` then ``voxel damage 4 4 4 100 5 4 4 100 6 4 4 25`` and ``voxel query 6 4 4``. Confirm count 510 and durability 75 on the third cell.",
        "8. Reset, damage ``(4,4,4)`` by 20 so durability is 80, then formally reload max durability 150 and an asymmetric top texture. Confirm 80 stays 80. Reorient a visible top cell to ``pz_py`` and another to ``px_pz`` so the pattern rotates or tilts.",
        "9. Reload max durability 60, then add an isolated probe voxel and make its required definition unloadable. Confirm the whole reload is rejected and the published world, orientations, and display stay on the previous result.",
        "",
        "## Result",
        "",
        "Automated windowed capture completed with exit 0. Capture log:",
        "",
        ($captureLines | ForEach-Object { "- $_" }),
        "",
        $(if ($settle) { "Multi-target settlement: $settle" } else { "Multi-target settlement timing was not printed." }),
        "",
        "Automated capture wrote historical ``.godot/issue45-*.png`` and current ``.godot/issue46-*.png``. This report records script exit, COMPLETE, capture logs, and file presence only; it does not claim a human visually confirmed the PNGs. Headless smoke is separate automated integration evidence."
    )
}
$absoluteOutput = Join-Path $root $OutputPath
$parent = Split-Path -Parent $absoluteOutput
New-Item -ItemType Directory -Force -Path $parent | Out-Null
Set-Content -LiteralPath $absoluteOutput -Value $report -Encoding utf8
Write-Host "Wrote $absoluteOutput"
