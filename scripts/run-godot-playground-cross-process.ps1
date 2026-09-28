param([string] $OutputDirectory)

# Formal Playground comparison for #127. Each leg is a fresh Godot process.
# Run after building Train.csproj, with GODOT set to the Godot .NET console executable.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (-not $env:GODOT -or -not (Test-Path -LiteralPath $env:GODOT)) {
    [Console]::Error.WriteLine('GODOT must point at the Godot .NET console executable.')
    exit 2
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root ('.godot/validation/issue-127/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) {
    [Console]::Error.WriteLine("Output directory already exists: $OutputDirectory")
    exit 1
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$evidence = Join-Path $OutputDirectory 'evidence'
New-Item -ItemType Directory -Path $evidence | Out-Null

foreach ($name in @(
    'TRAIN_VOXEL_SMOKE', 'TRAIN_VOXEL_SMOKE_REENTER', 'TRAIN_VOXEL_SMOKE_RESUME', 'TRAIN_VOXEL_OBSERVE',
    'TRAIN_VOXEL_BASELINE', 'TRAIN_CONTENT_SMOKE', 'TRAIN_CONTENT_EXPORT_SMOKE', 'TRAIN_MODS_EXPORT_SMOKE',
    'TRAIN_LOG_SMOKE', 'TRAIN_PLAYGROUND_SMOKE', 'TRAIN_PLAYGROUND_SMOKE_MODE', 'TRAIN_PLAYGROUND_CAPTURE',
    'TRAIN_PLAYGROUND_CAPTURE_STEPS', 'TRAIN_AUTHORITY_CHECKPOINT', 'TRAIN_AUTHORITY_CHECKPOINT_DIR',
    'TRAIN_AUTHORITY_BUILD', 'TRAIN_PLAYGROUND_SAVE_ID'
)) {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}
$env:TRAIN_PLAYGROUND_CROSS_PROCESS = '1'
$env:TRAIN_PLAYGROUND_CONFIG = 'straight-rail-train'
$env:TRAIN_PLAYGROUND_CROSS_EVIDENCE = $evidence

$head = (git rev-parse HEAD).Trim()
$status = @(git status --porcelain)
$configuration = [ordered]@{
    issue = 127
    commit = $head
    dirty = ($status.Count -gt 0)
    configuration = 'straight-rail-train'
    sequence = 'cross-process-comparison'
    save_after = 8
    godot = $env:GODOT
    godot_version = (& $env:GODOT --headless --version | Select-Object -First 1)
    dotnet = (dotnet --version).Trim()
    powershell = $PSVersionTable.PSVersion.ToString()
    operating_system = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    command = 'pwsh -NoProfile -File scripts/run-godot-playground-cross-process.ps1 -OutputDirectory <fresh path>'
}
$configuration | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'config.json') -Encoding utf8

function Invoke-Leg([string] $leg, [string] $userDirectory, [string] $documentDirectory, [string] $logName = $leg) {
    $env:TRAIN_PLAYGROUND_CROSS_LEG = $leg
    $env:TRAIN_PLAYGROUND_CROSS_EVIDENCE = $documentDirectory
    $env:TRAIN_MODS_SMOKE_USER_DATA = $userDirectory
    New-Item -ItemType Directory -Path $userDirectory -Force | Out-Null
    $out = Join-Path $OutputDirectory "$logName.stdout.log"
    $err = Join-Path $OutputDirectory "$logName.stderr.log"
    $process = Start-Process -FilePath $env:GODOT -ArgumentList @('--headless', '--path', '.', '--quit-after', '3000') `
        -WorkingDirectory $root -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput $out -RedirectStandardError $err
    $text = [System.IO.File]::ReadAllText($out) + "`n" + [System.IO.File]::ReadAllText($err)
    "leg=$leg log=$logName pid=$($process.Id) exit=$($process.ExitCode) user_data=$userDirectory evidence=$documentDirectory" |
        Add-Content -LiteralPath (Join-Path $OutputDirectory 'processes.log') -Encoding utf8
    return @{ Code = $process.ExitCode; Text = $text }
}

