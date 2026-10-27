#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ExecutablePath,
    [Parameter(Mandatory)][string] $InputPath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateSet('map-1x', 'map-8x', 'follow-1x', 'follow-8x', 'node-1x', 'node-8x',
        'sdfgi-off', 'sdfgi-on', 'taa-off', 'taa-on', 'scene-node', 'scene-node-first',
        'scene-edge', 'scene-follow', 'scene-follow-8x', 'scene-memory', 'scene-soak', 'scene-capture', 'scene-map', 'scene-reference', 'scene-editor',
        'scene-route-1x', 'scene-route-2x', 'scene-route-4x', 'scene-route-8x', 'scene-route-capture-1x', 'scene-route-capture-8x')]
    [string[]] $Scenario = @('map-1x', 'map-8x', 'follow-1x', 'follow-8x', 'node-1x', 'node-8x',
        'sdfgi-off', 'sdfgi-on', 'taa-off', 'taa-on'),
    [ValidateRange(1, 2147483647)][int] $Repeat = 3,
    [switch] $StreamingDiagnostics,
    [ValidateSet('default', 'farthest', 'default-low-orbit', 'farthest-low-orbit')][string[]] $RouteCamera = @('default'),
    # 启动器上限减去 scene-memory 的 600 秒退出余量。
    [ValidateRange(0, 2146878)][int] $DurationSeconds = 0,
    [switch] $NodeReadyHold,
    [string] $ReferencePath,
    # scene-* 默认列车 1、节点 2。
    [ValidateRange(1, 9223372036854775807)][long] $TrainId,
    [ValidateRange(1, 9223372036854775807)][long] $NodeId,
    # test 指用户 2026-10-22 指定的 Linux runner 主机。
    [ValidateSet('development', 'test')][string] $Machine = 'development',
    [string] $RepositoryPath = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../checks/Invoke-HiddenProcess.ps1')
. (Join-Path $PSScriptRoot 'measurement-host.ps1')

