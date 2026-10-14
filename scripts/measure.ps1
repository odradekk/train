#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ExecutablePath,
    [Parameter(Mandatory)][string] $InputPath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidateSet('map-1x', 'map-8x', 'follow-1x', 'follow-8x', 'node-1x', 'node-8x',
        'sdfgi-off', 'sdfgi-on', 'taa-off', 'taa-on')]
    [string[]] $Scenario = @('map-1x', 'map-8x', 'follow-1x', 'follow-8x', 'node-1x', 'node-8x',
        'sdfgi-off', 'sdfgi-on', 'taa-off', 'taa-on')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'checks/Invoke-HiddenProcess.ps1')

function Get-FileIdentity([string] $Path) {
    @{ path = $Path; sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Write-Json([string] $Path, $Value) {
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $Path -Encoding utf8
}

$repo = Split-Path $PSScriptRoot -Parent
$runs = [System.Collections.Generic.List[object]]::new()
$summaryPath = $null
$failure = $null
try {
    if (-not $IsWindows) { throw '测量入口需要 Windows Release 导出与真实窗口。' }
    if ($Scenario.Count -eq 0) { throw '至少选择一个情景。' }
    $ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
    $InputPath = (Resolve-Path -LiteralPath $InputPath).Path
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

    # 短命进程读取元数据，避免同一终端复测不同构建时程序集身份冲突或 DLL 锁定。
    $build = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -Command {
        param($path)
        $ErrorActionPreference = 'Stop'
        [Reflection.Assembly]::LoadFrom($path).GetCustomAttributes(
            [Reflection.AssemblyInformationalVersionAttribute], $false)[0].InformationalVersion
    } -args $assemblies[0].FullName
    if ($LASTEXITCODE -ne 0) { throw '无法读取 Train.dll 构建版本。' }
    if ($build -notmatch '\+([0-9a-f]{40})$') { throw "程序集版本未包含完整 Git 提交：$build" }
    $commit = $Matches[1]
    $apiBlob = & git -C $repo rev-parse "${commit}:docs/contracts/public-api.txt"
    if ($LASTEXITCODE -ne 0) { throw '无法读取构建提交的 public-api Git blob。' }
    $identity = @{
        build_commit = $commit
        public_api_git_blob = $apiBlob
        executable = Get-FileIdentity $ExecutablePath
        pck = Get-FileIdentity $pck
        assembly = Get-FileIdentity $assemblies[0].FullName
        input = Get-FileIdentity $InputPath
    }

    foreach ($name in ($Scenario | Select-Object -Unique)) {
        foreach ($repeat in 1..3) {
            $runDirectory = Join-Path $OutputDirectory "$name-$repeat"
            [System.IO.Directory]::CreateDirectory($runDirectory) | Out-Null
            $reportPath = Join-Path $runDirectory 'report.json'
            $identityPath = Join-Path $runDirectory 'identity.json'
            $loadPath = Join-Path $runDirectory 'external-load.jsonl'
            $stopPath = Join-Path $runDirectory 'sampler.stop'
            $readyPath = Join-Path $runDirectory 'sampler.ready'
            Write-Json $identityPath $identity
            $run = @{ scenario = $name; repeat = $repeat; report = $reportPath; external_load = $loadPath; succeeded = $false }
            $runs.Add($run)
            $previousAppData = $env:APPDATA
            $previousLocalAppData = $env:LOCALAPPDATA
            $job = $null
            try {
                $env:APPDATA = Join-Path $runDirectory 'appdata'
                $env:LOCALAPPDATA = Join-Path $runDirectory 'localappdata'
                [System.IO.Directory]::CreateDirectory($env:APPDATA) | Out-Null
                [System.IO.Directory]::CreateDirectory($env:LOCALAPPDATA) | Out-Null
                $job = Start-Job -ArgumentList $loadPath, $stopPath, $readyPath -ScriptBlock {
                    param($loadPath, $stopPath, $readyPath)
                    $ErrorActionPreference = 'Stop'
                    while (-not (Test-Path -LiteralPath $stopPath)) {
                        $cpu = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"
                        $processes = @(Get-Process | Where-Object { $_.ProcessName -like 'Godot*' -or $_.ProcessName -eq 'dotnet' } |
                            ForEach-Object { @{ pid = $_.Id; name = $_.ProcessName; cpu_seconds = $_.CPU; working_set_bytes = $_.WorkingSet64 } })
                        @{ utc = [DateTime]::UtcNow.ToString('O'); total_cpu_percent = $cpu.PercentProcessorTime; processes = $processes } |
                            ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath $loadPath -Encoding utf8
                        [System.IO.File]::WriteAllText($readyPath, '')
                        Start-Sleep -Seconds 1
                    }
                }
                $readyDeadline = [DateTime]::UtcNow.AddSeconds(15)
                while (-not (Test-Path -LiteralPath $readyPath)) {
                    if ($job.State -eq 'Failed' -or [DateTime]::UtcNow -gt $readyDeadline) { throw '外部负载采样未能启动。' }
                    Start-Sleep -Milliseconds 100
                }
                $result = Invoke-HiddenProcess -FilePath $ExecutablePath -TimeoutSeconds 180 -ArgumentList @(
                    '--windowed', '--resolution', '1920x1080', '--log-file', (Join-Path $runDirectory 'godot.log'), '--',
                    "--measure=$name", '--measure-input', $InputPath, '--measure-output', $reportPath, '--measure-identity', $identityPath)
                $result.Output | Set-Content -LiteralPath (Join-Path $runDirectory 'process.log') -Encoding utf8
                Write-Json (Join-Path $runDirectory 'process.json') @{
                    exit_code = $result.ExitCode; timed_out = $result.TimedOut
                    cleanup_succeeded = $result.CleanupSucceeded; duration_ms = $result.DurationMs
                }
                if ($result.ExitCode -ne 0 -or $result.TimedOut -or -not $result.CleanupSucceeded) { throw "$name/$repeat 进程失败或超时。" }
                if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "$name/$repeat 未生成报告。" }
                $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
                if ($report.outcome -ne 'completed' -or $report.identity.build_configuration -ne 'ExportRelease' -or
                    @($report.timing.frames).Count -eq 0 -or $null -eq $report.timing.frames -or
                    $report.scope.final_performance -ne $false) { throw "$name/$repeat 报告未完成、不是 Release 或缺少真实绘制帧。" }
            }
            finally {
                $env:APPDATA = $previousAppData
                $env:LOCALAPPDATA = $previousLocalAppData
                if ($null -ne $job) {
                    [System.IO.File]::WriteAllText($stopPath, '')
                    try {
                        $finished = Wait-Job -Job $job -Timeout 15
                        if ($null -eq $finished) { Stop-Job -Job $job; throw '外部负载采样未能及时停止。' }
                        Receive-Job -Job $job -ErrorAction Stop | Out-Null
                        if ($job.State -ne 'Completed') { throw '外部负载采样失败。' }
                    }
                    finally { Remove-Job -Job $job -Force }
                }
            }
            $run.succeeded = $true
            Write-Host "$name/$repeat 完成：$reportPath"
        }
    }
}
catch { $failure = $_.Exception.Message }
finally {
    if ($null -ne $summaryPath) {
        Write-Json $summaryPath @{
            succeeded = $null -eq $failure; error = $failure; runs = @($runs.ToArray())
            scope = @{
                final_performance = $false
                warmup_seconds = 5; sample_seconds = 30; repetitions = 3
                external_load = '每秒记录总 CPU 与 Godot/dotnet 的 PID、名称、累计 CPU 秒、工作集；保留并发负载，不要求暂停其他任务。'
                limitation = '开发机真实窗口测量，仅用于重复采样与证据核对，不代表目标硬件最终性能。'
            }
        }
    }
}
if ($null -ne $failure) { Write-Error $failure -ErrorAction Continue; exit 1 }
Write-Host "测量完成：$summaryPath"
exit 0
