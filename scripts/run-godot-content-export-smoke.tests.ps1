# Exercises scripts/run-godot-content-export-smoke.ps1 with controlled fake programs.

$ErrorActionPreference = "Stop"
$smoke = Join-Path $PSScriptRoot "run-godot-content-export-smoke.ps1"
. (Join-Path $PSScriptRoot "Invoke-HiddenProcess.ps1")
$failed = 0

function Invoke-Smoke {
    param(
        [string] $GodotPath,
        [string] $ExportBin,
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
        $process = Start-Process -FilePath "powershell.exe" -ArgumentList @("-NoProfile", "-File", $smoke) -Wait -PassThru -NoNewWindow
        return [int] $process.ExitCode
    }
    finally {
        if ($null -eq $previousGodot) { Remove-Item Env:GODOT -ErrorAction SilentlyContinue } else { $env:GODOT = $previousGodot }
        if ($null -eq $previousBin) { Remove-Item Env:TRAIN_CONTENT_EXPORT_BIN -ErrorAction SilentlyContinue } else { $env:TRAIN_CONTENT_EXPORT_BIN = $previousBin }
        if ($null -eq $previousNoSearch) { Remove-Item Env:TRAIN_CONTENT_EXPORT_SMOKE_NO_SEARCH -ErrorAction SilentlyContinue } else { $env:TRAIN_CONTENT_EXPORT_SMOKE_NO_SEARCH = $previousNoSearch }
    }
}

function New-FakeProgram {
    param([int] $ExitCode, [string[]] $Lines)
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("train-fake-export-" + [guid]::NewGuid().ToString("N") + ".cmd")
    $body = @(
        "@echo off"
    ) + ($Lines | ForEach-Object { "echo $_" }) + @(
        "exit /b $ExitCode"
    )
    Set-Content -LiteralPath $path -Value $body -Encoding Ascii
    return $path
}

function New-FakeWinExe {
    param([int] $ExitCode, [string[]] $Lines)
    $id = [guid]::NewGuid().ToString("N")
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("train-fake-winexe-" + $id + ".exe")
    $prints = @($Lines | ForEach-Object {
        $escaped = $_.Replace("\", "\\").Replace('"', '\"')
        "        System.Console.WriteLine(`"$escaped`");"
    }) -join "`r`n"
    $code = @"
public static class TrainFakeWinExe$id {
    public static int Main() {
$prints
        return $ExitCode;
    }
}
"@
    Add-Type -TypeDefinition $code -OutputType WindowsApplication -OutputAssembly $path -ReferencedAssemblies System
    return $path
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

$allMarkers = @(
    "CONTENT_EXPORT_IRON_ORE_LOADED",
    "CONTENT_EXPORT_PNG_DECODED",
    "CONTENT_EXPORT_INIT_OK",
    "CONTENT_EXPORT_JSON_READABLE",
    "CONTENT_EXPORT_PNG_READABLE",
    "CONTENT_EXPORT_JSONL_CREATED"
)

$helperWinExe = New-FakeWinExe -ExitCode 3 -Lines @("HIDDEN_PROCESS_STDOUT_MARKER")
$helperRun = Invoke-HiddenProcess -FilePath $helperWinExe
if ($helperRun.ExitCode -ne 3 -or $helperRun.Output -notlike "*HIDDEN_PROCESS_STDOUT_MARKER*") {
    Write-Host "FAIL helper captures Windows GUI exe stdout and exit : exit=$($helperRun.ExitCode)"
    $failed++
}
else {
    Write-Host "ok helper captures Windows GUI exe stdout and exit"
}
Remove-Item -LiteralPath $helperWinExe -ErrorAction SilentlyContinue

$failingExport = New-FakeProgram -ExitCode 7 -Lines @("export failed")
$passingBin = New-FakeProgram -ExitCode 0 -Lines $allMarkers
$missingBin = New-FakeProgram -ExitCode 0 -Lines @("CONTENT_EXPORT_IRON_ORE_LOADED")
$passingWinExe = New-FakeWinExe -ExitCode 0 -Lines $allMarkers
$silentWinExe = New-FakeWinExe -ExitCode 0 -Lines @()
$nonzeroWinExe = New-FakeWinExe -ExitCode 7 -Lines $allMarkers
try {
    Assert-SmokeExit -Name "unavailable Godot" -Expected 2 -Actual (Invoke-Smoke -ForceUnavailable)
    Assert-SmokeExit -Name "failed export" -Expected 1 -Actual (Invoke-Smoke -GodotPath $failingExport)
    Assert-SmokeExit -Name "exported binary with all markers" -Expected 0 -Actual (Invoke-Smoke -ExportBin $passingBin)
    Assert-SmokeExit -Name "exported binary missing a marker" -Expected 1 -Actual (Invoke-Smoke -ExportBin $missingBin)
    Assert-SmokeExit -Name "Windows GUI exe with all markers" -Expected 0 -Actual (Invoke-Smoke -ExportBin $passingWinExe)
    Assert-SmokeExit -Name "Windows GUI exe exit 0 without markers" -Expected 1 -Actual (Invoke-Smoke -ExportBin $silentWinExe)
    Assert-SmokeExit -Name "Windows GUI exe nonzero exit" -Expected 1 -Actual (Invoke-Smoke -ExportBin $nonzeroWinExe)
}
finally {
    Remove-Item -LiteralPath $failingExport, $passingBin, $missingBin, $passingWinExe, $silentWinExe, $nonzeroWinExe -ErrorAction SilentlyContinue
}

if ($failed -gt 0) {
    exit 1
}
exit 0
