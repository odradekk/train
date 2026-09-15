# Windows x64 authority checks for issue #74.
# This script must run on Windows with the same git SHA, Godot 4.7.2 .NET,
# .NET 10 runtime, and the same published content as the reviewed checkout.
# It does not treat a Fedora export or a mounted train.exe as a passing run.
#
# From the repository root:
#   $env:GODOT = "<Godot_v4.7.2-stable_mono_win64.exe>"
#   pwsh -NoProfile -File scripts/run-windows-issue74-authority.ps1
#
# Evidence kind: source-build tests (dotnet test), not the exported train.exe.
# Export directory D:\train-builds\issue74\<SHA>\ is a separate artifact and
# must not be mixed into the same result line.

param(
    [string] $OutputPath = ".godot/validation/issue74/windows-authority.md"
)

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$sha = (git rev-parse HEAD).Trim()
$os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
$sdk = (& dotnet --version).Trim()
$runtime = (& dotnet --list-runtimes | Select-String -Pattern "^Microsoft.NETCore.App 10\." | Select-Object -Last 1)
$godot = if ($env:GODOT) { $env:GODOT } else { "" }
$godotVersion = if ($godot -and (Test-Path -LiteralPath $godot)) { (& $godot --version | Out-String).Trim() } else { "unset" }

$lines = @(
    "# Issue #74 Windows x64 authority",
    "",
    "- SHA: $sha",
    "- OS: $os",
    "- dotnet SDK: $sdk",
    "- runtime: $runtime",
    "- GODOT: $godot",
    "- Godot version: $godotVersion",
    "- Evidence kind: source checkout ``dotnet test`` / optional Godot smoke. Not the exported ``train.exe``.",
    "- Content: built-in published catalog from this checkout (``core`` voxels/composites, generator ``slice-4-straight-rail-v1``).",
    ""
)

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
    $lines += @(
        "BLOCKED: this host is not Windows. Exporting or mounting a build is not a Windows runtime result.",
        "Required: Windows x64, same SHA $sha, Godot 4.7.2 .NET, Microsoft.NETCore.App 10, PowerShell 7.",
        "",
        "Commands to run on that machine:",
        "",
        '```powershell',
        'git rev-parse HEAD',
        'dotnet --list-runtimes',
        '$env:GODOT = "<Godot_v4.7.2-stable_mono_win64.exe>"',
        'dotnet test Train.Tests/Train.Tests.csproj --filter FullyQualifiedName~Start_at_zero_save_at_900_new_session_load_matches_uninterrupted_checkpoints',
        'dotnet test Train.Tests/Train.Tests.csproj --filter FullyQualifiedName~Formal_save_at_330_resumes_the_same_authority_and_keeps_zero_after_coast_stop',
        'dotnet test Train.Tests/Train.Tests.csproj --filter FullyQualifiedName~Formal_files_restore_halts_and_reject_missing_geometry_without_replacing_the_session',
        'pwsh -NoProfile -File scripts/run-godot-voxel-smoke.ps1 -Slice 4',
        '```',
        "",
        "The 900 test records hashes at 0/300/600/900/1200/1500/1800 and compares the save branch to the uninterrupted run.",
        "The 330 test is the coupler-coast save branch. The Formal_files test includes the FirstSegmentDamageCell gap-stop save branch.",
        "Compare ``WorldSaveAuthority.Fingerprint`` / ``CaptureTrainAuthority`` equality. Geometry error is not a substitute."
    )
    $absolute = Join-Path $root $OutputPath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $absolute) | Out-Null
    Set-Content -LiteralPath $absolute -Value $lines -Encoding utf8
    Write-Host ($lines -join [Environment]::NewLine)
    Write-Host "Wrote $absolute"
    exit 2
}

if (-not $godot -or -not (Test-Path -LiteralPath $godot)) {
    throw "Set GODOT to the Godot 4.7.2 .NET Windows executable before running Windows authority checks."
}

$filters = @(
    "FullyQualifiedName~Start_at_zero_save_at_900_new_session_load_matches_uninterrupted_checkpoints",
    "FullyQualifiedName~Formal_save_at_330_resumes_the_same_authority_and_keeps_zero_after_coast_stop",
    "FullyQualifiedName~Formal_files_restore_halts_and_reject_missing_geometry_without_replacing_the_session"
)

foreach ($filter in $filters) {
    Write-Host "Running $filter"
    & dotnet test (Join-Path $root "Train.Tests/Train.Tests.csproj") --filter $filter --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Windows authority test failed: $filter"
    }
    $lines += "- PASS ``dotnet test --filter $filter``"
}

Write-Host "Running slice 4 Godot smoke"
& pwsh -NoProfile -File (Join-Path $root "scripts/run-godot-voxel-smoke.ps1") -Slice 4
if ($LASTEXITCODE -ne 0) {
    throw "Windows slice 4 smoke failed."
}
$lines += "- PASS ``scripts/run-godot-voxel-smoke.ps1 -Slice 4``"
$lines += "- Result: Windows x64 source-build authority checks passed for SHA $sha."

$absolute = Join-Path $root $OutputPath
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $absolute) | Out-Null
Set-Content -LiteralPath $absolute -Value $lines -Encoding utf8
Write-Host "Wrote $absolute"
