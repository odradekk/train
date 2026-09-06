# Repeatable Windows export smoke for issue #20 missing/empty mods-root isolation.
#
# Commands (from the repository root, with Godot 4.7 .NET on PATH or $env:GODOT):
#   & $godot --headless --path . --export-release "Windows Desktop" builds/mods-smoke/train.exe
#   $env:TRAIN_MODS_EXPORT_SMOKE = "1"
#   $env:TRAIN_MODS_SMOKE_USER_DATA = "<isolated empty user-data>"
#   .\builds\mods-smoke\train.exe --user-data-dir "<isolated empty user-data>"
#
# Requires the exported process stdout to contain:
#   MODS_EXPORT_IRON_ORE_LOADED
#   MODS_EXPORT_INIT_OK
# and one of:
#   MODS_EXPORT_ROOT_EMPTY
#   MODS_EXPORT_ROOT_UNAVAILABLE
# MODS_EXPORT_ROOT_PRESENT does not satisfy the missing/empty contract.
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

    if ($env:TRAIN_MODS_EXPORT_SMOKE_NO_SEARCH -eq "1") {
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

$exported = $env:TRAIN_MODS_EXPORT_BIN
$godot = Find-Godot
$exportCommand = '& <godot> --headless --path . --export-release "Windows Desktop" builds/mods-smoke/train.exe'
$runCommand = '$env:TRAIN_MODS_EXPORT_SMOKE="1"; $env:TRAIN_MODS_SMOKE_USER_DATA="<isolated-user-data>"; .\builds\mods-smoke\train.exe --user-data-dir "<isolated-user-data>"'
if (-not $exported -and -not $godot) {
    [Console]::Error.WriteLine("Godot executable not found (PATH, GODOT, GODOT_HOME, or standard Program Files).")
    [Console]::Error.WriteLine("Unrun export command: $exportCommand")
    [Console]::Error.WriteLine("Unrun exported-process command: $runCommand")
    exit 2
}

if (-not $exported) {
    $exported = Join-Path $root "builds/mods-smoke/train.exe"
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
$isolated = Join-Path ([System.IO.Path]::GetTempPath()) ("train-mods-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $isolated | Out-Null
$previousSmokeRoot = $env:TRAIN_MODS_SMOKE_USER_DATA
$env:TRAIN_MODS_EXPORT_SMOKE = "1"
$env:TRAIN_MODS_SMOKE_USER_DATA = $isolated
$processExit = -1
$output = ""
try {
    $run = Invoke-HiddenProcess -FilePath $exported -ArgumentList @("--headless", "--user-data-dir", $isolated)
    $processExit = $run.ExitCode
    $output = $run.Output
    Write-Host $output
}
finally {
    if ($null -eq $previousSmokeRoot) {
        Remove-Item Env:TRAIN_MODS_SMOKE_USER_DATA -ErrorAction SilentlyContinue
    }
    else {
        $env:TRAIN_MODS_SMOKE_USER_DATA = $previousSmokeRoot
    }
    Remove-Item -LiteralPath $isolated -Recurse -Force -ErrorAction SilentlyContinue
}

$required = @(
    "MODS_EXPORT_IRON_ORE_LOADED",
    "MODS_EXPORT_INIT_OK"
)
$missing = @($required | Where-Object { $output -notlike "*$_*" })
$hasEmpty = $output -like "*MODS_EXPORT_ROOT_EMPTY*"
$hasUnavailable = $output -like "*MODS_EXPORT_ROOT_UNAVAILABLE*"
$hasPresent = $output -like "*MODS_EXPORT_ROOT_PRESENT*"
if (-not ($hasEmpty -or $hasUnavailable)) {
    $missing += "MODS_EXPORT_ROOT_EMPTY|MODS_EXPORT_ROOT_UNAVAILABLE"
}
if ($hasPresent -and -not ($hasEmpty -or $hasUnavailable)) {
    $missing += "missing/empty mods root (ROOT_PRESENT is not accepted)"
}

if ($processExit -ne 0) {
    $detail = "Exported process exited with code $processExit."
    if ($missing.Count -gt 0) {
        $detail += " Missing: $($missing -join ', ')."
    }
    [Console]::Error.WriteLine("Godot mods export smoke failed. $detail")
    exit 1
}

if ($missing.Count -gt 0) {
    [Console]::Error.WriteLine("Godot mods export smoke failed. Missing: $($missing -join ', ')")
    exit 1
}

Write-Host "Godot mods export smoke passed."
exit 0
