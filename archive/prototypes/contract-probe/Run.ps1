# 可抛弃原型的单次复现入口；不替代 scripts/run-tests.ps1 的生产验证。
param([string]$OutputDirectory = ('TestResults/run-tests/contract-probe-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')))
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
if (Test-Path -LiteralPath $output) { throw "输出目录已存在：$output" }
New-Item -ItemType Directory -Path $output | Out-Null
$plans = @(
    @{ name = 'structure-core'; script = 'Structure/Run.ps1'; parameter = '-EvidenceDirectory' },
    @{ name = 'content'; script = 'Content/Run.ps1'; parameter = '-OutputDirectory' },
    @{ name = 'scenes'; script = 'Scenes/run.ps1'; parameter = '-OutputDirectory' },
    @{ name = 'godot'; script = 'Godot/run.ps1'; parameter = '-OutputDirectory' }
)
$outcomes = [Collections.Generic.List[object]]::new()
Push-Location $repo
try {
    $head = & git rev-parse HEAD
    $gitStatus = @(& git status --short)
    $sourceFiles = @('global.json', 'export_presets.cfg')
    foreach ($folder in @('Contracts/Core','Contracts/Content','Contracts/Scenes','prototypes/core-contract','prototypes/content-contract','prototypes/scenes-contract','prototypes/godot-contract')) {
        $sourceFiles += Get-ChildItem -LiteralPath $folder -File |
            Where-Object Extension -In '.cs','.csproj' | ForEach-Object { [IO.Path]::GetRelativePath($repo, $_.FullName) }
    }
    $sourceFiles += Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.godot)[\\/]' -and $_.Extension -in '.cs','.csproj','.ps1','.md','.tscn','.godot' } |
        ForEach-Object { [IO.Path]::GetRelativePath($repo, $_.FullName) }
    $manifest = foreach ($path in $sourceFiles | Sort-Object -Unique) { @{ path = $path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'source-inputs.json')
    foreach ($plan in $plans) {
        $script = Join-Path $PSScriptRoot $plan.script
        $childOutput = Join-Path $OutputDirectory $plan.name
        $command = "pwsh -NoProfile -File $script $($plan.parameter) $childOutput"
        Write-Output "开始 $($plan.name)"
        $watch = [Diagnostics.Stopwatch]::StartNew()
        & pwsh -NoProfile -File $script $plan.parameter $childOutput *> (Join-Path $output ($plan.name + '-runner.log'))
        $code = $LASTEXITCODE
        $watch.Stop()
        $outcomes.Add(@{ module = $plan.name; command = $command; exitCode = $code; elapsedMilliseconds = $watch.ElapsedMilliseconds; status = $(if ($code -eq 0) { 'passed' } else { 'failed' }) })
        Write-Output "$($plan.name) exit=$code"
    }
    # 汇总不能把后来修改的源码配上先前通过记录。
    $changedInputs = @($manifest | Where-Object { (Get-FileHash -LiteralPath $_.path -Algorithm SHA256).Hash -ne $_.sha256 })
    $allPassed = @($outcomes | Where-Object exitCode -NE 0).Count -eq 0 -and $changedInputs.Count -eq 0
    $summary = @{
        schema = 'contract-prototype-292'; testedCommit = $head; workingTree = $gitStatus
        contractInput = '8636b68cf035af7f53967199098244e439313b87'
        status = $(if ($allPassed) { 'prototype_checks_passed' } else { 'failed' })
        formalBaseline = 'not_established'; productionIsolation = 'not_executed'
        engine = 'Godot .NET 4.7.2'; sdk = (& dotnet --version); powershell = $PSVersionTable.PSVersion.ToString()
        outcomes = $outcomes; sourceChangedDuringRun = $changedInputs
        pending = @('真实生产者运行同组断言', '实际创建工厂位置检查与生产隔离入口', '完整内容计算、核心业务与录制重放', '真实体素几何与编辑', '完整宿主文件/资产/资源事务与重启加载', '完整联调、窗口观感和性能')
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'summary.json')
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# 四模块契约原型运行记录')
    $lines.Add('')
    $lines.Add("源码基准：$head；工作区状态及逐文件 SHA256 见 summary.json / source-inputs.json。")
    $lines.Add("整体原型检查：$($summary.status)。未建立正式契约基线，未执行四模块生产隔离或完整联调。")
    $lines.Add('')
    $lines.Add('| 范围 | 退出码 | 耗时（毫秒） | 日志 |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($result in $outcomes) { $lines.Add("| $($result.module) | $($result.exitCode) | $($result.elapsedMilliseconds) | $($result.module)-runner.log |") }
    $lines.Add('')
    $lines.Add('每个子目录保存命令、隔离输入与实际引用、正确／故意破坏／恢复日志。content 另有日期目录；Godot 同时保存源运行与 Windows 导出运行。')
    $lines.Add('五类违规：禁止程序集依赖、公开接口不兼容、拒绝前改变活动内容、迟到结果覆盖新会话、改写旧快照嵌套数据。正确和恢复版本须通过，破坏版本须由指定断言明确检出。')
    $lines.Add('')
    $lines.Add('边界及剩余项以 archive/prototypes/contract-probe/README.md 的矩阵为准。固定情景不证明业务算法；真实文件检查不证明断电耐久或生产包加载；headless 引擎数据检查不证明画面。')
    $lines | Set-Content -LiteralPath (Join-Path $output 'report.md')
    Write-Output "证据：$output"
    if (-not $allPassed) { exit 1 }
} finally { Pop-Location }
