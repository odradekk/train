#Requires -Version 7.0
# Decision note: 2026-10-26-performance-measurement-system
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateRange(1, 2147483)][int] $Seconds)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw '常驻显示脚本只支持 Windows。' }
. (Join-Path $PSScriptRoot 'measurement-host.ps1')
$awake = [MeasurementDisplayAwake]::new()
try {
    Write-Host "display acquired seconds=$Seconds"
    Start-Sleep -Seconds $Seconds
} finally {
    $awake.Dispose()
    Write-Host 'display released'
}