function Get-FileIdentity([string] $Path) {
    @{ path = $Path; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Write-Json([string] $Path, $Value) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8
}

$runs = [System.Collections.Generic.List[object]]::new()
$summaryPath = $null
$failure = $null
$displayAwake = $null
$displayAwakeState = @{ acquired = $false; restored = $false }
$idleInhibit = $false
$wrapper = $null
try {
    $lowOrbit = @($RouteCamera | Where-Object { $_ -like '*-low-orbit' }).Count -gt 0
    if ($PSBoundParameters.ContainsKey('RouteCamera') -and -not $StreamingDiagnostics -and
        (@($RouteCamera | Where-Object { $_ -notlike '*-low-orbit' }).Count -gt 0 -or
         @($Scenario | Where-Object { $_ -notin @('scene-node', 'scene-edge') }).Count -gt 0)) {
        throw '-RouteCamera 需要 -StreamingDiagnostics；scene-node/scene-edge 的低俯仰环绕除外。'
    }
    if ($NodeReadyHold -and @($Scenario | Where-Object { $_ -notin @('scene-node-first', 'scene-reference') }).Count -gt 0) {
        throw '-NodeReadyHold 只用于 scene-node-first 和 scene-reference。'
    }
    if ($StreamingDiagnostics -and @($Scenario | Where-Object { $_ -notin @('scene-route-1x', 'scene-route-2x', 'scene-route-4x', 'scene-route-8x') }).Count -gt 0) {
        throw '-StreamingDiagnostics 仅支持 scene-route-1x/2x/4x/8x。'
    }
    if (-not $IsWindows -and -not $IsLinux) { throw '测量入口需要 Windows 或 Linux Release 导出与真实窗口。' }
    if ($IsWindows) {
        $displayAwake = [MeasurementDisplayAwake]::new()
        $displayAwakeState.acquired = $true
    }
    else {
        # 合成器是否遵守 logind 的空闲抑制取决于主机；这里只记录能否取得抑制。
        try {
            $probe = Invoke-HiddenProcess -FilePath 'systemd-inhibit' -TimeoutSeconds 30 -ArgumentList @(
                '--what=idle:sleep', '--who=train-measure', '--why=window measurement', '--mode=block', 'true')
            $idleInhibit = $probe.ExitCode -eq 0 -and -not $probe.TimedOut
        }
        catch { $idleInhibit = $false }
        # 测试机器的合成器会改写窗口尺寸；gamescope 给游戏固定的 1920×1080 输出（用户 2026-10-22 决定）。
        if (-not (Get-Command gamescope -ErrorAction SilentlyContinue)) {
            throw 'Linux 测量需要 gamescope 提供固定的 1920×1080 输出，主机上找不到 gamescope。'
        }
        if (-not (Get-Command python3 -ErrorAction SilentlyContinue)) {
            throw 'Linux 测量需要 python3 记录游戏退出状态，主机上找不到 python3。'
        }
        $wrapper = @{ program = 'gamescope'; version = ((& gamescope --version 2>&1) | Select-Object -First 1) -as [string]
            arguments = @('-w', '1920', '-h', '1080', '-W', '1920', '-H', '1080', '--') }
    }
    if ($Scenario.Count -eq 0) { throw '至少选择一个情景。' }
    $ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
    $InputPath = (Resolve-Path -LiteralPath $InputPath).Path
    if ($ReferencePath) { $ReferencePath = (Resolve-Path -LiteralPath $ReferencePath).Path }
    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $InputPath -PathType Leaf)) { throw '可执行文件与输入存档必须是文件。' }
    $OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
    if (Test-Path -LiteralPath $OutputDirectory) {
        if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container) -or
            @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
            throw '输出目录必须是新目录或空目录。'
        }
    }
    else { [System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null }
    $summaryPath = Join-Path $OutputDirectory 'summary.json'

    $exportDirectory = Split-Path $ExecutablePath -Parent
    $pck = [System.IO.Path]::ChangeExtension($ExecutablePath, '.pck')
    if (-not (Test-Path -LiteralPath $pck -PathType Leaf)) { throw "缺少旁置 PCK：$pck" }
    $assemblies = @(Get-ChildItem -LiteralPath $exportDirectory -Filter Train.dll -File -Recurse)
    if ($assemblies.Count -ne 1) { throw '导出目录必须包含唯一的 Train.dll。' }

    # 子进程避免复测不同构建时的程序集冲突与 DLL 锁定。
    $build = & (Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })) -NoProfile -Command {
        param($path)
        $ErrorActionPreference = 'Stop'
        [Reflection.Assembly]::LoadFrom($path).GetCustomAttributes(
            [Reflection.AssemblyInformationalVersionAttribute], $false)[0].InformationalVersion
    } -args $assemblies[0].FullName
    if ($LASTEXITCODE -ne 0) { throw '无法读取 Train.dll 构建版本。' }
    if ($build -notmatch '\+([0-9a-f]{40})$') { throw "程序集版本未包含完整 Git 提交：$build" }
    $commit = $Matches[1]
    $apiBlob = & git -C $RepositoryPath rev-parse "${commit}:docs/contracts/public-api.txt"
    if ($LASTEXITCODE -ne 0) { throw '无法读取构建提交的 public-api Git blob。' }
    $identity = @{
        build_commit = $commit
        public_api_git_blob = $apiBlob
        executable = Get-FileIdentity $ExecutablePath
        pck = Get-FileIdentity $pck
        assembly = Get-FileIdentity $assemblies[0].FullName
        input = Get-FileIdentity $InputPath
        machine = $Machine
        machine_basis = $(if ($Machine -eq 'test') { '用户 2026-10-22 决定 Demo 阶段测试机器为 Linux runner lan-linux-nightly 所在主机：https://github.com/odradekk/train/issues/397#issuecomment-6336919759' } else { '开发机，不是测试机器' })
        system = if ($IsWindows) {
            @{
                os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
                processors = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
                physical_memory_bytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
                display_adapters = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, PNPDeviceID)
            }
        }
        else {
            @{
                os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
                processors = @(@{ Name = (Select-String -LiteralPath /proc/cpuinfo -Pattern '^model name\s*:\s*(.+)$' |
                    Select-Object -First 1).Matches[0].Groups[1].Value; NumberOfLogicalProcessors = [Environment]::ProcessorCount })
                physical_memory_bytes = [long](Select-String -LiteralPath /proc/meminfo -Pattern '^MemTotal:\s+(\d+) kB$').Matches[0].Groups[1].Value * 1024
                # 只记录显示类型和 X 显示编号，不记录 socket 路径。
                display_environment = @(@($(if ($env:DISPLAY) { "x11 $env:DISPLAY" }), $(if ($env:WAYLAND_DISPLAY) { 'wayland' })) | Where-Object { $_ })
                ac_online = @(Get-ChildItem /sys/class/power_supply -ErrorAction SilentlyContinue |
                    Where-Object { (Get-Content -LiteralPath (Join-Path $_.FullName 'type') -ErrorAction SilentlyContinue) -eq 'Mains' } |
                    ForEach-Object { (Get-Content -LiteralPath (Join-Path $_.FullName 'online')) -eq '1' })
                platform_profile = Get-Content -LiteralPath /sys/firmware/acpi/platform_profile -ErrorAction SilentlyContinue
                idle_inhibit = $idleInhibit
                display_wrapper = $wrapper
            }
        }
    }

    $selections = @(foreach ($name in ($Scenario | Select-Object -Unique)) {
        foreach ($camera in ($RouteCamera | Select-Object -Unique)) {
            @{ scenario = $name; camera = $camera }
        }
    })
    foreach ($selection in $selections) {
        $name = $selection.scenario
        $isScene = $name.StartsWith('scene-', [StringComparison]::Ordinal)
        $timeout = if ($name -eq 'scene-memory') {
            if ($DurationSeconds -eq 0) { 7800 } else { $DurationSeconds + 600 }
        } elseif ($name -eq 'scene-soak') {
            # 长测在到时后跑完当前一段（最长约 15 分钟），再加读档与退出余量。
            $(if ($DurationSeconds -eq 0) { 7200 } else { $DurationSeconds }) + 1800
        } elseif ($name.StartsWith('scene-route-', [StringComparison]::Ordinal)) { 3600 }
        elseif ($isScene) { 600 } else {
            # 实测采样外耗时最多 39.5 秒；总耗时取 1.5 倍并向上取整到 30 秒，不降低原有预算。
            $sampleSeconds = if ($DurationSeconds -gt 0) { $DurationSeconds } else { 30 }
            [Math]::Max(180, [Math]::Ceiling(($sampleSeconds + 40) * 1.5 / 30) * 30)
        }
        for ($iteration = 1; $iteration -le $Repeat; $iteration++) {
            $runName = if ($StreamingDiagnostics -or $lowOrbit) { "$name-$($selection.camera)-$iteration" } else { "$name-$iteration" }
            $runDirectory = Join-Path $OutputDirectory $runName
            [System.IO.Directory]::CreateDirectory($runDirectory) | Out-Null
            $reportPath = Join-Path $runDirectory 'report.json'
            $identityPath = Join-Path $runDirectory 'identity.json'
            $loadPath = Join-Path $runDirectory 'external-load.jsonl'
            $stopPath = Join-Path $runDirectory 'sampler.stop'
            $readyPath = Join-Path $runDirectory 'sampler.ready'
            Write-Json $identityPath $identity
            $run = @{ scenario = $name; repeat = $iteration; report = "$runName/report.json"; external_load = $loadPath; succeeded = $false }
            if ($StreamingDiagnostics) { $run.streaming_diagnostics = $true }
            if ($StreamingDiagnostics -or $lowOrbit) { $run.route_camera = $selection.camera }
            $runs.Add($run)
            $userDirectories = if ($IsWindows) { @{ APPDATA = 'appdata'; LOCALAPPDATA = 'localappdata' } }
            else { @{ XDG_CONFIG_HOME = 'config'; XDG_DATA_HOME = 'data'; XDG_CACHE_HOME = 'cache' } }
            $previousUserDirectories = @{}
            foreach ($variable in $userDirectories.Keys) { $previousUserDirectories[$variable] = [Environment]::GetEnvironmentVariable($variable) }
            $job = $null
            try {
                foreach ($variable in $userDirectories.Keys) {
                    $directory = Join-Path $runDirectory $userDirectories[$variable]
                    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
                    [Environment]::SetEnvironmentVariable($variable, $directory)
                }
                try {
                    $job = Start-Job -ArgumentList (Join-Path $PSScriptRoot 'measurement-host.ps1'), $loadPath, $stopPath, $readyPath -ScriptBlock {
                        param($helper, $loadPath, $stopPath, $readyPath)
                        $ErrorActionPreference = 'Stop'
                        . $helper
                        Write-MeasurementLoadSamples $loadPath $stopPath $readyPath
                    }
                    $readyDeadline = [DateTime]::UtcNow.AddSeconds(15)
                    while (-not (Test-Path -LiteralPath $readyPath)) {
                        if ($job.State -eq 'Failed' -or [DateTime]::UtcNow -gt $readyDeadline) {
                            throw '外部负载采样未能启动；游戏测量继续。'
                        }
                        Start-Sleep -Milliseconds 100
                    }
                } catch { $_.Exception.Message | Add-Content (Join-Path $runDirectory 'sampler-errors.log') }
                $arguments = @(
                    '--windowed', '--resolution', '1920x1080', '--log-file', (Join-Path $runDirectory 'godot.log'), '--',
                    "--measure=$name", '--measure-input', $InputPath, '--measure-output', $reportPath, '--measure-identity', $identityPath)
                if ($isScene -or $DurationSeconds -gt 0) { $arguments += @('--measure-duration', $DurationSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)) }
                if ($StreamingDiagnostics) { $arguments += '--measure-streaming' }
                if ($StreamingDiagnostics -or $lowOrbit) { $arguments += @('--measure-route-camera', $selection.camera) }
                if ($NodeReadyHold) { $arguments += '--measure-node-ready-hold' }
                if ($ReferencePath) { $arguments += @('--measure-reference', $ReferencePath) }
                if ($PSBoundParameters.ContainsKey('TrainId')) { $arguments += @('--measure-train', $TrainId.ToString([Globalization.CultureInfo]::InvariantCulture)) }
                if ($PSBoundParameters.ContainsKey('NodeId')) { $arguments += @('--measure-node', $NodeId.ToString([Globalization.CultureInfo]::InvariantCulture)) }
                $launch = @($ExecutablePath) + $arguments
                if ($null -ne $wrapper) {
                    $launch = @($wrapper.program) + $wrapper.arguments + @('python3',
                        (Join-Path $PSScriptRoot 'game-process.py'), (Join-Path $runDirectory 'game-process.json')) + $launch
                }
                if ($idleInhibit) { $launch = @('systemd-inhibit', '--what=idle:sleep', '--who=train-measure', '--why=window measurement', '--mode=block') + $launch }
                $result = Invoke-HiddenProcess -FilePath $launch[0] -TimeoutSeconds $timeout -ArgumentList @($launch | Select-Object -Skip 1)
                $result.Output | Set-Content -LiteralPath (Join-Path $runDirectory 'process.log') -Encoding utf8
                Assert-MeasurementRunOutcome -Result $result -RunDirectory $runDirectory -RunName "$name/$iteration" -Gamescope:($null -ne $wrapper)
            }
            finally {
                foreach ($variable in $previousUserDirectories.Keys) { [Environment]::SetEnvironmentVariable($variable, $previousUserDirectories[$variable]) }
                if ($null -ne $job) {
                    [System.IO.File]::WriteAllText($stopPath, '')
                    try {
                        $finished = Wait-Job -Job $job -Timeout 15
                        if ($null -eq $finished) { Stop-Job -Job $job; throw '外部负载采样未能及时停止。' }
                        Receive-Job -Job $job -ErrorAction Stop | Out-Null
                        if ($job.State -ne 'Completed') { throw '外部负载采样失败。' }
                    }
                    catch { $_.Exception.Message | Add-Content (Join-Path $runDirectory 'sampler-errors.log') }
                    finally { Remove-Job -Job $job -Force }
                }
            }
            $run.succeeded = $true
            Write-Host "$name/$iteration 完成：$reportPath"
        }
    }
}
catch { $failure = $_.Exception.Message }
finally {
    if ($null -ne $displayAwake) {
        try { $displayAwake.Dispose(); $displayAwakeState.restored = $true }
        catch { $failure = @($failure, $_.Exception.Message) | Where-Object { $_ } | Join-String -Separator '; ' }
    }
    if ($null -ne $summaryPath) {
        Write-Json $summaryPath @{
            succeeded = $null -eq $failure; error = $failure; runs = @($runs.ToArray())
            display_awake = $(if ($IsWindows) { $displayAwakeState } else { @{ idle_inhibit = $idleInhibit } })
            display_wrapper = $wrapper
            scope = @{
                final_performance = $false
                warmup_seconds = 5; sample_seconds = $(if ($DurationSeconds -gt 0) { $DurationSeconds } else { 30 }); repetitions = $Repeat
                scene_duration_seconds_argument = $DurationSeconds
                scene_timing = '新 scene 情景的预热、采样和运行时长以各运行报告为准；DurationSeconds=0 使用情景默认值。'
                external_load = $(if ($IsWindows) { '每秒通过性能计数器记录总 CPU、可用物理内存、页面输入与读取速率及 Godot/train/dotnet 的 PID、名称、累计 CPU 秒、工作集；保留并发负载，不要求暂停其他任务。' }
                    else { '每秒记录 /proc/stat 总 CPU 占用、MemAvailable、最高温区温度、交流电状态及 Godot/train/dotnet 的 PID、名称、累计 CPU 秒、工作集；保留并发负载。' })
                limitation = $(if ($Machine -eq 'test') { '用户 2026-10-22 指定的测试机器上的真实窗口测量；阈值待用户确认，不判定通过。' }
                    else { '开发机真实窗口测量，仅用于重复采样与证据核对，不代表目标硬件最终性能。' })
            }
        }
    }
}
if ($null -ne $failure) { Write-Error $failure -ErrorAction Continue; exit 1 }
Write-Host "测量完成：$summaryPath"
exit 0