function Require-Success([hashtable] $run, [string] $leg, [string[]] $markers) {
    if ($run.Code -ne 0 -or -not $run.Text.Contains("CROSS_DONE leg=$leg") -or
        $run.Text.Contains('CROSS_FAILED') -or $run.Text.Contains('PLAYGROUND_ESTABLISH_FAILED') -or
        $run.Text.Contains('Unhandled exception') -or $run.Text.Contains('ERROR:')) {
        throw "$leg failed or printed an error; inspect $OutputDirectory/$leg.*.log (exit $($run.Code))."
    }
    foreach ($marker in $markers) {
        if (-not $run.Text.Contains($marker)) {
            throw "$leg omitted required marker: $marker"
        }
    }
}

function Check-Count([string] $text, [string] $marker, [int] $count) {
    $found = ([regex]::Matches($text, [regex]::Escape($marker))).Count
    if ($found -ne $count) {
        throw "Expected $count occurrences of $marker; got $found."
    }
}

try {
    $continuous = Invoke-Leg 'continuous' (Join-Path $OutputDirectory 'continuous-user-data') $evidence
    Require-Success $continuous 'continuous' @('CROSS_PENDING_REJECTED_CONTINUED step=3',
        'CROSS_GODOT leg=continuous index=10', 'probe_hit=false', 'CROSS_GODOT leg=continuous index=13')
    Check-Count $continuous.Text 'CROSS_CHECKPOINT leg=continuous index=' 14
    for ($index = 0; $index -le 13; $index++) {
        if (-not (Test-Path -LiteralPath (Join-Path $evidence ('{0:D2}-evidence.json' -f $index)))) {
            throw "Missing canonical checkpoint $index."
        }
    }

    $savedUserData = Join-Path $OutputDirectory 'resume-user-data'
    $save = Invoke-Leg 'save' $savedUserData $evidence
    Require-Success $save 'save' @('CROSS_SAVED id=', 'index=8 boundary=0:8 pending=4')
    Check-Count $save.Text 'CROSS_COMPARE leg=save difference Equal' 9
    $saveMatch = [regex]::Match($save.Text, 'CROSS_SAVED id=([0-9a-fA-F-]{36})')
    if (-not $saveMatch.Success) { throw 'Save leg did not provide a save UUID.' }
    $env:TRAIN_PLAYGROUND_SAVE_ID = $saveMatch.Groups[1].Value
    $resume = Invoke-Leg 'resume' $savedUserData $evidence
    Require-Success $resume 'resume' @('CROSS_CHECKPOINT leg=resume index=8', 'paused=true', 'CROSS_PENDING_REJECTED_CONTINUED step=3')
    Check-Count $resume.Text 'CROSS_COMPARE leg=resume difference Equal' 6
    Remove-Item Env:TRAIN_PLAYGROUND_SAVE_ID

    $observe = Invoke-Leg 'observe' (Join-Path $OutputDirectory 'observation-user-data') $evidence
    Require-Success $observe 'observe' @('CROSS_TARGET_LOST target-lost', 'CROSS_OBSERVE index=11 mode=Follow', 'rail_hit=true')
    Check-Count $observe.Text 'CROSS_COMPARE leg=observe difference Equal' 14
    $failure = Invoke-Leg 'failure' (Join-Path $OutputDirectory 'failure-user-data') $evidence
    Require-Success $failure 'failure' @('CROSS_PROTECTION cancel=kept', 'CROSS_PROTECTION save_failure=WriteFailed kept=true',
        'CROSS_PROTECTION candidate_failure=NotFound kept=true', 'CROSS_RECOVERY_FAILED stage=base',
        'relationship=configuration_version status=IncompatibleBase kept=true')
    $badSave = [regex]::Match($failure.Text, 'CROSS_RECOVERY_FAILED[^\r\n]*save=([0-9a-fA-F-]{36})')
    if (-not $badSave.Success) { throw 'Failure leg did not provide the bad save UUID.' }
    $env:TRAIN_PLAYGROUND_SAVE_ID = $badSave.Groups[1].Value
    $badRestore = Invoke-Leg 'resume' (Join-Path $OutputDirectory 'failure-user-data') $evidence 'bad-restore'
    Remove-Item Env:TRAIN_PLAYGROUND_SAVE_ID
    if ($badRestore.Code -eq 0 -or
        -not $badRestore.Text.Contains('PLAYGROUND_SMOKE_FAILED load status=IncompatibleBase stage=base') -or
        -not $badRestore.Text.Contains('relationship=configuration_version') -or
        -not $badRestore.Text.Contains('PLAYGROUND_ESTABLISH_FAILED')) {
        throw 'Corrupted save unexpectedly established a session or omitted its failure stage.'
    }
    $adaptation = Invoke-Leg 'adaptation' (Join-Path $OutputDirectory 'adaptation-user-data') $evidence
    Require-Success $adaptation 'adaptation' @('CROSS_ADAPTATION status=Completed relationship=semantic_revision paused=true',
        'domain_comparison=separate')

    $negative = Join-Path $OutputDirectory 'field-difference-evidence'
    New-Item -ItemType Directory -Path $negative | Out-Null
    Copy-Item -Path (Join-Path $evidence '*') -Destination $negative
    $changed = Join-Path $negative '09-evidence.json'
    $node = [System.Text.Json.Nodes.JsonNode]::Parse([System.IO.File]::ReadAllText($changed))
    $canonical = [System.Text.Json.Nodes.JsonNode]::Parse($node['canonical'].GetValue[string]())
    $canonical['next_entity_id'] = [System.Text.Json.Nodes.JsonValue]::Create(
        [ulong]($canonical['next_entity_id'].GetValue[ulong]() + 1))
    $node['canonical'] = $canonical.ToJsonString()
    $node['digest'] = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($node['canonical'].GetValue[string]())))
    [System.IO.File]::WriteAllText($changed, $node.ToJsonString())
    $field = Invoke-Leg 'observe' (Join-Path $OutputDirectory 'field-difference-user-data') $negative 'field-difference'
    if ($field.Code -eq 0 -or -not $field.Text.Contains('CROSS_COMPARE leg=observe difference FieldMismatch') -or
        -not $field.Text.Contains('CROSS_COMPARE leg=observe checkpoint 9 input ') -or
        -not $field.Text.Contains('CROSS_COMPARE leg=observe field next_entity_id') -or
        -not $field.Text.Contains('CROSS_FAILED leg=observe index=9')) {
        throw 'Injected field difference did not fail at checkpoint 9 with its field and input position.'
    }

    $context = [System.Text.Json.Nodes.JsonNode]::Parse([System.IO.File]::ReadAllText((Join-Path $evidence '00-evidence.json')))
    $buildId = $context['build'].GetValue[string]()
    $contentDigest = $context['content_digest'].GetValue[string]()
    $report = @"
