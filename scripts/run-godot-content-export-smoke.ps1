# Repeatable Windows export smoke for issue #19 raw JSON/PNG inclusion.
#
# Commands (from the repository root, with Godot 4.7 .NET on PATH or $env:GODOT):
#   & $godot --headless --path . --export-release "Windows Desktop" builds/content-smoke/train.exe
#   $env:TRAIN_CONTENT_EXPORT_SMOKE = "1"
#   .\builds\content-smoke\train.exe
#
# Requires the exported process stdout to contain:
#   CONTENT_EXPORT_IRON_ORE_LOADED
#   CONTENT_EXPORT_PNG_DECODED
#   CONTENT_EXPORT_INIT_OK
#   CONTENT_EXPORT_JSON_READABLE
#   CONTENT_EXPORT_PNG_READABLE
#   CONTENT_EXPORT_JSONL_CREATED
#
# Exit 2 if Godot is unavailable and no exported binary was supplied.
# Exit 1 if export or the exported process fails, or a required marker is missing.

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
. (Join-Path $PSScriptRoot "Invoke-HiddenProcess.ps1")

function Find-Godot {
    if ($env:GODOT -and (Test-Path -LiteralPath $env:GODOT)) {
        return $env:GODOT
    }

    if ($env:TRAIN_CONTENT_EXPORT_SMOKE_NO_SEARCH -eq "1") {
        return $null
    }

    foreach ($name in @("godot", "godot-mono", "Godot_v4.7.2-stable_mono_win64_console", "Godot_v4.7.2-stable_mono_win64")) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) {
            return $cmd.Source
        }
    }

    $roots = @(
        $env:GODOT_HOME,
        "$env:LOCALAPPDATA\Programs\Godot",
        "$env:ProgramFiles\Godot",
        "${env:ProgramFiles(x86)}\Godot"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($dir in $roots) {
        $hit = Get-ChildItem -LiteralPath $dir -Filter "Godot*_mono*_console.exe" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $hit) {
            $hit = Get-ChildItem -LiteralPath $dir -Filter "Godot*.exe" -File -ErrorAction SilentlyContinue |
                Select-Object -First 1
        }
        if ($hit) {
            return $hit.FullName
        }
    }

    return $null
}

$exported = $env:TRAIN_CONTENT_EXPORT_BIN
$godot = Find-Godot
$exportCommand = '& <godot> --headless --path . --export-release "Windows Desktop" builds/content-smoke/train.exe'
$runCommand = '$env:TRAIN_CONTENT_EXPORT_SMOKE="1"; .\builds\content-smoke\train.exe'
if (-not $exported -and -not $godot) {
    [Console]::Error.WriteLine("Godot executable not found (PATH, GODOT, GODOT_HOME, or standard Program Files).")
    [Console]::Error.WriteLine("Unrun export command: $exportCommand")
    [Console]::Error.WriteLine("Unrun exported-process command: $runCommand")
    exit 2
}

if (-not $exported) {
    $exported = Join-Path $root "builds/content-smoke/train.exe"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $exported) | Out-Null
    Write-Host "Using Godot: $godot"
    & $godot --headless --path $root --export-release "Windows Desktop" $exported
    if ($LASTEXITCODE -ne 0) {
        [Console]::Error.WriteLine("Godot export-release failed with exit $LASTEXITCODE.")
        exit 1
    }
}

if (-not (Test-Path -LiteralPath $exported)) {
    [Console]::Error.WriteLine("Exported binary not found: $exported")
    exit 1
}

Write-Host "Using exported binary: $exported"
$env:TRAIN_CONTENT_EXPORT_SMOKE = "1"
$run = Invoke-HiddenProcess -FilePath $exported -ArgumentList @("--headless")
$processExit = $run.ExitCode
$output = $run.Output
Write-Host $output

$required = @(
    "CONTENT_EXPORT_IRON_ORE_LOADED",
    "CONTENT_EXPORT_PNG_DECODED",
    "CONTENT_EXPORT_INIT_OK",
    "CONTENT_EXPORT_JSON_READABLE",
    "CONTENT_EXPORT_PNG_READABLE",
    "CONTENT_EXPORT_JSONL_CREATED"
)
$missing = @($required | Where-Object { $output -notlike "*$_*" })
if ($processExit -ne 0) {
    $detail = "Exported process exited with code $processExit."
    if ($missing.Count -gt 0) {
        $detail += " Missing: $($missing -join ', ')."
    }
    [Console]::Error.WriteLine("Godot content export smoke failed. $detail")
    exit 1
}

if ($missing.Count -gt 0) {
    [Console]::Error.WriteLine("Godot content export smoke failed. Missing: $($missing -join ', ')")
    exit 1
}

Write-Host "Godot content export smoke passed."
exit 0
