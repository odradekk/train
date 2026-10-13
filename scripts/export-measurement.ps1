#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'checks/Invoke-HiddenProcess.ps1')
. (Join-Path $PSScriptRoot 'checks/MeasurementSourceIdentity.ps1')

$repo = Split-Path $PSScriptRoot -Parent
$originalDirectory = Get-Location
$environment = @{}
foreach ($name in @('APPDATA', 'LOCALAPPDATA', 'MeasurementBuildIdentityPath')) {
    $environment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$templateDirectory = $null
$summaryPath = $null
$failure = $null
$steps = [System.Collections.Generic.List[object]]::new()

function Invoke-MeasurementBuildStep([string] $Name, [string] $FilePath, [string[]] $Arguments) {
    $result = Invoke-HiddenProcess -FilePath $FilePath -ArgumentList $Arguments -TimeoutSeconds 180
    $result.Output | Set-Content -LiteralPath (Join-Path $OutputDirectory "$Name-process.log") -Encoding utf8
    $steps.Add([ordered]@{
        name = $Name; exit_code = $result.ExitCode; timed_out = $result.TimedOut
        cleanup_succeeded = $result.CleanupSucceeded; duration_ms = $result.DurationMs
    })
    if ($result.ExitCode -ne 0 -or $result.TimedOut -or -not $result.CleanupSucceeded) {
        throw "$Name 失败（退出码 $($result.ExitCode)，超时 $($result.TimedOut)）。"
    }
}

try {
    if (-not $IsWindows) { throw '测量构建只支持 Windows Release 导出。' }
    if ([string]::IsNullOrWhiteSpace($env:GODOT) -or -not (Test-Path -LiteralPath $env:GODOT -PathType Leaf)) {
        throw 'GODOT 必须指向已安装的 Godot .NET 可执行文件。'
    }
    $OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    if (Test-Path -LiteralPath $OutputDirectory) {
        if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or
            @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
            throw '输出目录必须是新目录或空目录。'
        }
    }
    $repoPrefix = $repo.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($OutputDirectory.Equals($repo, [StringComparison]::OrdinalIgnoreCase)) { throw '不能将仓库根目录用作导出目录。' }
    if ($OutputDirectory.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        & git -C $repo check-ignore --quiet -- $OutputDirectory
        if ($LASTEXITCODE -ne 0) { throw '仓库内的导出目录必须被 Git 忽略，例如 TestResults/measurement-build。' }
    }
    [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
    $summaryPath = Join-Path $OutputDirectory 'export-summary.json'
    $sourceTemplate = Join-Path $environment.APPDATA 'Godot/export_templates/4.7.2.stable.mono/windows_release_x86_64.exe'
    if (-not (Test-Path -LiteralPath $sourceTemplate -PathType Leaf)) { throw "缺少已安装的 Windows Release 模板：$sourceTemplate" }
    $source = Get-MeasurementSourceIdentity $repo
    $sourceJson = $source | ConvertTo-Json -Depth 10
    $manifestPath = Join-Path $OutputDirectory 'build-source.json'
    $sourceJson | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $env:MeasurementBuildIdentityPath = $manifestPath
    $env:APPDATA = Join-Path $OutputDirectory 'appdata'
    $env:LOCALAPPDATA = Join-Path $OutputDirectory 'localappdata'
    [IO.Directory]::CreateDirectory($env:LOCALAPPDATA) | Out-Null
    $templateDirectory = Join-Path $env:APPDATA 'Godot/export_templates/4.7.2.stable.mono'
    [IO.Directory]::CreateDirectory($templateDirectory) | Out-Null
    Copy-Item -LiteralPath $sourceTemplate -Destination $templateDirectory
    Set-Location -LiteralPath $repo
    Invoke-MeasurementBuildStep 'build' 'dotnet' @('build', 'Train.csproj', '-c', 'ExportRelease')
    Invoke-MeasurementBuildStep 'import' $env:GODOT @('--headless', '--path', $repo, '--recovery-mode',
        '--log-file', (Join-Path $OutputDirectory 'import-godot.log'), '--editor', '--import')
    $binary = Join-Path $OutputDirectory 'train.exe'
    Invoke-MeasurementBuildStep 'export' $env:GODOT @('--headless', '--path', $repo,
        '--log-file', (Join-Path $OutputDirectory 'export-godot.log'), '--export-release', 'Windows Desktop', $binary)
    $afterJson = Get-MeasurementSourceIdentity $repo | ConvertTo-Json -Depth 10
    $afterJson | Set-Content -LiteralPath (Join-Path $OutputDirectory 'source-after.json') -Encoding utf8
    if ($sourceJson -cne $afterJson) { throw '导出期间源码身份发生变化；保留证据，但此构建不能用于测量。' }
    foreach ($path in @($binary, [IO.Path]::ChangeExtension($binary, '.pck'))) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少导出文件：$path" }
    }
    if (@(Get-ChildItem -LiteralPath $OutputDirectory -Filter Train.dll -File -Recurse).Count -ne 1) {
        throw '导出目录必须包含唯一的 Train.dll。'
    }
}
catch { $failure = $_.Exception.Message }
finally {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process') }
    Set-Location -LiteralPath $originalDirectory.Path
    if ($null -ne $templateDirectory) {
        try {
            $resolvedTemplate = [IO.Path]::GetFullPath($templateDirectory)
            $outputPrefix = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            if (-not $resolvedTemplate.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw '模板清理路径超出本次导出目录。'
            }
            if (Test-Path -LiteralPath $resolvedTemplate) { Remove-Item -LiteralPath $resolvedTemplate -Recurse -Force }
        }
        catch { $failure = $_.Exception.Message }
    }
    if ($null -ne $summaryPath) {
        [ordered]@{ succeeded = $null -eq $failure; error = $failure; steps = $steps.ToArray() } |
            ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    }
}
if ($null -ne $failure) { Write-Error $failure -ErrorAction Continue; exit 1 }
Write-Host "测量构建已保留：$binary"
exit 0
