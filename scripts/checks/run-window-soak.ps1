#Requires -Version 7.0
# 在测试机器（Linux runner lan-linux-nightly 所在主机）上导出 Release 构建并在真实 1080p 窗口中测量。
# 仓库公开：输出不写主机名、用户名、socket 路径和 Godot 安装路径。
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CacheDirectory,
    [string] $Root = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsLinux) { throw '窗口长测只在 Linux 测试机器上运行。' }
# 构建放在 runner 的磁盘临时目录而不是 /tmp：/tmp 可能是 tmpfs，会占用被测内存。
if (-not $env:RUNNER_TEMP) { throw '需要 GitHub Actions 的 RUNNER_TEMP。' }

$release = 'https://github.com/godotengine/godot/releases/download/4.7.2-stable'
$archiveName = 'Godot_v4.7.2-stable_mono_export_templates.tpz'
$work = Join-Path $env:RUNNER_TEMP "window-soak-$([guid]::NewGuid().ToString('N'))"
$results = Join-Path $Root 'TestResults/window-soak'
New-Item -ItemType Directory -Force -Path $work, $results, $CacheDirectory | Out-Null
try {
    # 缓存只省下载；每次都按同一发布的官方 SHA512-SUMS.txt 重新核对。
    $sums = Join-Path $work 'SHA512-SUMS.txt'
    Invoke-WebRequest "$release/SHA512-SUMS.txt" -OutFile $sums
    $expected = @(Get-Content -LiteralPath $sums | ForEach-Object { $fields = $_ -split '\s+'; if ($fields[-1] -eq $archiveName) { $fields[0] } })
    if ($expected.Count -ne 1) { throw "SHA512-SUMS.txt 中没有唯一的 $archiveName。" }
    $archive = Join-Path $CacheDirectory $archiveName
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        Invoke-WebRequest "$release/$archiveName" -OutFile "$archive.partial"
        Move-Item -LiteralPath "$archive.partial" -Destination $archive -Force
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $expected[0]) {
        Remove-Item -LiteralPath $archive -Force
        throw "$archiveName 与官方 SHA512 不符，已删除缓存。"
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $template = Join-Path $work 'linux_release.x86_64'
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try { [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('templates/linux_release.x86_64'), $template) }
    finally { $zip.Dispose() }
    [IO.File]::SetUnixFileMode($template, [IO.UnixFileMode]'UserRead, UserWrite, UserExecute')

    $build = Join-Path $work 'build'
    & pwsh -NoProfile -File (Join-Path $Root 'scripts/export-measurement.ps1') -SourceDirectory $Root -OutputDirectory $build -TemplatePath $template
    $exportExit = $LASTEXITCODE
    Get-ChildItem -LiteralPath $build -File | Where-Object { $_.Extension -in '.json', '.log' } |
        Copy-Item -Destination (New-Item -ItemType Directory -Force -Path (Join-Path $results 'export')).FullName
    if ($exportExit -ne 0) { throw "Release 导出失败，退出码 $exportExit。" }

    # 使用 runner 用户自己的桌面会话；X11 只选能连上的显示。
    $runtime = "/run/user/$(& id -u)"
    if (Test-Path -LiteralPath $runtime -PathType Container) {
        $env:XDG_RUNTIME_DIR = $runtime
        $wayland = @(Get-ChildItem -LiteralPath $runtime -Filter 'wayland-*' | Where-Object Name -Match '^wayland-\d+$' | Sort-Object Name)
        if ($wayland.Count -gt 0) { $env:WAYLAND_DISPLAY = $wayland[0].Name }
    }
    foreach ($socket in @(Get-ChildItem -LiteralPath '/tmp/.X11-unix' -Filter 'X*' -ErrorAction SilentlyContinue | Sort-Object Name)) {
        $display = ':' + $socket.Name.Substring(1)
        & xrandr --display $display --current *> $null
        if ($LASTEXITCODE -eq 0) { $env:DISPLAY = $display; break }
    }
    if (-not $env:DISPLAY -and -not $env:WAYLAND_DISPLAY) { throw '找不到可用的 X11 或 Wayland 显示。' }
    Write-Host "display: $(@($(if ($env:DISPLAY) { "x11 $env:DISPLAY" }), $(if ($env:WAYLAND_DISPLAY) { 'wayland' })) -join ', ')"

    $executable = Join-Path $build 'train.x86_64'
    $save = Join-Path $Root 'examples/standard-world/save.json'
    & pwsh -NoProfile -File (Join-Path $Root 'scripts/measure.ps1') -ExecutablePath $executable -InputPath $save `
        -OutputDirectory (Join-Path $results 'reference') -Scenario scene-reference -Repeat 1 -Machine test
    if ($LASTEXITCODE -ne 0) { throw "scene-reference 测量失败，退出码 $LASTEXITCODE。" }
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