# Playground 跨进程对照 — Issue #127

受测提交：$head（工作树变更：$($configuration.dirty)）；配置 straight-rail-train v1；正式序列 cross-process-comparison，保存于检查点 8、边界 0:8，待执行输入 4 条。

构建：$buildId；语义内容摘要：$contentDigest；环境：$($configuration.operating_system) / $($configuration.architecture)，.NET $($configuration.dotnet)，Godot $($configuration.godot_version)，PowerShell $($configuration.powershell)。原始配置见 config.json，独立进程 PID/退出码见 processes.log。

实际输入：1 制造桥接车辆；2 损伤桥接部件并断裂；3 启动车头；4 放置探针体素；5–8 在暂停边界依次排入两次移除、放置与损伤（目标步 3）；保存并退出；9 推进至步 3，第二次移除拒绝而后续输入执行；10 毁掉探针体素；11 推进至步 300；12 损伤轨道；13 推进至步 2300 并检查 ForwardGap 故障停车。每个正式输入的完整规范载荷、结果及边界见 continuous.stdout.log 和证据 JSON。

| 证据层 | 本次结果 | 原始证据 |
| --- | --- | --- |
| 领域 | 正式序列 13 步操作状态/断言通过；暂停期 4 条待执行输入、失效拒绝后继续、ID/断裂、速度及断轨停车进入权威检查点。完整领域单测结果另随交付记录。 | continuous.stdout.log、evidence/00–13-evidence.json |
| 真实 Godot | 每步检查正式世界显示计数、轨道实体、发布碰撞命中、探针体素增删的显示/碰撞生命周期和车辆视觉；观察过程执行面板/查询、自由/聚焦/跟随及目标消失，并改变帧批次。 | continuous.stdout.log、observe.stdout.log |
| 跨进程 | 连续、保存退出、另进程恢复、观察四路在同一输入和边界逐检查点相等；恢复默认暂停。字段注入在检查点 9 报 next_entity_id 与输入位置并退出非零；坏档恢复入口也退出非零并报 base/configuration_version，当前会话保持。 | processes.log、save.stdout.log、resume.stdout.log、field-difference.stdout.log、failure.stdout.log、bad-restore.stderr.log |
| 内容适配 | 独立 Godot 进程修改已存语义修订标识后普通读档成功，报告 semantic_revision 适配并暂停；不参与同输入逐点摘要比较。 | adaptation.stdout.log、adaptation-user-data |
| 人工 | 待用户在图形窗口检查镜头切换、跟随连续性、控制反馈、失败诊断和保存切换保护。Headless 断言不视作人工观感通过。 | 下列步骤 |

