#Requires -Version 7.0
# 在测试机器（Linux runner lan-linux-nightly 所在主机）上导出 Release 构建并在真实 1080p 窗口中测量。
# 仓库公开：输出不写主机名、用户名、socket 路径和 Godot 安装路径。
# Decision note: 2026-10-26-performance-measurement-system
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CacheDirectory,
    [Parameter(Mandatory)][string] $Scenario,
    [ValidateSet('example', 'scene-scale')][string] $InputSet = 'example',
    [string] $InputPath = 'examples/standard-world/save.json',
    [ValidateRange(1, 2147483647)][int] $Repeat = 3,
    [ValidateRange(0, 2146878)][int] $DurationSeconds = 0,
    [switch] $NodeReadyHold,
    [switch] $StreamingDiagnostics,
    [string] $Root = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$release = 'https://github.com/godotengine/godot/releases/download/4.7.2-stable'
$archiveName = 'Godot_v4.7.2-stable_mono_export_templates.tpz'
$work = $null
$results = $null
$stage = 'input'
$failure = $false
$inputs = @()
try {
    $Root = (Resolve-Path -LiteralPath $Root).Path
    if ($InputSet -eq 'example') {
        $examples = Join-Path $Root 'examples'
        $InputPath = [IO.Path]::GetFullPath($InputPath, $Root)
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (-not $InputPath.StartsWith($examples + [IO.Path]::DirectorySeparatorChar, $comparison) -or
            -not (Test-Path -LiteralPath $InputPath -PathType Leaf)) { throw [ArgumentException]::new('存档必须是 examples/ 下已存在的文件。') }
    }
    $stage = 'platform'
    if (-not $IsLinux) { throw '测量运行器只在 Linux 测试机器上运行。' }
    # /tmp 可能是 tmpfs，会占用被测内存；构建放在 runner 磁盘临时目录。
    if (-not $env:RUNNER_TEMP) { throw '需要 GitHub Actions 的 RUNNER_TEMP。' }
    $work = Join-Path $env:RUNNER_TEMP "measure-$([guid]::NewGuid().ToString('N'))"
    $commit = (& git -C $Root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw '无法读取被测提交。' }
    $results = Join-Path $Root "TestResults/measure-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff'))-$($commit.Substring(0, 7))"
    New-Item -ItemType Directory -Force -Path $work, $results, $CacheDirectory | Out-Null
    $scaleDirectory = Join-Path $work 'scene-scale'
    $inputs = if ($InputSet -eq 'scene-scale') {
        # 30× 和 100× 打开节点所需内存超过测试机，只生成和统计，不在游戏里打开。
        @(1, 10 | ForEach-Object {
            @{ name = "${_}x"; path = (Join-Path $scaleDirectory "saves/${_}x.save.json")
                directory = (Join-Path $results "scale-${_}x"); stage = 'not_started'; succeeded = $false }
        })
    } else {
        @(@{ name = 'example'; path = $InputPath; directory = $results; stage = 'not_started'; succeeded = $false })
    }
    $stage = 'template'
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
    $stage = 'export'
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'export-measurement.ps1') -SourceDirectory $Root -OutputDirectory $build -TemplatePath $template `
        *> (Join-Path $results 'export-process.log')
    $exportExit = $LASTEXITCODE
    Get-ChildItem -LiteralPath $build -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.json', '.log' } |
        Copy-Item -Destination (New-Item -ItemType Directory -Force -Path (Join-Path $results 'export')).FullName
    if ($exportExit -ne 0) { throw "Release 导出失败，退出码 $exportExit。" }

    if ($InputSet -eq 'scene-scale') {
        $stage = 'scale-inputs'
        & python3 (Join-Path $Root 'scripts/measure/scene-scale.py') generate --root $Root --output $scaleDirectory --configuration Release `
            *> (Join-Path $results 'scale-inputs-process.log')
        $inputExit = $LASTEXITCODE
        $preservedInputs = (New-Item -ItemType Directory -Force -Path (Join-Path $results 'scale-inputs')).FullName
        Get-ChildItem -LiteralPath $scaleDirectory -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.json', '.log' } |
            Copy-Item -Destination $preservedInputs
        $saves = Join-Path $scaleDirectory 'saves'
        if (Test-Path -LiteralPath $saves -PathType Container) { Copy-Item -LiteralPath $saves -Destination $preservedInputs -Recurse }
        if ($inputExit -ne 0) { throw "规模点输入生成失败，退出码 $inputExit。" }
        foreach ($measurementInput in $inputs) {
            if (-not (Test-Path -LiteralPath $measurementInput.path -PathType Leaf)) { throw "规模点 $($measurementInput.name) 存档缺失。" }
        }
    }

    # 使用 runner 用户自己的桌面会话；X11 只选能连上的显示。
    $stage = 'display'
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
    foreach ($measurementInput in $inputs) {
        New-Item -ItemType Directory -Force -Path $measurementInput.directory | Out-Null
        try {
            $stage = $measurementInput.stage = 'reference'
            & (Join-Path $PSScriptRoot 'measure.ps1') -RepositoryPath $Root -ExecutablePath $executable -InputPath $measurementInput.path `
                -OutputDirectory (Join-Path $measurementInput.directory 'reference') -Scenario scene-reference -Repeat 1 -Machine test -NodeReadyHold:$NodeReadyHold `
                *> (Join-Path $measurementInput.directory 'reference-process.log')
            if ($LASTEXITCODE -ne 0) { throw "scene-reference 测量失败，退出码 $LASTEXITCODE。" }
            $stage = $measurementInput.stage = 'scenarios'
            & (Join-Path $PSScriptRoot 'measure.ps1') -RepositoryPath $Root -ExecutablePath $executable -InputPath $measurementInput.path `
                -OutputDirectory (Join-Path $measurementInput.directory 'scenarios') -Scenario ($Scenario -split ',' | ForEach-Object { $_.Trim() }) -Repeat $Repeat -Machine test -DurationSeconds $DurationSeconds -NodeReadyHold:$NodeReadyHold `
                -ReferencePath (Join-Path $measurementInput.directory 'reference/scene-reference-1/scene-reference.json') -StreamingDiagnostics:$StreamingDiagnostics `
                *> (Join-Path $measurementInput.directory 'scenarios-process.log')
            if ($LASTEXITCODE -ne 0) { throw "场景列表测量失败，退出码 $LASTEXITCODE。" }
            $measurementInput.succeeded = $true
            $measurementInput.stage = 'completed'
        }
        catch {
            $failure = $true
            $_.ToString() | Set-Content -LiteralPath (Join-Path $measurementInput.directory 'failure.log')
            Write-Host "输入 $($measurementInput.name) 失败，阶段：$($measurementInput.stage)。"
        }
    }
}
catch {
    $failure = $true
    if ($results) { $_.ToString() | Set-Content -LiteralPath (Join-Path $results 'failure.log') }
    if ($_.Exception -is [ArgumentException]) { Write-Host $_.Exception.Message }
    Write-Host "测量失败，阶段：$stage。"
}
finally {
    if ($work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
    if ($results) {
        @{ succeeded = -not $failure; stage = $stage; input_set = $InputSet
            scenarios = $Scenario; repeat = $Repeat; duration_seconds = $DurationSeconds
            scales = @($inputs | Where-Object name -NE 'example' | ForEach-Object {
                @{ scale = $_.name; stage = $_.stage; succeeded = $_.succeeded }
            }) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'summary.json')
    }
}
if ($failure) { exit 1 }
exit 0
