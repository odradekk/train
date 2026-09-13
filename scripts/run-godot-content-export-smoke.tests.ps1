# Exercises scripts/run-godot-content-export-smoke.ps1 with controlled fake programs.

$ErrorActionPreference = "Stop"
$smoke = Join-Path $PSScriptRoot "run-godot-content-export-smoke.ps1"
. (Join-Path $PSScriptRoot "Invoke-HiddenProcess.ps1")
. (Join-Path $PSScriptRoot "SmokeTests.Helpers.ps1")
$pwsh = Get-TestPowerShellPath
$failed = 0

function Invoke-Smoke {
    param(
        [string] $GodotPath,
        [string] $ExportBin,
        [string] $OutputPath,
        [switch] $ExportOnly,
        [switch] $ForceUnavailable
    )
    $previousGodot = $env:GODOT
    $previousBin = $env:TRAIN_CONTENT_EXPORT_BIN
    $previousNoSearch = $env:TRAIN_CONTENT_EXPORT_SMOKE_NO_SEARCH
    try {
        if ($ForceUnavailable) {
            Remove-Item Env:GODOT -ErrorAction SilentlyContinue
            Remove-Item Env:TRAIN_CONTENT_EXPORT_BIN -ErrorAction SilentlyContinue
            $env:TRAIN_CONTENT_EXPORT_SMOKE_NO_SEARCH = "1"
        }
        else {
            if ($GodotPath) { $env:GODOT = $GodotPath } else { Remove-Item Env:GODOT -ErrorAction SilentlyContinue }
            if ($ExportBin) { $env:TRAIN_CONTENT_EXPORT_BIN = $ExportBin } else { Remove-Item Env:TRAIN_CONTENT_EXPORT_BIN -ErrorAction SilentlyContinue }
            Remove-Item Env:TRAIN_CONTENT_EXPORT_SMOKE_NO_SEARCH -ErrorAction SilentlyContinue
        }
        $arguments = @("-NoProfile", "-File", $smoke)
        if ($OutputPath) { $arguments += @("-OutputPath", $OutputPath) }
        if ($ExportOnly) { $arguments += "-ExportOnly" }
        & $pwsh @arguments 2>&1 | Out-Host
        return [int] $LASTEXITCODE
    }
    finally {
        if ($null -eq $previousGodot) { Remove-Item Env:GODOT -ErrorAction SilentlyContinue } else { $env:GODOT = $previousGodot }
        if ($null -eq $previousBin) { Remove-Item Env:TRAIN_CONTENT_EXPORT_BIN -ErrorAction SilentlyContinue } else { $env:TRAIN_CONTENT_EXPORT_BIN = $previousBin }
        if ($null -eq $previousNoSearch) { Remove-Item Env:TRAIN_CONTENT_EXPORT_SMOKE_NO_SEARCH -ErrorAction SilentlyContinue } else { $env:TRAIN_CONTENT_EXPORT_SMOKE_NO_SEARCH = $previousNoSearch }
    }
}

function New-FakeProgram {
    param([int] $ExitCode, [string[]] $Lines)
    return New-FakeNativeProgram -Name "train-fake-export" -ExitCode $ExitCode -Lines $Lines
}

function Assert-SmokeExit {
    param([string] $Name, [int] $Expected, [int] $Actual)
    if ($Actual -ne $Expected) {
        Write-Host "FAIL $Name : expected exit $Expected, got $Actual"
        $script:failed++
        return
    }
    Write-Host "ok $Name"
}

function Assert-SmokeCondition {
    param([string] $Name, [bool] $Condition)
    if (-not $Condition) {
        Write-Host "FAIL $Name"
        $script:failed++
        return
    }
    Write-Host "ok $Name"
}

$allMarkers = @(
    "CONTENT_EXPORT_IRON_ORE_LOADED",
    "CONTENT_EXPORT_PNG_DECODED",
    "CONTENT_EXPORT_INIT_OK",
    "CONTENT_EXPORT_JSON_READABLE",
    "CONTENT_EXPORT_PNG_READABLE",
    "CONTENT_EXPORT_JSONL_CREATED"
)

