#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $SourceDirectory = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    # 默认取已安装的 Release 模板；Linux 测试机器传入已核对的模板文件。
    [string] $TemplatePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../checks/Invoke-HiddenProcess.ps1')

$userDirectories = if ($IsWindows) { @{ APPDATA = 'appdata'; LOCALAPPDATA = 'localappdata' } }
else { @{ XDG_CONFIG_HOME = 'config'; XDG_DATA_HOME = 'data'; XDG_CACHE_HOME = 'cache' } }
$environment = @{}
foreach ($name in $userDirectories.Keys) {
    $environment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$templateDirectory = $null
$summaryPath = $null
$failure = $null
$steps = [System.Collections.Generic.List[object]]::new()

function Invoke-MeasurementBuildStep([string] $Name, [string] $FilePath, [string[]] $Arguments) {
    $result = Invoke-HiddenProcess -FilePath $FilePath -ArgumentList $Arguments -TimeoutSeconds 180 -WorkingDirectory $repo
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
    if (-not $IsWindows -and -not $IsLinux) { throw '测量构建只支持 Windows 与 Linux Release 导出。' }
    $repo = (Resolve-Path -LiteralPath $SourceDirectory).Path
    $commit = & git -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw '无法读取构建提交。' }
    $status = @(& git -C $repo status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw '测量导出需要干净工作树，请先提交修改。' }
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
    $templateName = if ($IsWindows) { 'windows_release_x86_64.exe' } else { 'linux_release.x86_64' }
    $sourceTemplate = if ($TemplatePath) { $TemplatePath }
    elseif ($IsWindows) { Join-Path $environment.APPDATA "Godot/export_templates/4.7.2.stable.mono/$templateName" }
    else {
        $data = if ($environment.XDG_DATA_HOME) { $environment.XDG_DATA_HOME } else { Join-Path $HOME '.local/share' }
        Join-Path $data "godot/export_templates/4.7.2.stable.mono/$templateName"
    }
    if (-not (Test-Path -LiteralPath $sourceTemplate -PathType Leaf)) { throw "缺少 Release 导出模板 $templateName。" }
    foreach ($name in $userDirectories.Keys) {
        $directory = Join-Path $OutputDirectory $userDirectories[$name]
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        [Environment]::SetEnvironmentVariable($name, $directory, 'Process')
    }
    $templateDirectory = if ($IsWindows) { Join-Path $env:APPDATA 'Godot/export_templates/4.7.2.stable.mono' }
    else { Join-Path $env:XDG_DATA_HOME 'godot/export_templates/4.7.2.stable.mono' }
    [IO.Directory]::CreateDirectory($templateDirectory) | Out-Null
    Copy-Item -LiteralPath $sourceTemplate -Destination (Join-Path $templateDirectory $templateName)
    Invoke-MeasurementBuildStep 'build' 'dotnet' @('build', 'Train.csproj', '-c', 'ExportRelease')
    Invoke-MeasurementBuildStep 'import' $env:GODOT @('--headless', '--path', $repo, '--recovery-mode',
        '--log-file', (Join-Path $OutputDirectory 'import-godot.log'), '--editor', '--import')
    $binary = Join-Path $OutputDirectory $(if ($IsWindows) { 'train.exe' } else { 'train.x86_64' })
    Invoke-MeasurementBuildStep 'export' $env:GODOT @('--headless', '--path', $repo,
        '--log-file', (Join-Path $OutputDirectory 'export-godot.log'), '--export-release', $(if ($IsWindows) { 'Windows Desktop' } else { 'Linux' }), $binary)
    Invoke-MeasurementBuildStep 'optimizations' (Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })) @('-NoProfile', '-File',
        (Join-Path $PSScriptRoot '../checks/check-export-optimizations.ps1'), '-ExportDirectory', $OutputDirectory)
    $afterCommit = & git -C $repo rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -ne $afterCommit) { throw '导出期间 HEAD 发生变化；此构建不能用于测量。' }
    $status = @(& git -C $repo status --porcelain)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw '导出结束后工作树不干净；此构建不能用于测量。' }
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
        [ordered]@{ succeeded = $null -eq $failure; error = $failure; source_directory = $repo; build_commit = $commit; steps = $steps.ToArray() } |
            ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    }
}
if ($null -ne $failure) { Write-Error $failure -ErrorAction Continue; exit 1 }
Write-Host "测量构建已保留：$binary"
exit 0