取消、保存失败、缺失候选、坏档均保持当前会话；内容适配与同输入比较分列。上述适配只改变存档依赖记录的修订标识，不覆盖未来真实内容规格变化的全矩阵。

## 父票 #109 验收映射

| #109 AC | 子票 | 本次新增证据与边界 |
| --- | --- | --- |
| 1 固定配置/普通存档默认暂停 | #121、#125、#127 | continuous 与 resume 检查入口暂停；保存后状态在检查点 8 对照。 |
| 2 观察模式及目标生命周期 | #124、#127 | observe 的模式、目标失效与相机位置断言；人工连续性待验证。 |
| 3 模拟控制/观察隔离 | #121、#124、#127 | 检查点 0–13 与 observe 同输入比较；帧批次变化。 |
| 4 切换保护与失败保留 | #125、#127 | failure 检查取消、写入失败、缺失候选、坏档。 |
| 5 配置/正式序列分离 | #121–#123、#127 | cross-process-comparison 引用 straight-rail-train，四个独立进程段复用。 |
| 6 输入全序及失效继续 | #122、#123、#127 | 检查点 8/9 的边界、四条待执行输入和拒绝后执行。 |
| 7 规范编码 | #122、#126 | 复用 AuthorityCodec 与既有黄金向量；本次未重做编码矩阵。 |
| 8 实体 ID | #122、#126、#127 | 每点 next_entity_id 跨进程对照；既有失败分配/溢出单测继续保留。 |
| 9 无状态随机 | #126 | 断裂后的权威状态参与对照；算法黄金向量沿用 #126。 |
| 10 权威摘要与首差异 | #126、#127 | 14 个检查点的构建/内容/输入/边界比较；字段注入定位检查点 9。 |
| 11 分层报告 | #127 | 本报告四栏，内容适配单列；人工明确待验证。 |

重跑：pwsh -NoProfile -File scripts/run-godot-playground-cross-process.ps1 -OutputDirectory <新的空路径>。人工：运行 dotnet build Train.csproj 与 Godot 图形入口；选择 Playground 的 straight-rail-train，依次检查自由相机、选择、聚焦、车辆跟随、运行/暂停/单步/N 步、目标销毁提示；修改后分别尝试取消切换、保存后切换、保存失败及坏档读取，核对世界、镜头与反馈。使用新存档，勿覆盖本目录的用户数据。

边界：保留四个旧真实入口；#118 迁移/退役未验收；无有界录制、隔离重放、ZIP 或性能结论。
"@
    $report | Set-Content -LiteralPath (Join-Path $OutputDirectory 'report.md') -Encoding utf8
    Write-Host "PASS Issue #127 cross-process comparison: $OutputDirectory"
    exit 0
}
catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $OutputDirectory 'failure.txt') -Encoding utf8
    [Console]::Error.WriteLine("Issue #127 cross-process comparison failed: $_")
    [Console]::Error.WriteLine("Evidence: $OutputDirectory")
    exit 1
}