$helperWinExe = $null
if ($IsWindows) {
    try {
        $helperWinExe = New-FakeWindowsGuiExe -Name "train-fake-helper-winexe" -ExitCode 3 -Lines @("HIDDEN_PROCESS_STDOUT_MARKER")
        $helperRun = Invoke-HiddenProcess -FilePath $helperWinExe.FilePath
        if ($helperRun.ExitCode -ne 3 -or $helperRun.Output -notlike "*HIDDEN_PROCESS_STDOUT_MARKER*") {
            Write-Host "FAIL helper captures Windows GUI exe stdout and exit : exit=$($helperRun.ExitCode)"
            $failed++
        }
        else {
            Write-Host "ok helper captures Windows GUI exe stdout and exit"
        }
    }
    finally {
        if ($helperWinExe) {
            Remove-Item -LiteralPath $helperWinExe.Root -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
else {
    Write-Host "SKIP helper captures Windows GUI exe stdout and exit (Windows only)"
}

$passingBin = New-FakeProgram -ExitCode 0 -Lines $allMarkers
$missingBin = New-FakeProgram -ExitCode 0 -Lines @("CONTENT_EXPORT_IRON_ORE_LOADED")
$exportRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("train content export " + [guid]::NewGuid().ToString("N"))
$outputPath = Join-Path $exportRoot "output with spaces/train.exe"
$invocationPath = Join-Path $exportRoot "godot-arguments.txt"
New-Item -ItemType Directory -Path $exportRoot | Out-Null
$successfulExporter = New-FakeGodotExporter -Name "train-fake-content-godot" -InvocationPath $invocationPath -ExitCode 0
$failingExporter = New-FakeGodotExporter -Name "train-failing-content-godot" -InvocationPath $invocationPath -ExitCode 7
$passingWinExe = $null
$silentWinExe = $null
$nonzeroWinExe = $null
$windowsNamedBins = @()
try {
    if ($IsWindows) {
        $passingWinExe = New-FakeWindowsGuiExe -Name "train-passing-content-winexe" -ExitCode 0 -Lines $allMarkers
        $silentWinExe = New-FakeWindowsGuiExe -Name "train-silent-content-winexe" -ExitCode 0 -Lines @()
        $nonzeroWinExe = New-FakeWindowsGuiExe -Name "train-nonzero-content-winexe" -ExitCode 7 -Lines $allMarkers
    }
    Assert-SmokeExit -Name "unavailable Godot with fresh OutputPath" -Expected 2 -Actual (Invoke-Smoke -OutputPath $outputPath -ForceUnavailable)
    Assert-SmokeCondition -Name "unavailable Godot does not create OutputPath" -Condition (-not (Test-Path -LiteralPath $outputPath))
    Assert-SmokeExit -Name "failed export with fresh OutputPath" -Expected 1 -Actual (Invoke-Smoke -GodotPath $failingExporter -OutputPath $outputPath)
    Assert-SmokeCondition -Name "failed export does not create OutputPath" -Condition (-not (Test-Path -LiteralPath $outputPath))
    Assert-SmokeExit -Name "exported binary with all markers" -Expected 0 -Actual (Invoke-Smoke -ExportBin $passingBin)
    Assert-SmokeExit -Name "exported binary missing a marker" -Expected 1 -Actual (Invoke-Smoke -ExportBin $missingBin)
    Assert-SmokeExit -Name "ExportOnly rejects a supplied environment binary" -Expected 2 -Actual (Invoke-Smoke -ExportBin $passingBin -ExportOnly)
    if (-not $IsWindows) {
        foreach ($extension in @(".exe", ".cmd", ".bat")) {
            $windowsNamedBin = New-FakeNativeProgram -Name "train-content-windows-named" -ExitCode 0 -Lines $allMarkers -Extension $extension
            $windowsNamedBins += $windowsNamedBin
            Assert-SmokeExit -Name "non-Windows rejects supplied $extension binary" -Expected 2 -Actual (Invoke-Smoke -ExportBin $windowsNamedBin)
        }
    }
    Remove-Item -LiteralPath $invocationPath -ErrorAction SilentlyContinue
    Assert-SmokeExit -Name "OutputPath exports to a new path with spaces" -Expected 0 -Actual (Invoke-Smoke -GodotPath $successfulExporter -ExportBin $missingBin -OutputPath $outputPath -ExportOnly)
    $exportArguments = @(Get-Content -LiteralPath $invocationPath -ErrorAction SilentlyContinue)
    Assert-SmokeCondition -Name "OutputPath invokes Godot export-release" -Condition ($exportArguments -contains "--export-release")
    Assert-SmokeCondition -Name "OutputPath is passed intact to Godot" -Condition ($exportArguments -contains $outputPath)
    Assert-SmokeCondition -Name "Godot creates the requested OutputPath" -Condition (Test-Path -LiteralPath $outputPath)
    if ($IsWindows) {
        Assert-SmokeExit -Name "Windows GUI exe with all markers" -Expected 0 -Actual (Invoke-Smoke -ExportBin $passingWinExe.FilePath)
        Assert-SmokeExit -Name "Windows GUI exe exit 0 without markers" -Expected 1 -Actual (Invoke-Smoke -ExportBin $silentWinExe.FilePath)
        Assert-SmokeExit -Name "Windows GUI exe nonzero exit" -Expected 1 -Actual (Invoke-Smoke -ExportBin $nonzeroWinExe.FilePath)
    }
    else {
        Write-Host "SKIP Windows GUI exe smoke cases (Windows only)"
    }
}
finally {
    @($passingBin, $missingBin, $successfulExporter, $failingExporter) + $windowsNamedBins |
        Where-Object { $_ } |
        Remove-Item -ErrorAction SilentlyContinue
    @($passingWinExe, $silentWinExe, $nonzeroWinExe) |
        Where-Object { $_ } |
        ForEach-Object { Remove-Item -LiteralPath $_.Root -Recurse -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $exportRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failed -gt 0) {
    exit 1
}
exit 0
