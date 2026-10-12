# PowerShell 7 统一检查入口，负责发现、选择、准备环境、调用检查和发布报告。
# MSTest 分类来自 TestCategory；App 检查由 runner-catalog.json 登记，断言留在各自实现中。
# Windows CI 与本地验证使用同一入口。
#
# 退出码：
#   0 所选必需检查都通过
#   1 检查、构建、仓库输入或入口自身失败
#   2 阻塞（环境不可用、发现失败、清理失败、玩家用户目录变化、待实现的检查）
#   3 取消或超时
#   4 参数非法、未知名称或明确选择零匹配
# 有效运行按失败、清理失败、取消/超时、其他阻塞/未完成、通过的顺序合并结果。

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$script:Utf8 = [System.Text.UTF8Encoding]::new($false)
try { [Console]::OutputEncoding = $script:Utf8 } catch { }
$OutputEncoding = $script:Utf8

$script:RawArgs = @($args)
$script:Root = Split-Path -Parent $PSScriptRoot
$script:ReaderPath = Join-Path $PSScriptRoot "metadata-reader/TestMetadataReader.cs"
$script:CancelRequested = $false
$script:TestHostPath = ""
$script:OwnedPid = 0
$script:RunDirectory = ""
$script:HandlerAdded = $false
$script:OnCancel = $null
$script:Parsed = $null
$script:TestedGit = $null
$script:RepoCatalogLoaded = $false
$script:RepoCatalog = $null
$script:ExternalCatalog = $null
$script:RunnerCatalogMode = "none"
$script:MigrationFeedback = ""
$script:RunChecks = $null
$script:OverrideCfg = $null
$script:PerCheckAppDataUsed = $false
$script:IsolationRoots = [System.Collections.Generic.List[string]]::new()
$script:IsolatedModules = @('core', 'content', 'scenes')
$script:TestAssemblyBoundary = '未指定 -Project 时，Core/Content 的 -Correctness 另行隔离构建；Scenes 测试入口建立后沿用此规则。指定 -Project 时在工作树构建，不证明模块隔离。普通 MSTest 不证明 Godot 引擎或隔离装配行为。'
. (Join-Path $PSScriptRoot 'checks/common-checks.ps1')

function Protect-StandardHandles {
    if (-not $IsWindows) { return }
    if (-not ("NativeHandleGuard" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NativeHandleGuard {
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
    public static void ClearInherit() {
        SetHandleInformation(GetStdHandle(-10), 1, 0);
        SetHandleInformation(GetStdHandle(-11), 1, 0);
        SetHandleInformation(GetStdHandle(-12), 1, 0);
    }
}
"@
    }
    [NativeHandleGuard]::ClearInherit()
}

function Register-CancelSignal {
    if (-not ("ConsoleCancelSignal" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Threading;
public static class ConsoleCancelSignal {
    public static volatile int Requested;
    static int registered;
    static void Handle(object sender, ConsoleCancelEventArgs args) {
        args.Cancel = true;
        Requested = 1;
    }
    public static void Register() {
        if (Interlocked.Exchange(ref registered, 1) == 1) return;
        Requested = 0;
        Console.CancelKeyPress += Handle;
    }
    public static void Unregister() {
        if (Interlocked.Exchange(ref registered, 0) == 0) return;
        Console.CancelKeyPress -= Handle;
    }
}
"@
    }
    [ConsoleCancelSignal]::Requested = 0
    $script:CancelRequested = $false
    try {
        [ConsoleCancelSignal]::Register()
        $script:HandlerAdded = $true
    }
    catch {
        $script:HandlerAdded = $false
    }
}

function Unregister-CancelSignal {
    if (-not $script:HandlerAdded) { return }
    try { [ConsoleCancelSignal]::Unregister() } catch { }
    $script:HandlerAdded = $false
}

function Test-CancelRequested {
    if (("ConsoleCancelSignal" -as [type]) -and [ConsoleCancelSignal]::Requested -ne 0) {
        $script:CancelRequested = $true
    }
    return [bool] $script:CancelRequested
}

function New-Map {
    return ,[System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
}

function New-List {
    return ,[System.Collections.Generic.List[object]]::new()
}

function Add-Map {
    param($Map, [string] $Key, $Value)
    $Map[$Key] = $Value
    return $Map
}

function Get-HelpText {
    param([string] $GroupText)
@"
用法
  pwsh -NoProfile -File scripts/run-tests.ps1
      只显示帮助和已发现的组，不执行测试。

  pwsh -NoProfile -File scripts/run-tests.ps1 -List [-Family <名称>] [-Scope <名称>] [-Test <用例>]
      列出筛选后的真实组和用例。

  pwsh -NoProfile -File scripts/run-tests.ps1 -Family <unit|headless> [-Scope <范围>] [-Test <用例>]
  pwsh -NoProfile -File scripts/run-tests.ps1 -Correctness [-Scope <范围>]
  pwsh -NoProfile -File scripts/run-tests.ps1 -Test godot.logging-smoke
      显式选择后执行。同维度多个取值取并集，类别与范围取交集，同一用例只保留一次。
      不限定模块的 -Correctness 包含已登记的 Godot 检查，因此需要 GODOT，并会构建 Train.csproj 再导入。

  -Project <路径>
      指定单个 MSTest 项目；不自动发现其他项目，不能与 -Module 同用。
      默认发现 Tests 下显式引用 MSTest 或 MSTest.TestAdapter 的项目。

  -Module <core|content|scenes|godot>
      构建和发现前限定模块项目；多个模块取并集。缺少目标入口返回阻塞。
      模块标签表示测试责任；隔离范围见下方 test-assembly。

  -RunnerCatalog <路径|none>
      默认读取 scripts/runner-catalog.json。none 只跑 MSTest。
      路径替换本次可运行的外部检查。清单仍来自仓库里的目录文件。

  -BuildGame
      即使所选检查不依赖游戏程序集，也复用准备步骤构建 Train.csproj；不需要 GODOT。
      构建失败会保留 game-build.log 并使整次运行非成功，独立检查仍可执行。

  -PrepTimeoutSeconds <秒>
      测试构建、游戏构建、Godot 导入和版本探测共用的卡住上限，默认 120。
      2026-09-30 用空的包缓存实测：Train.Tests 还原 10471ms、构建 8315ms；Train.csproj 还原 5834ms、构建 8608ms。
      同一天的混合运行里导入用了 37726ms。120 秒仍约为这次导入的三倍，余量够用，所以上限保持 120。它是卡住上限，不是通过条件。
      检查超时仍由 -TimeoutSeconds 或目录中的值决定。

标记
  用例从已构建程序集的 TestCategory 读取：family:unit、family:headless、scope:<稳定功能范围>、
  requires:godot、requires:process、requires:export、requires:graphical。
  类别按测试实际行为标注，可标在类上。普通 C# 的多模块或会话检查是 headless，不是单元测试。

退出码
  0 所选必需检查都通过
  1 检查、构建、项目输入或入口自身失败
  2 阻塞（环境不可用、发现失败、清理失败、玩家用户目录变化、待实现的检查）
  3 取消或超时
  4 参数非法、未知名称或明确选择零匹配
  有效运行按失败、清理失败、取消/超时、其他阻塞/未完成、通过的顺序合并结果。

边界
  test-assembly: $($script:TestAssemblyBoundary)
  coverage: registered MSTest, Godot, and cross-process checks are not all repository validation.
  registered: the logging, content, main-menu, simulation, content-export, and mods-export App checks run through the shared App check driver.
  ci: Windows CI calls this entry. See docs/ci.md.
  pending: benchmark, long, manual, and platform are not empty groups that pass.
  manual: inventoried manual entries are not automated passes.
  isolation: each Godot check gets one APPDATA directory inside this run, and the import and the version probe each get one more. Processes started by one check share that directory. The entry does not write override.cfg. Killing the run leaves no user-data redirect. An existing project override.cfg is left untouched; its presence and SHA256 are recorded once, before Godot starts.
  partial: durationStatus partial keeps the milliseconds a cancelled or blocked check already ran. Only passed and failed checks are measured-this-run.

结果
  每次执行写入新的 TestResults/run-tests/<id>/summary.json、report.md，并保留原始 TRX 和 Godot 日志。
  记录的提交和脏状态取自构建开始前。运行结束时的 Git 状态单独记录。
  运行期间如果提交或工作区变了，结果不能证明结束时的干净提交已通过。
  读不到提交或工作区状态时，身份未验证，不能把该次运行当成干净提交已通过。
  不覆盖历史结果。脏工作区的结果不能证明干净提交已通过。
  取消：执行过程中创建该次目录下的 cancel.request，或按 Ctrl+C。只结束本次启动的进程树。

$(if ($GroupText) { $GroupText } else { "本次未能列出组。" })
"@
}

function Parse-RunArgs {
    param([string[]] $Raw)
    $parsed = @{
        Families = [System.Collections.Generic.List[string]]::new()
        Scopes = [System.Collections.Generic.List[string]]::new()
        Tests = [System.Collections.Generic.List[string]]::new()
        Correctness = $false
        BuildGame = $false
        List = $false
        Project = ""
        Modules = [System.Collections.Generic.List[string]]::new()
        Configuration = "Debug"
        TimeoutSeconds = 0
        PrepTimeoutSeconds = 0
        ResultsRoot = ""
        TestHostPath = ""
        DotnetPath = "dotnet"
        RunnerCatalog = ""
        Help = $false
        Error = ""
    }
    if ($Raw.Count -eq 0) {
        $parsed.Help = $true
        return [pscustomobject] $parsed
    }

    $single = @{
        "-Project" = "Project"
        "-Configuration" = "Configuration"
        "-TimeoutSeconds" = "TimeoutSeconds"
        "-PrepTimeoutSeconds" = "PrepTimeoutSeconds"
        "-ResultsRoot" = "ResultsRoot"
        "-TestHostPath" = "TestHostPath"
        "-DotnetPath" = "DotnetPath"
        "-RunnerCatalog" = "RunnerCatalog"
    }
    $seenSingle = @{}
    for ($i = 0; $i -lt $Raw.Count; $i++) {
        if ($parsed.Error) { break }
        $token = [string] $Raw[$i]
        if ($token -eq "-BuildGame") {
            if ($parsed.BuildGame) { $parsed.Error = "error: invalid-argument duplicate -BuildGame" }
            else { $parsed.BuildGame = $true }
            continue
        }
        if ($token -eq "-Correctness") {
            if ($parsed.Correctness) { $parsed.Error = "error: invalid-argument duplicate -Correctness" }
            else { $parsed.Correctness = $true }
            continue
        }
        if ($token -eq "-List") {
            if ($parsed.List) { $parsed.Error = "error: invalid-argument duplicate -List" }
            else { $parsed.List = $true }
            continue
        }
        if ($token -eq "-Family" -or $token -eq "-Scope" -or $token -eq "-Test" -or $token -eq "-Module") {
            if ($i + 1 -ge $Raw.Count) {
                $parsed.Error = "error: invalid-argument $token missing value"
                continue
            }
            $value = [string] $Raw[++$i]
            if ($value.StartsWith("-") -or [string]::IsNullOrWhiteSpace($value)) {
                $parsed.Error = "error: invalid-argument $token missing value"
                continue
            }
            if ($token -eq "-Family") { [void] $parsed.Families.Add($value) }
            elseif ($token -eq "-Scope") { [void] $parsed.Scopes.Add($value) }
            elseif ($token -eq "-Module") { [void] $parsed.Modules.Add($value) }
            else { [void] $parsed.Tests.Add($value) }
            continue
        }
        if ($single.ContainsKey($token)) {
            if ($seenSingle.ContainsKey($token)) {
                $parsed.Error = "error: invalid-argument duplicate $token"
                continue
            }
            if ($i + 1 -ge $Raw.Count) {
                $parsed.Error = "error: invalid-argument $token missing value"
                continue
            }
            $value = [string] $Raw[++$i]
            if ([string]::IsNullOrWhiteSpace($value) -or (($token -ne "-TimeoutSeconds" -and $token -ne "-PrepTimeoutSeconds") -and $value.StartsWith("-"))) {
                $parsed.Error = "error: invalid-argument $token missing value"
                continue
            }
            $seenSingle[$token] = $true
            if ($token -eq "-TimeoutSeconds") {
                $number = 0
                if (-not [int]::TryParse($value, [ref] $number) -or $number -le 0) {
                    $parsed.Error = "error: invalid-argument -TimeoutSeconds $value"
                    continue
                }
                $parsed.TimeoutSeconds = $number
            }
            elseif ($token -eq "-PrepTimeoutSeconds") {
                $number = 0
                if (-not [int]::TryParse($value, [ref] $number) -or $number -le 0) {
                    $parsed.Error = "error: invalid-argument -PrepTimeoutSeconds $value"
                    continue
                }
                $parsed.PrepTimeoutSeconds = $number
            }
            else {
                $parsed[$single[$token]] = $value
            }
            continue
        }
        $parsed.Error = "error: invalid-argument $token"
    }
    return [pscustomobject] $parsed
}

function Resolve-ExistingPath {
    param([string] $Path)
    if ([System.IO.Path]::IsPathRooted($Path)) { return $Path }
    return [System.IO.Path]::GetFullPath((Join-Path $script:Root $Path))
}

function Copy-IsolatedProject {
    param($Project, [string] $RunDirectory)
    $module = [string] $Project.Module
    $isolationRoot = Join-Path ([IO.Path]::GetTempPath()) ("train-isolation-$([Guid]::NewGuid().ToString('N'))-$module")
    $evidenceRoot = Join-Path $RunDirectory "isolation/$module"
    New-Item -ItemType Directory -Force -Path $isolationRoot | Out-Null
    $script:IsolationRoots.Add($isolationRoot)
    New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
    # 输入复制失败时也保留本次目录身份，供运行结束后核对清理。
    [IO.File]::WriteAllText((Join-Path $evidenceRoot 'root.txt'), $isolationRoot, $script:Utf8)
    $inputs = [System.Collections.Generic.List[object]]::new()
    $boundary = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'checks/project-boundaries.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($null -eq $boundary.projects) { throw [IO.InvalidDataException]::new('missing project-boundaries.json projects') }
    $projectName = "Train.$((Get-Culture).TextInfo.ToTitleCase($module)).Tests"
    $allowed = @(Get-AllowedProjectClosure -Projects $boundary.projects -Name $projectName -IncludeSelf)
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $allowed) {
        $relativeProject = [string] $boundary.projects[$name].path
        [void] $directories.Add(([IO.Path]::GetDirectoryName($relativeProject)).Replace('\', '/'))
    }
    foreach ($relative in @('global.json')) {
        $source = Join-Path $script:Root $relative
        $destination = Join-Path $isolationRoot $relative
        Copy-Item -LiteralPath $source -Destination $destination
        $inputs.Add(@{ path = $relative; sha256 = (Get-Sha256File $source) })
    }
    foreach ($directory in $directories) {
        $sourceRoot = Join-Path $script:Root $directory
        if (-not (Test-Path -LiteralPath $sourceRoot)) { throw [IO.DirectoryNotFoundException]::new("missing isolated input $directory") }
        foreach ($file in @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File)) {
            $relative = [IO.Path]::GetRelativePath($script:Root, $file.FullName).Replace('\', '/')
            if ($relative -match '/(bin|obj|TestResults)/' -or $file.Extension -notin @('.cs', '.csproj', '.py', '.json', '.txt')) { continue }
            $destination = Join-Path $isolationRoot $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            $inputs.Add(@{ path = $relative; sha256 = (Get-Sha256File $file.FullName) })
        }
    }
    # 截断 MSBuild 沿父目录查找，避免导入临时目录上层的配置。
    foreach ($filename in @('Directory.Build.props', 'Directory.Build.targets')) {
        [IO.File]::WriteAllText((Join-Path $isolationRoot $filename), '<Project />', $script:Utf8)
    }
    $inputs | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'inputs.json') -Encoding utf8
    $relativeProject = [IO.Path]::GetRelativePath($script:Root, $Project.Path)
    return [pscustomobject]@{ Root = $isolationRoot; Evidence = $evidenceRoot; Project = (Join-Path $isolationRoot $relativeProject); Allowed = $allowed; Matrix = $boundary }
}

function Remove-IsolatedProjects {
    $errors = [System.Collections.Generic.List[string]]::new()
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $safeLeaf = '^train-isolation-[0-9a-f]{32}-(' + ($script:IsolatedModules -join '|') + ')$'
    foreach ($root in $script:IsolationRoots) {
        $fullPath = [IO.Path]::GetFullPath($root)
        $leaf = [IO.Path]::GetFileName($fullPath.TrimEnd('\', '/'))
        if (-not $fullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $leaf -notmatch $safeLeaf) {
            $errors.Add("refused unsafe isolation path $root")
            continue
        }
        try { Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop }
        catch { $errors.Add("$fullPath : $($_.Exception.Message)") }
    }
    $script:IsolationRoots.Clear()
    return $errors.ToArray()
}

function New-IsolatedReferenceFailure([string] $Reason, [string] $Outcome = 'failed', [string] $LogPath = '') {
    return [pscustomobject]@{ Reason = $Reason; Outcome = $Outcome; LogPath = $LogPath }
}

function New-IsolatedProcessFailure($Run, $Disposition, [string] $Reason, [string] $LogPath) {
    $outcome = if ($Disposition.Kind -eq 'exit') { Get-BuildFailureKind $Run.Output }
        elseif ($Disposition.Kind -in @('timeout', 'cancel')) { 'cancelled' }
        else { 'blocked' }
    return (New-IsolatedReferenceFailure -Reason "${Reason}:$($Disposition.Kind):$($Run.ExitCode)" -Outcome $outcome -LogPath $LogPath)
}

function Test-IsolatedReferences {
    param([string] $ProjectPath, [string] $IsolationRoot, [string] $EvidenceRoot, [string[]] $Allowed, $Matrix, [string] $Dotnet, [string] $TempDirectory, [string] $CancelFile, [int] $TimeoutSeconds)
    $arguments = @('msbuild', $ProjectPath, "-property:Configuration=$($script:Parsed.Configuration)", '-target:ResolveReferences', '-getItem:ProjectReference,ReferencePath,Compile', '-getProperty:TargetPath')
    $log = Join-Path $EvidenceRoot 'references.log'
    $run = Invoke-OwnedProcess -FileName $Dotnet -Arguments $arguments -WorkingDirectory $IsolationRoot -TempDirectory $TempDirectory -TimeoutSeconds $TimeoutSeconds -CancelFile $CancelFile -LogPath $log
    $disposition = Get-ProcessDisposition $run
    if ($disposition.Kind -ne 'exit' -or $run.ExitCode -ne 0) { return (New-IsolatedProcessFailure -Run $run -Disposition $disposition -Reason 'msbuild' -LogPath $log) }
    # MSBuild can append MSB9008 after its JSON when a forbidden project is absent from the isolated copy.
    $jsonStart = $run.Output.IndexOf('{')
    $jsonEnd = $run.Output.LastIndexOf('}')
    try { $resolved = $run.Output.Substring($jsonStart, $jsonEnd - $jsonStart + 1) | ConvertFrom-Json }
    catch { return (New-IsolatedReferenceFailure 'msbuild-output-invalid' -LogPath $log) }
    $inside = $IsolationRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $firstParty = [System.Collections.Generic.List[object]]::new()
    foreach ($item in @($resolved.Items.ProjectReference)) {
        if (-not ([string] $item.FullPath).StartsWith($inside, [StringComparison]::OrdinalIgnoreCase) -or [string] $item.Filename -notin $Allowed) {
            return (New-IsolatedReferenceFailure "project-reference:$($item.FullPath)" -LogPath $log)
        }
        $firstParty.Add(@{ kind = 'project'; name = [string] $item.Filename; path = [string] $item.FullPath })
    }
    foreach ($item in @($resolved.Items.ReferencePath)) {
        $name = [string] $item.Filename
        if ($name -ne 'Train' -and -not $name.StartsWith('Train.', [StringComparison]::Ordinal)) { continue }
        if ($name -notin $Allowed -or -not ([string] $item.FullPath).StartsWith($inside, [StringComparison]::OrdinalIgnoreCase)) {
            return (New-IsolatedReferenceFailure "assembly-reference:$($item.FullPath)" -LogPath $log)
        }
        $firstParty.Add(@{ kind = 'resolved'; name = $name; path = [string] $item.FullPath })
    }
    foreach ($item in @($resolved.Items.Compile)) {
        if (Test-TestSdkCompileSource ([string] $item.FullPath)) { continue }
        if (-not ([string] $item.FullPath).StartsWith($inside, [StringComparison]::OrdinalIgnoreCase)) {
            return (New-IsolatedReferenceFailure "compile-source:$($item.FullPath)" -LogPath $log)
        }
    }
    foreach ($name in $Allowed) {
        $relativeProject = [string] $Matrix.projects[$name].path
        $candidate = Join-Path $IsolationRoot $relativeProject
        if ([IO.Path]::GetFullPath($candidate).Equals([IO.Path]::GetFullPath($ProjectPath), [StringComparison]::OrdinalIgnoreCase)) { continue }
        $sourceArguments = @('msbuild', $candidate, "-property:Configuration=$($script:Parsed.Configuration)", '-getItem:Compile')
        $sourceLog = Join-Path $EvidenceRoot "compile-$name.log"
        $sourceRun = Invoke-OwnedProcess -FileName $Dotnet -Arguments $sourceArguments -WorkingDirectory $IsolationRoot -TempDirectory $TempDirectory -TimeoutSeconds $TimeoutSeconds -CancelFile $CancelFile -LogPath $sourceLog
        $sourceDisposition = Get-ProcessDisposition $sourceRun
        if ($sourceDisposition.Kind -ne 'exit' -or $sourceRun.ExitCode -ne 0) { return (New-IsolatedProcessFailure -Run $sourceRun -Disposition $sourceDisposition -Reason "msbuild-source:$name" -LogPath $sourceLog) }
        $sourceStart = $sourceRun.Output.IndexOf('{')
        $sourceEnd = $sourceRun.Output.LastIndexOf('}')
        try { $source = $sourceRun.Output.Substring($sourceStart, $sourceEnd - $sourceStart + 1) | ConvertFrom-Json }
        catch { return (New-IsolatedReferenceFailure "msbuild-source-output-invalid:$name" -LogPath $sourceLog) }
        foreach ($item in @($source.Items.Compile)) {
            if (-not ([string] $item.FullPath).StartsWith($inside, [StringComparison]::OrdinalIgnoreCase)) {
                return (New-IsolatedReferenceFailure "compile-source:${name}:$($item.FullPath)" -LogPath $sourceLog)
            }
        }
        $firstParty.Add(@{ kind = 'source-evaluation'; name = $name; path = $candidate })
    }
    $assembly = Get-ProjectAssemblyPath -ProjectPath $ProjectPath -Configuration $script:Parsed.Configuration
    $outputDirectory = Split-Path -Parent $assembly
    foreach ($file in @(Get-ChildItem -LiteralPath $outputDirectory -Filter 'Train*.dll' -File)) {
        if ($file.BaseName -ne 'Train' -and -not $file.BaseName.StartsWith('Train.', [StringComparison]::Ordinal)) { continue }
        if ($file.BaseName -notin $Allowed) { return (New-IsolatedReferenceFailure "output-assembly:$($file.FullName)" -LogPath $log) }
        $stream = [IO.File]::OpenRead($file.FullName)
        try {
            $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
            try {
                $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
                foreach ($referenceName in @(Get-AssemblyReferenceNames $metadata)) {
                    if (($referenceName -eq 'Train' -or $referenceName.StartsWith('Train.', [StringComparison]::Ordinal)) -and $referenceName -notin $Allowed) {
                        return (New-IsolatedReferenceFailure "metadata-reference:$($file.Name):$referenceName" -LogPath $log)
                    }
                }
            }
            finally { $pe.Dispose() }
        }
        finally { $stream.Dispose() }
        $firstParty.Add(@{ kind = 'output'; name = $file.BaseName; path = $file.FullName })
    }
    $firstParty | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'references.json') -Encoding utf8
    return $null
}

function Get-XmlText {
    param($Node, [string] $Name)
    if ($null -eq $Node) { return "" }
    $child = $Node.SelectSingleNode($Name)
    if ($null -eq $child -or $null -eq $child.InnerText) { return "" }
    return $child.InnerText.Trim()
}

function Get-XmlAttr {
    param($Node, [string] $Name)
    if ($null -eq $Node -or $null -eq $Node.Attributes) { return "" }
    $attribute = $Node.Attributes.GetNamedItem($Name)
    if ($null -eq $attribute) { return "" }
    return [string] $attribute.Value
}

function Get-ProjectAssemblyPath {
    param([string] $ProjectPath, [string] $Configuration)
    [xml] $xml = Get-Content -LiteralPath $ProjectPath -Raw
    $tfm = ""
    $assembly = ""
    foreach ($group in @($xml.SelectNodes("/Project/PropertyGroup"))) {
        $framework = Get-XmlText $group "TargetFramework"
        $name = Get-XmlText $group "AssemblyName"
        if ($framework) { $tfm = $framework }
        if ($name) { $assembly = $name }
    }
    if (-not $assembly) { $assembly = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath) }
    if (-not $tfm) { throw "error: discovery project has no TargetFramework: $ProjectPath" }
    $dir = Split-Path -Parent $ProjectPath
    return Join-Path $dir (Join-Path "bin" (Join-Path $Configuration (Join-Path $tfm ($assembly + ".dll"))))
}

function Get-GodotGameAssemblyPath {
    param([string] $Configuration)
    # Godot.NET.Sdk writes the game assembly here. bin/<cfg>/<tfm>/Train.dll is not produced.
    return Join-Path $script:Root ".godot/mono/temp/bin/$Configuration/Train.dll"
}

function Ensure-MetadataReader {
    if (-not ("TestMetadataReader" -as [type])) {
        Add-Type -Path $script:ReaderPath -ReferencedAssemblies @(
            "System.Reflection.Metadata.dll",
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Private.CoreLib.dll",
            "System.Collections.Immutable.dll"
        )
    }
}

function Read-TestCatalog {
    param([string] $AssemblyPath)
    Ensure-MetadataReader
    return [TestMetadataReader]::Read($AssemblyPath)
}

function Format-CaseLine {
    param($Case)
    $scopes = ($Case.Scopes -join ",")
    $requirements = "none"
    if (@($Case.Requirements).Count -gt 0) { $requirements = ($Case.Requirements -join ",") }
    $line = "CASE id=$($Case.Id) family=$($Case.Family) scopes=$scopes requirements=$requirements group=$($Case.Group) module=$($Case.Module)"
    if ($Case.Claim) { $line += " claim=$($Case.Claim)" }
    return $line
}

function Get-GroupSummary {
    param($Cases)
    $lines = [System.Collections.Generic.List[string]]::new()
    $familyCounts = @{}
    $scopeCounts = @{}
    foreach ($case in @($Cases)) {
        if (-not $familyCounts.ContainsKey($case.Family)) { $familyCounts[$case.Family] = 0 }
        $familyCounts[$case.Family]++
        foreach ($scope in @($case.Scopes)) {
            if (-not $scopeCounts.ContainsKey($scope)) { $scopeCounts[$scope] = 0 }
            $scopeCounts[$scope]++
        }
    }
    foreach ($name in @($familyCounts.Keys | Sort-Object)) {
        $lines.Add("GROUP family=$name count=$($familyCounts[$name])")
    }
    foreach ($name in @($scopeCounts.Keys | Sort-Object)) {
        $lines.Add("GROUP scope=$name count=$($scopeCounts[$name])")
    }
    $registered = [System.Collections.Generic.List[string]]::new()
    foreach ($case in @($Cases)) {
        if ($case.Kind -eq "external") { $registered.Add([string] $case.Id) }
    }
    if ($registered.Count -gt 0) {
        $registered.Sort([System.StringComparer]::Ordinal)
        $lines.Add("REGISTERED " + ($registered -join " "))
    }
    $lines.Add("PENDING benchmark")
    $lines.Add("PENDING long")
    $lines.Add("PENDING manual")
    $lines.Add("PENDING platform")
    return ($lines -join [Environment]::NewLine)
}

function Convert-Catalog {
    param($Catalog, [string] $ProjectPath, [string] $Module)
    $cases = [System.Collections.Generic.List[object]]::new()
    foreach ($item in @($Catalog.Cases)) {
        $scopes = [System.Collections.Generic.List[string]]::new()
        foreach ($scope in $item.Scopes) { [void] $scopes.Add([string] $scope) }
        $requirements = [System.Collections.Generic.List[string]]::new()
        foreach ($requirement in $item.Requirements) { [void] $requirements.Add([string] $requirement) }
        $group = ""
        if ($scopes.Count -gt 0) { $group = [string] $scopes[0] }
        $caseModule = $Module
        if ($Module -eq 'contract-support' -and [string] $item.Id -match '^Train\.ContractSupport\.Tests\.(Core|Content|Scenes)\.') {
            $caseModule = $Matches[1].ToLowerInvariant()
        }
        $cases.Add([pscustomobject]@{
            Id = [string] $item.Id
            Family = [string] $item.Family
            Scopes = $scopes
            Requirements = $requirements
            Group = $group
            Kind = "mstest"
            Project = $ProjectPath
            Module = $caseModule
            Claim = ""
            Script = ""
            Arguments = [System.Collections.Generic.List[string]]::new()
            NeedsImport = $false
            NeedsGameBuild = $false
            NeedsExport = $false
            NeedsContentInput = $false
            TimeoutSeconds = 0
        })
    }
    $errors = [System.Collections.Generic.List[string]]::new()
    foreach ($errorItem in @($Catalog.Errors)) { $errors.Add([string] $errorItem) }
    return [pscustomobject]@{ Cases = $cases; Errors = $errors }
}

function Test-PendingFamily {
    param([string] $Name)
    return @("benchmark", "long", "manual", "platform") -contains $Name
}

function Select-Cases {
    param($Parsed, $Cases)
    $knownFamilies = @{}
    $knownScopes = @{}
    $byId = @{}
    $byMethod = @{}
    foreach ($case in @($Cases)) {
        $knownFamilies[$case.Family] = $true
        $byId[$case.Id] = $case
        $method = $case.Id.Substring($case.Id.LastIndexOf(".") + 1)
        if (-not $byMethod.ContainsKey($method)) { $byMethod[$method] = [System.Collections.Generic.List[object]]::new() }
        $byMethod[$method].Add($case)
        foreach ($scope in @($case.Scopes)) { $knownScopes[$scope] = $true }
    }

    foreach ($family in @($Parsed.Families)) {
        if (Test-PendingFamily $family) {
            return [pscustomobject]@{ Error = "error: pending $family is not a runnable group"; ExitCode = 2; Cases = @() }
        }
        if (-not $knownFamilies.ContainsKey($family)) {
            return [pscustomobject]@{ Error = "error: unknown-name family $family"; ExitCode = 4; Cases = @() }
        }
    }
    foreach ($scope in @($Parsed.Scopes)) {
        if (-not $knownScopes.ContainsKey($scope)) {
            return [pscustomobject]@{ Error = "error: unknown-name scope $scope"; ExitCode = 4; Cases = @() }
        }
    }

    $testCases = [System.Collections.Generic.List[object]]::new()
    foreach ($requested in @($Parsed.Tests)) {
        if ($byId.ContainsKey($requested)) {
            $testCases.Add($byId[$requested])
            continue
        }
        if ($byMethod.ContainsKey($requested)) {
            $matches = @($byMethod[$requested])
            if ($matches.Count -ne 1) {
                return [pscustomobject]@{ Error = "error: unknown-name ambiguous test $requested"; ExitCode = 4; Cases = @() }
            }
            $testCases.Add($matches[0])
            continue
        }
        return [pscustomobject]@{ Error = "error: unknown-name test $requested"; ExitCode = 4; Cases = @() }
    }

    $selected = [System.Collections.Generic.List[object]]::new()
    $seen = @{}
    foreach ($case in @($Cases)) {
        if ($Parsed.Correctness -and $case.Family -ne "unit" -and $case.Family -ne "headless") { continue }
        if ($Parsed.Correctness -and @($case.Requirements) -contains "export") { continue }
        if ($Parsed.Families.Count -gt 0 -and -not ($Parsed.Families -contains $case.Family)) { continue }
        if ($Parsed.Scopes.Count -gt 0) {
            $scopeHit = $false
            foreach ($scope in @($case.Scopes)) {
                if ($Parsed.Scopes -contains $scope) { $scopeHit = $true }
            }
            if (-not $scopeHit) { continue }
        }
        if ($Parsed.Tests.Count -gt 0) {
            $testHit = $false
            foreach ($picked in @($testCases)) {
                if ($picked.Id -eq $case.Id) { $testHit = $true }
            }
            if (-not $testHit) { continue }
        }
        if ($seen.ContainsKey($case.Id)) { continue }
        $seen[$case.Id] = $true
        $selected.Add($case)
    }

    if ($selected.Count -eq 0) {
        return [pscustomobject]@{ Error = "error: zero-match"; ExitCode = 4; Cases = @() }
    }
    return [pscustomobject]@{ Error = ""; ExitCode = 0; Cases = $selected.ToArray() }
}

function New-MissingGitState {
    param([string] $Reason)
    return [pscustomobject]@{
        Commit = "unknown"
        Dirty = $true
        Paths = (New-List)
        IdentityKnown = $false
        IdentityReason = $Reason
    }
}

function Get-GitState {
    $commit = "unknown"
    $dirty = $true
    $paths = New-List
    $known = $false
    $reason = "无法读取 Git 状态"
    try {
        $commitText = (& git -C $script:Root rev-parse HEAD 2>$null | Out-String).Trim()
        $revCode = $LASTEXITCODE
        $revOk = ($revCode -eq 0 -and $commitText)
        if ($revOk) { $commit = $commitText }
        else { $reason = "git rev-parse exit $revCode" }
        $statusLines = @(& git -C $script:Root status --porcelain 2>&1)
        $statusCode = $LASTEXITCODE
        if ($statusCode -ne 0) {
            $detail = ((@($statusLines) | ForEach-Object { [string] $_ }) -join " ").Trim()
            if ($detail.Length -gt 240) { $detail = $detail.Substring(0, 240) }
            $statusReason = "git status exit $statusCode"
            if ($detail) { $statusReason = "$statusReason $detail" }
            if (-not $revOk) { $reason = "$reason; $statusReason" }
            else { $reason = $statusReason }
        }
        elseif (-not $revOk) {
            $dirty = $true
        }
        else {
            $known = $true
            $reason = ""
            $text = ((@($statusLines) | ForEach-Object { [string] $_ }) -join [Environment]::NewLine)
            $dirty = -not [string]::IsNullOrWhiteSpace($text)
            if ($dirty) {
                foreach ($line in @($statusLines)) {
                    if (-not [string]::IsNullOrWhiteSpace([string] $line)) { $paths.Add(([string] $line).Trim()) }
                }
            }
        }
    }
    catch {
        $commit = "unknown"
        $dirty = $true
        $known = $false
        $reason = [string] $_.Exception.Message
    }
    # An unreadable snapshot is not a known-clean tree. Dirty stays true so it cannot be
    # reported as clean; IdentityKnown says this was not an observation of edits.
    return [pscustomobject]@{
        Commit = $commit
        Dirty = [bool] $dirty
        Paths = $paths
        IdentityKnown = [bool] $known
        IdentityReason = $reason
    }
}

function Test-GitChanged($Start, $End) {
    if ([string] $Start.Commit -ne [string] $End.Commit) { return $true }
    if ([bool] $Start.Dirty -ne [bool] $End.Dirty) { return $true }
    $left = [System.Collections.Generic.List[string]]::new()
    $right = [System.Collections.Generic.List[string]]::new()
    foreach ($item in @($Start.Paths)) {
        if (-not [string]::IsNullOrWhiteSpace([string] $item)) { $left.Add(([string] $item).Trim()) }
    }
    foreach ($item in @($End.Paths)) {
        if (-not [string]::IsNullOrWhiteSpace([string] $item)) { $right.Add(([string] $item).Trim()) }
    }
    $left.Sort([System.StringComparer]::Ordinal)
    $right.Sort([System.StringComparer]::Ordinal)
    if ($left.Count -ne $right.Count) { return $true }
    for ($i = 0; $i -lt $left.Count; $i++) {
        if ($left[$i] -ne $right[$i]) { return $true }
    }
    return $false
}

function Get-DotnetVersion {
    param([string] $DotnetPath)
    $version = (& $DotnetPath --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { return "" }
    return ($version -split "\r?\n" | Select-Object -Last 1).Trim()
}

function Test-RequirementAvailable {
    param([string] $Requirement)
    switch ($Requirement) {
        "godot" { return [bool] ($env:GODOT -and (Test-Path -LiteralPath $env:GODOT)) }
        "process" { return $true }
        "export" { return [bool] ($env:GODOT -and (Test-Path -LiteralPath $env:GODOT)) }
        "python" { return $null -ne (Get-Command python -ErrorAction SilentlyContinue) }
        "jsonschema" {
            if (-not (Get-Command python -ErrorAction SilentlyContinue)) { return $false }
            & python -c 'import jsonschema, referencing' *> $null
            return $LASTEXITCODE -eq 0
        }
        default { return $false }
    }
}

function Get-GroupTimeoutSeconds {
    param([int] $CaseCount, [int] $Override)
    if ($Override -gt 0) { return $Override }
    # 沿用编排层的保守等待预算：预留启动时间，随用例数增加，并限制在 90 至 900 秒。
    # 该预算用于结束卡住的进程，不是测试耗时或性能基线。
    $seconds = 45 + (20 * $CaseCount)
    return [Math]::Min(900, [Math]::Max(90, $seconds))
}

function Initialize-ChildProcess {
    param(
        [string] $FileName,
        [string[]] $Arguments,
        [string] $WorkingDirectory,
        [string] $TempDirectory,
        [switch] $StripTrainSwitches,
        [string] $AppDataDirectory = ""
    )
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FileName
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    foreach ($argument in @($Arguments)) { [void] $info.ArgumentList.Add([string] $argument) }
    $strip = [System.Collections.Generic.List[string]]::new()
    foreach ($key in @($info.Environment.Keys)) {
        $name = [string] $key
        if ($StripTrainSwitches -and $name -like "TRAIN_*") { $strip.Add($name) }
        elseif ($name -like "TRAIN_RUN_TESTS_*") { $strip.Add($name) }
    }
    foreach ($key in $strip) { [void] $info.Environment.Remove($key) }
    $info.Environment["TEMP"] = $TempDirectory
    $info.Environment["TMP"] = $TempDirectory
    $info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    $info.Environment["DOTNET_NOLOGO"] = "1"
    $info.Environment["MSBUILDDISABLENODEREUSE"] = "1"
    $info.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1"
    $info.Environment["UseSharedCompilation"] = "false"
    if ($AppDataDirectory) {
        $info.Environment["APPDATA"] = $AppDataDirectory
        $info.Environment["LOCALAPPDATA"] = $AppDataDirectory
    }
    return $info
}

function Stop-OwnedProcessTree {
    param([int] $ProcessId, [switch] $PretendFailure)
    if ($PretendFailure) { return $false }
    if ($ProcessId -le 0) { return $true }
    if ($IsWindows) {
        & taskkill.exe /PID $ProcessId /T /F 1>$null 2>$null
    }
    else {
        & kill -- "-$ProcessId" 1>$null 2>$null
        & kill $ProcessId 1>$null 2>$null
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    return -not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
}

function Get-OutcomeRank([string] $Outcome) {
    switch ($Outcome) {
        "passed" { return 0 }
        "unknown" { return 0 }
        "not-executed" { return 1 }
        "cancelled" { return 2 }
        "blocked" { return 3 }
        "failed" { return 4 }
        default { return 4 }
    }
}

function Get-ProcessDisposition($Run) {
    # StopDispatch is only user cancel or a cleanup failure. A timeout whose process
    # was cleaned up does not stop independent groups; callers still block dependents.
    # For every non-exit result, Kind is the reason and dependents are blocked.
    if ($Run.CleanupFailed) {
        return [pscustomobject]@{ Kind = "cleanup"; Outcome = "blocked"; StopDispatch = $true }
    }
    if ($Run.TimedOut) {
        return [pscustomobject]@{ Kind = "timeout"; Outcome = "cancelled"; StopDispatch = $false }
    }
    if ($Run.Cancelled -or (Test-CancelRequested)) {
        return [pscustomobject]@{ Kind = "cancel"; Outcome = "cancelled"; StopDispatch = $true }
    }
    return [pscustomobject]@{ Kind = "exit"; Outcome = ""; StopDispatch = $false }
}

function New-DispatchState {
    return [pscustomobject]@{
        StopDispatch = $false
        StopReason = ""
        UserCancel = $false
        CleanupFailed = $false
        PrepTimedOut = $false
    }
}

function Apply-ProcessDisposition($Disposition, $State, [switch] $PrepStep) {
    if ($Disposition.Kind -eq "cleanup") { $State.CleanupFailed = $true }
    if ($Disposition.Kind -eq "cancel") { $State.UserCancel = $true }
    if ($PrepStep -and $Disposition.Kind -eq "timeout") { $State.PrepTimedOut = $true }
    if ($Disposition.StopDispatch) {
        $State.StopDispatch = $true
        $State.StopReason = [string] $Disposition.Kind
    }
}

function Get-RunDurationStatus {
    param([string] $Outcome, [int] $DurationMs)
    if ($Outcome -eq "passed" -or $Outcome -eq "failed") { return "measured-this-run" }
    if (($Outcome -eq "cancelled" -or $Outcome -eq "blocked") -and $DurationMs -gt 0) { return "partial" }
    return "unmeasured"
}

function Merge-ResultEntry($Previous, $Incoming) {
    $duration = [int] $Previous.DurationMs + [int] $Incoming.DurationMs
    if ((Get-OutcomeRank ([string] $Incoming.Outcome)) -gt (Get-OutcomeRank ([string] $Previous.Outcome))) {
        return [pscustomobject]@{ Outcome = [string] $Incoming.Outcome; DurationMs = $duration; Native = [string] $Incoming.Native }
    }
    return [pscustomobject]@{ Outcome = [string] $Previous.Outcome; DurationMs = $duration; Native = [string] $Previous.Native }
}

function Invoke-OwnedProcess {
    param(
        [string] $FileName,
        [string[]] $Arguments,
        [string] $WorkingDirectory,
        [string] $TempDirectory,
        [int] $TimeoutSeconds,
        [string] $CancelFile,
        [string] $LogPath,
        [switch] $StripTrainSwitches,
        [string] $AppDataDirectory = ""
    )
    $info = Initialize-ChildProcess -FileName $FileName -Arguments $Arguments -WorkingDirectory $WorkingDirectory -TempDirectory $TempDirectory -StripTrainSwitches:$StripTrainSwitches -AppDataDirectory $AppDataDirectory
    if (Test-CancelRequested) {
        return [pscustomobject]@{
            ExitCode = -1
            TimedOut = $false
            Cancelled = $true
            CleanupFailed = $false
            Output = ""
        }
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    [void] $process.Start()
    if ($AppDataDirectory -and $FileName -eq $env:GODOT) { $script:PerCheckAppDataUsed = $true }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $script:OwnedPid = $process.Id
    $timedOut = $false
    $cancelled = $false
    $started = [DateTime]::UtcNow
    while (-not $process.HasExited) {
        if ((Test-CancelRequested) -or ($CancelFile -and (Test-Path -LiteralPath $CancelFile))) {
            $cancelled = $true
            break
        }
        if (([DateTime]::UtcNow - $started).TotalSeconds -ge $TimeoutSeconds) {
            $timedOut = $true
            break
        }
        Start-Sleep -Milliseconds 200
    }
    $cleanupFailed = $false
    if ($timedOut -or $cancelled) {
        $pretend = [bool] $script:TestHostPath -and $env:TRAIN_RUN_TESTS_FAULT -eq "cleanup-kill"
        if ($pretend -and $env:TRAIN_RUN_TESTS_FAULT_COMMAND) {
            $commandText = "$FileName $($Arguments -join ' ')"
            if ($commandText -notlike "*$($env:TRAIN_RUN_TESTS_FAULT_COMMAND)*") { $pretend = $false }
        }
        $killed = Stop-OwnedProcessTree -ProcessId $process.Id -PretendFailure:$pretend
        if (-not $killed) { $cleanupFailed = $true }
        if (-not $process.HasExited) { [void] $process.WaitForExit(5000) }
    }
    else {
        $process.WaitForExit()
    }
    $script:OwnedPid = 0
    $stdoutText = ""
    $stderrText = ""
    if ($process.HasExited) {
        if ($stdoutTask.Wait(15000)) { $stdoutText = [string] $stdoutTask.Result }
        if ($stderrTask.Wait(15000)) { $stderrText = [string] $stderrTask.Result }
    }
    $text = (@($stdoutText, $stderrText) | Where-Object { $_ }) -join [Environment]::NewLine
    if ($LogPath) {
        $header = "command: $FileName $($Arguments -join ' ')"
        Set-Content -LiteralPath $LogPath -Value ($header + [Environment]::NewLine + $text) -Encoding utf8
    }
    $exitCode = -1
    if ($process.HasExited) { $exitCode = $process.ExitCode }
    $process.Dispose()
    return [pscustomobject]@{
        ExitCode = $exitCode
        TimedOut = [bool] $timedOut
        Cancelled = [bool] $cancelled
        CleanupFailed = [bool] $cleanupFailed
        Output = $text
    }
}

function Read-TrxResults {
    param([string] $TrxPath)
    $results = @{}
    if (-not (Test-Path -LiteralPath $TrxPath)) { return ,$results }
    [xml] $xml = Get-Content -LiteralPath $TrxPath -Raw
    $namespaceUri = $xml.DocumentElement.NamespaceURI
    $manager = [System.Xml.XmlNamespaceManager]::new($xml.NameTable)
    $manager.AddNamespace("t", $namespaceUri)
    $definitions = @{}
    foreach ($unit in @($xml.SelectNodes("//t:TestDefinitions/t:UnitTest", $manager))) {
        $method = $unit.SelectSingleNode("./t:TestMethod", $manager)
        if (-not $method) { continue }
        $className = Get-XmlAttr $method "className"
        if ($className.Contains(",")) { $className = $className.Split(",")[0].Trim() }
        $definitions[(Get-XmlAttr $unit "id")] = "$className.$(Get-XmlAttr $method "name")"
    }
    foreach ($result in @($xml.SelectNodes("//t:Results/t:UnitTestResult", $manager))) {
        $id = $definitions[(Get-XmlAttr $result "testId")]
        if (-not $id) { $id = Get-XmlAttr $result "testName" }
        $outcome = Get-XmlAttr $result "outcome"
        $mapped = switch ($outcome) {
            "Passed" { "passed" }
            "Failed" { "failed" }
            "NotExecuted" { "not-executed" }
            "Inconclusive" { "blocked" }
            "Timeout" { "cancelled" }
            default { "failed" }
        }
        $durationMs = 0
        $parsed = [TimeSpan]::Zero
        if ([TimeSpan]::TryParse((Get-XmlAttr $result "duration"), [ref] $parsed)) {
            $durationMs = [int] [Math]::Round($parsed.TotalMilliseconds)
        }
        $entry = [pscustomobject]@{ Outcome = $mapped; DurationMs = $durationMs; Native = $outcome }
        if ($results.ContainsKey($id)) { $results[$id] = Merge-ResultEntry $results[$id] $entry }
        else { $results[$id] = $entry }
    }
    return ,$results
}

function ConvertTo-JsonValue($Value) {
    if ($null -eq $Value) { return $null }
    $base = $Value.psobject.BaseObject
    if ($null -ne $base -and -not [object]::ReferenceEquals($base, $Value)) { $Value = $base }
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) { return [string] $Value }
    if ($Value -is [bool] -or $Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) { return $Value }
    if ($Value -is [System.Collections.IDictionary]) {
        $map = [System.Collections.Generic.Dictionary[string, object]]::new()
        foreach ($key in @($Value.Keys)) { $map[[string] $key] = ConvertTo-JsonValue $Value[$key] }
        return ,$map
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $list = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $Value) { [void] $list.Add((ConvertTo-JsonValue $item)) }
        return ,$list
    }
    return [string] $Value
}

function Write-RunReports {
    param($State)
    $summaryPath = Join-Path $State.Directory "summary.json"
    $reportPath = Join-Path $State.Directory "report.md"
    $json = [System.Text.Json.JsonSerializer]::Serialize((ConvertTo-JsonValue $State.Summary), $State.JsonOptions)
    [System.IO.File]::WriteAllText($summaryPath, $json)
    [System.IO.File]::WriteAllText($reportPath, $State.Markdown)
    return $summaryPath
}

function Get-RegisteredRunnerNames {
    $names = New-List
    $names.Add("mstest")
    $godot = $false
    $process = $false
    $catalog = $script:ExternalCatalog
    if ($script:RunnerCatalogMode -ne "none" -and $null -ne $catalog -and $null -ne $catalog.checks) {
        foreach ($item in @($catalog.checks)) {
            foreach ($requirement in @($item.requirements)) {
                if ([string] $requirement -eq "godot") { $godot = $true }
                if ([string] $requirement -eq "process") { $process = $true }
            }
        }
    }
    if ($godot) { $names.Add("godot") }
    if ($process) { $names.Add("cross-process") }
    return ,$names
}

function New-Summary {
    param($Parsed, $Git, $Ending, $Environment, $Selection, $Checks, $Groups, $Preparation, [int] $ExitCode, [string] $Directory)
    $counts = @{ passed = 0; failed = 0; blocked = 0; cancelled = 0; "not-executed" = 0 }
    foreach ($check in @($Checks)) {
        $outcome = [string] $check["outcome"]
        if ($counts.ContainsKey($outcome)) { $counts[$outcome]++ }
    }
    if ($null -eq $Ending) { $Ending = New-MissingGitState "运行结束时没有记录 Git 身份" }
    $startKnown = [bool] $Git.IdentityKnown
    $endKnown = [bool] $Ending.IdentityKnown
    if (-not $startKnown -or -not $endKnown) { $changed = $false }
    else { $changed = Test-GitChanged $Git $Ending }
    $proven = $startKnown -and $endKnown -and (-not [bool] $Git.Dirty) -and (-not [bool] $Ending.Dirty) -and (-not $changed) -and ([string] $Git.Commit -ne "unknown")
    $summary = New-Map
    Add-Map $summary "schemaVersion" 1 | Out-Null
    Add-Map $summary "includesAllRepositoryChecks" $false | Out-Null
    Add-Map $summary "cleanCommitProven" $proven | Out-Null
    Add-Map $summary "commit" $Git.Commit | Out-Null
    Add-Map $summary "dirty" ([bool] $Git.Dirty) | Out-Null
    Add-Map $summary "dirtyPaths" $Git.Paths | Out-Null
    Add-Map $summary "identityKnown" $startKnown | Out-Null
    Add-Map $summary "identityReason" ([string] $Git.IdentityReason) | Out-Null
    Add-Map $summary "testedIdentityChanged" $changed | Out-Null
    $endingMap = New-Map
    Add-Map $endingMap "commit" $Ending.Commit | Out-Null
    Add-Map $endingMap "dirty" ([bool] $Ending.Dirty) | Out-Null
    Add-Map $endingMap "dirtyPaths" $Ending.Paths | Out-Null
    Add-Map $endingMap "identityKnown" $endKnown | Out-Null
    Add-Map $endingMap "identityReason" ([string] $Ending.IdentityReason) | Out-Null
    Add-Map $summary "gitAtEnd" $endingMap | Out-Null
    Add-Map $summary "testAssemblyBoundary" $script:TestAssemblyBoundary | Out-Null
    $gaps = New-List
    foreach ($gap in @("benchmark", "long", "platform", "scenes-module-tests", "godot-isolated-composition", "real-factory-call-locations")) { $gaps.Add($gap) }
    Add-Map $summary "migrationGaps" $gaps | Out-Null
    $runners = Get-RegisteredRunnerNames
    Add-Map $summary "registeredRunners" $runners | Out-Null
    Add-Map $summary "runnerCatalog" $script:RunnerCatalogMode | Out-Null
    Add-Map $summary "scheduling" "serial" | Out-Null
    Add-Map $summary "migrationFeedback" $script:MigrationFeedback | Out-Null
    Add-Map $summary "inventory" (New-InventoryMaps $Checks) | Out-Null
    Add-Map $summary "selection" $Selection | Out-Null
    Add-Map $summary "environment" $Environment | Out-Null
    Add-Map $summary "preparation" $Preparation | Out-Null
    Add-Map $summary "groups" $Groups | Out-Null
    Add-Map $summary "checks" $Checks | Out-Null
    $countMap = New-Map
    Add-Map $countMap "passed" $counts.passed | Out-Null
    Add-Map $countMap "failed" $counts.failed | Out-Null
    Add-Map $countMap "blocked" $counts.blocked | Out-Null
    Add-Map $countMap "cancelled" $counts.cancelled | Out-Null
    Add-Map $countMap "notExecuted" $counts["not-executed"] | Out-Null
    Add-Map $summary "outcomes" $countMap | Out-Null
    $manual = New-List
    Add-Map $summary "manual" $manual | Out-Null
    Add-Map $summary "exitCode" $ExitCode | Out-Null
    $artifacts = New-Map
    Add-Map $artifacts "summary" (Join-Path $Directory "summary.json") | Out-Null
    Add-Map $artifacts "report" (Join-Path $Directory "report.md") | Out-Null
    Add-Map $artifacts "directory" $Directory | Out-Null
    Add-Map $summary "artifacts" $artifacts | Out-Null
    return ,$summary
}

function New-Markdown {
    param($Summary)
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# 测试运行")
    $lines.Add("")
    $ending = $Summary["gitAtEnd"]
    if (-not $Summary["identityKnown"] -or -not $ending["identityKnown"]) {
        $startReason = if ($Summary["identityKnown"]) { "已记录提交 $($Summary["commit"])" } else { [string] $Summary["identityReason"] }
        $endReason = if ($ending["identityKnown"]) { "已记录提交 $($ending["commit"])" } else { [string] $ending["identityReason"] }
        $lines.Add("Git 身份未验证。构建前：$startReason。运行结束时：$endReason。该结果不能证明干净提交已通过。")
    }
    elseif ($Summary["testedIdentityChanged"]) {
        $startText = if ($Summary["dirty"]) { "有未提交修改" } else { "干净" }
        $endText = if ($ending["dirty"]) { "有未提交修改" } else { "干净" }
        $lines.Add("构建前是提交 $($Summary["commit"])，工作区$startText。运行结束时是提交 $($ending["commit"])，工作区$endText。结束时的提交和工作区不是本次已验证的代码。")
    }
    elseif ($Summary["dirty"]) {
        $lines.Add("工作区有未提交修改。该结果不能证明提交 $($Summary["commit"]) 的干净树已通过。")
    }
    else {
        $lines.Add("工作区干净。提交 $($Summary["commit"])。")
    }
    $lines.Add("")
    $lines.Add($Summary["testAssemblyBoundary"])
    $lines.Add("待验证：$(@($Summary["migrationGaps"]) -join '、')。这些项目不计入通过数。")
    $registeredNames = @($Summary["registeredRunners"] | ForEach-Object { [string] $_ })
    $registeredParts = [System.Collections.Generic.List[string]]::new()
    if ($registeredNames -contains "mstest") { $registeredParts.Add("MSTest") }
    if ($registeredNames -contains "godot") { $registeredParts.Add("Godot 检查") }
    if ($registeredNames -contains "cross-process") { $registeredParts.Add("新进程检查") }
    $registeredText = if ($registeredParts.Count -gt 0) { $registeredParts -join "、" } else { "没有外部目录里的可运行检查" }
    $lines.Add("已登记 $registeredText。支持脚本清单来自仓库目录。这仍不是全仓验证。Windows CI 说明见 docs/ci.md。Benchmark、长测、人工和额外平台不是会空跑通过的组。")
    $lines.Add("引擎观察、新进程恢复、领域对照和人工观感分开记录。TRX 和引擎日志保留原始文件。启动或截图成功不代替这些检查。")
    $lines.Add("需要引擎或共享资源的检查串行执行。隔离策略是 per-check-appdata：每个 Godot 检查在本次运行目录里有一份 APPDATA，导入和版本探测另外各有一份。同一次检查里的多个 Godot 进程共用这一份。入口不写项目根目录的 override.cfg。强制结束进程也不会留下用户目录重定向。environment 只在本次确实启动了使用该目录的 Godot 进程时写成 per-check-appdata，否则写成 not-started。")
    $lines.Add("")
    $selection = $Summary["selection"]
    if ($selection) {
        $lines.Add("## 选择")
        $lines.Add("- 模块 $(Join-ReportItems $selection["modules"])")
        $lines.Add("- 类别 $(Join-ReportItems $selection["families"])")
        $lines.Add("- 范围 $(Join-ReportItems $selection["scopes"])")
        $lines.Add("- 用例 $(Join-ReportItems $selection["tests"])")
        $lines.Add("- 完整正确性 $($selection["correctness"])")
        $lines.Add("")
    }
    $preparation = $Summary["preparation"]
    if ($preparation) {
        $lines.Add("## 准备")
        $lines.Add("- $($preparation["outcome"]) $($preparation["durationMs"])ms $($preparation["reason"])")
        if ($preparation.ContainsKey("steps")) {
            foreach ($step in @($preparation["steps"])) {
                $lines.Add("- $($step["name"]) $($step["outcome"]) $($step["reason"]) $($step["durationMs"])ms")
            }
        }
        $lines.Add("")
    }
    $environment = $Summary["environment"]
    if ($environment) {
        $lines.Add("## 环境")
        $lines.Add("- sdk $($environment["dotnetSdk"])")
        $lines.Add("- os $($environment["os"])")
        $lines.Add("- powershell $($environment["powershell"])")
        $lines.Add("- godot $($environment["godot"])")
        if ($environment.ContainsKey("godotVersion") -and $environment["godotVersion"]) { $lines.Add("- godotVersion $($environment["godotVersion"])") }
        if ($environment.ContainsKey("contentVersion")) { $lines.Add("- content $($environment["contentVersion"]) $($environment["contentSha256"])") }
        if ($environment.ContainsKey("gameAssemblyReason")) {
            $assemblySha = if ($environment.ContainsKey("gameAssemblySha256") -and $environment["gameAssemblySha256"]) { [string] $environment["gameAssemblySha256"] } else { "none" }
            $lines.Add("- gameAssembly $assemblySha $($environment["gameAssemblyReason"])")
        }
        elseif ($environment.ContainsKey("gameAssemblySha256") -and $environment["gameAssemblySha256"]) { $lines.Add("- gameAssembly $($environment["gameAssemblySha256"])") }
        if ($environment.ContainsKey("projects")) {
            foreach ($project in @($environment["projects"])) {
                $lines.Add("- testAssembly $($project.module) $($project.sha256) $($project.assembly) source=$($project.sourceProject)")
            }
        }
        if ($environment.ContainsKey("isolationCleanupErrors")) { $lines.Add("隔离目录清理失败，保留路径见 isolation/cleanup.log。") }
        elseif ($environment.ContainsKey("projects") -and @($environment["projects"] | Where-Object { $_.isolationRoot }).Count -gt 0) {
            $lines.Add("隔离程序集路径用于标识本次构建；运行结束后临时目录会被清理。")
        }
        if ($environment.ContainsKey("gameUserDataUnchanged")) { $lines.Add("- gameUserDataUnchanged $($environment["gameUserDataUnchanged"])") }
        if ($environment.ContainsKey("engineIsolation") -and $environment["engineIsolation"]) { $lines.Add("- engineIsolation $($environment["engineIsolation"])") }
        if ($environment.ContainsKey("overrideCfgPresent")) {
            if ([bool] $environment["overrideCfgPresent"]) {
                $lines.Add("- overrideCfg present=true sha256=$($environment["overrideCfgSha256"])。身份在 Godot 启动前读取一次。入口不修改它，也不因它阻断。Git 是否把它算进脏状态取决于这个检出的忽略规则。")
            }
            else {
                $lines.Add("- overrideCfg present=false。Godot 启动前没有这个文件。")
            }
        }
        $lines.Add("")
    }
    $lines.Add("## 结果")
    $counts = $Summary["outcomes"]
    $lines.Add("- 通过 $($counts["passed"])")
    $lines.Add("- 失败 $($counts["failed"])")
    $lines.Add("- 阻塞 $($counts["blocked"])")
    $lines.Add("- 取消 $($counts["cancelled"])")
    $lines.Add("- 未执行 $($counts["notExecuted"])")
    if ($preparation -and $preparation["outcome"] -eq 'failed') { $lines.Add("- 准备阶段失败；上述计数仅包含检查结果，详情见准备节。") }
    $lines.Add("- 退出码 $($Summary["exitCode"])")
    $lines.Add("")
    $lines.Add("## 检查")
    foreach ($check in @($Summary["checks"])) {
        $claim = ""
        if ($check["claim"]) { $claim = " claim=$($check["claim"])" }
        $lines.Add("- $($check["outcome"]) $($check["id"]) $($check["durationMs"])ms $($check["reason"])$claim")
        foreach ($artifact in @($check["artifacts"])) {
            if ($artifact) { $lines.Add("  - $artifact") }
        }
    }
    if (@($Summary["checks"]).Count -eq 0) { $lines.Add("- 无用例结果") }
    $lines.Add("")
    $lines.Add("## 人工观察")
    $lines.Add("- 本次没有执行人工检查。清单里的人工入口不是自动化通过。")
    $lines.Add("")
    $lines.Add("## 清单")
    if ($Summary["migrationFeedback"]) { $lines.Add([string] $Summary["migrationFeedback"]) }
    foreach ($item in @($Summary["inventory"])) {
        $measured = [string] $item["durationStatus"]
        if ($item.ContainsKey("durationMs")) { $measured = "$measured $($item["durationMs"])ms" }
        $lines.Add("- $($item["status"]) $($item["id"]) $measured $($item["destination"])")
    }
    if (@($Summary["inventory"]).Count -eq 0) { $lines.Add("- 清单为空") }
    $lines.Add("")
    $lines.Add("## 产物")
    $lines.Add("- $($Summary["artifacts"]["directory"])")
    return ($lines -join [Environment]::NewLine) + [Environment]::NewLine
}

function Join-ReportItems($Value) {
    if ($null -eq $Value) { return "" }
    $base = $Value.psobject.BaseObject
    if ($base -is [System.Collections.IEnumerable] -and $base -isnot [string]) {
        $parts = [System.Collections.Generic.List[string]]::new()
        foreach ($item in $base) { if ($null -ne $item) { $parts.Add([string] $item) } }
        return ($parts -join ",")
    }
    return [string] $base
}

function New-JsonOptions {
    $options = [System.Text.Json.JsonSerializerOptions]::new()
    $options.WriteIndented = $true
    $options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
    return $options
}

function New-RequestedSelection {
    param($Parsed)
    $selection = New-Map
    $families = New-List
    $scopes = New-List
    $tests = New-List
    if ($Parsed) {
        foreach ($family in @($Parsed.Families)) { $families.Add($family) }
        foreach ($scope in @($Parsed.Scopes)) { $scopes.Add($scope) }
        foreach ($test in @($Parsed.Tests)) { $tests.Add($test) }
        Add-Map $selection "correctness" ([bool] $Parsed.Correctness) | Out-Null
        Add-Map $selection "buildGame" ([bool] $Parsed.BuildGame) | Out-Null
    }
    else {
        Add-Map $selection "correctness" $false | Out-Null
        Add-Map $selection "buildGame" $false | Out-Null
    }
    Add-Map $selection "modules" @($(if ($Parsed) { $Parsed.Modules })) | Out-Null
    Add-Map $selection "families" $families | Out-Null
    Add-Map $selection "scopes" $scopes | Out-Null
    Add-Map $selection "tests" $tests | Out-Null
    Add-Map $selection "caseIds" (New-List) | Out-Null
    return ,$selection
}

function Publish-Run {
    param($Parsed, $Preparation, $Environment, $Selection, $Checks, $Groups, [int] $ExitCode, [string] $Directory)
    if (-not $Directory) { return }
    if ($null -eq $Environment) { $Environment = New-Map }
    if (-not $Environment.ContainsKey("os")) { Add-Map $Environment "os" ([System.Environment]::OSVersion.VersionString) | Out-Null }
    if (-not $Environment.ContainsKey("powershell")) { Add-Map $Environment "powershell" $PSVersionTable.PSVersion.ToString() | Out-Null }
    if (-not $Environment.ContainsKey("overrideCfgPresent")) {
        $overrideCfg = Get-CachedOverrideCfg
        Add-Map $Environment "overrideCfgPresent" ([bool] $overrideCfg.Present) | Out-Null
        Add-Map $Environment "overrideCfgSha256" ([string] $overrideCfg.Sha256) | Out-Null
    }
    if ($null -eq $Selection) { $Selection = New-RequestedSelection $Parsed }
    if ($null -eq $Checks) { $Checks = New-List }
    if ($null -eq $Groups) { $Groups = New-List }
    $tested = $script:TestedGit
    if ($null -eq $tested) { $tested = New-MissingGitState "构建前没有记录 Git 身份" }
    $summary = New-Summary -Parsed $Parsed -Git $tested -Ending (Get-GitState) -Environment $Environment -Selection $Selection -Checks $Checks -Groups $Groups -Preparation $Preparation -ExitCode $ExitCode -Directory $Directory
    $markdown = New-Markdown -Summary $summary
    Write-RunReports -State ([pscustomobject]@{ Directory = $Directory; Summary = $summary; Markdown = $markdown; JsonOptions = (New-JsonOptions) }) | Out-Null
}

function Get-ExitCode {
    param($Checks, [switch] $CleanupFailed, [switch] $UserCancel, [switch] $PrepTimedOut, [switch] $RequiredBuildBlocked, [switch] $RequiredBuildFailed, [switch] $IsolationFailed, [int] $PriorExitCode = 0)
    # 清理后的重算保留此前结果分类；参数选择错误不属于执行结果合并。
    if ($PriorExitCode -eq 4) { return 4 }
    $failed = $false
    $blocked = $false
    $cancelled = $false
    $notExecuted = $false
    foreach ($check in $Checks) {
        switch ([string] $check["outcome"]) {
            "failed" { $failed = $true }
            "blocked" { $blocked = $true }
            "cancelled" { $cancelled = $true }
            "not-executed" { $notExecuted = $true }
        }
    }
    if ($failed -or $RequiredBuildFailed -or $PriorExitCode -eq 1) { return 1 }
    if ($CleanupFailed) { return 2 }
    if ($UserCancel -or $cancelled -or $PrepTimedOut -or $PriorExitCode -eq 3) { return 3 }
    if ($blocked -or $notExecuted -or $RequiredBuildBlocked -or $IsolationFailed -or $PriorExitCode -eq 2) { return 2 }
    return 0
}

function New-Check {
    param($Case, [string] $Outcome, [string] $Reason, [int] $DurationMs, [string] $Group, $Artifacts = $null)
    $check = New-Map
    Add-Map $check "id" $Case.Id | Out-Null
    Add-Map $check "module" $Case.Module | Out-Null
    if ($Case.Kind -eq "mstest") { Add-Map $check "project" $Case.Project | Out-Null }
    Add-Map $check "family" $Case.Family | Out-Null
    Add-Map $check "claim" ([string] $Case.Claim) | Out-Null
    $scopes = New-List
    foreach ($scope in @($Case.Scopes)) { $scopes.Add($scope) }
    Add-Map $check "scopes" $scopes | Out-Null
    $requirements = New-List
    foreach ($requirement in @($Case.Requirements)) { $requirements.Add($requirement) }
    Add-Map $check "requirements" $requirements | Out-Null
    Add-Map $check "group" $Group | Out-Null
    Add-Map $check "outcome" $Outcome | Out-Null
    Add-Map $check "reason" $Reason | Out-Null
    Add-Map $check "durationMs" $DurationMs | Out-Null
    if ($null -eq $Artifacts) { $Artifacts = New-List }
    Add-Map $check "artifacts" $Artifacts | Out-Null
    return ,$check
}

function New-GroupReport {
    param(
        [string] $Name,
        [string] $Outcome,
        [int] $DurationMs,
        $Filters = $null,
        $Commands = $null,
        $TrxPaths = $null
    )
    if ($null -eq $Filters) { $Filters = New-List }
    if ($null -eq $Commands) { $Commands = New-List }
    if ($null -eq $TrxPaths) { $TrxPaths = New-List }
    $groupMap = New-Map
    Add-Map $groupMap "name" $Name | Out-Null
    Add-Map $groupMap "outcome" $Outcome | Out-Null
    Add-Map $groupMap "durationMs" $DurationMs | Out-Null
    Add-Map $groupMap "filters" $Filters | Out-Null
    Add-Map $groupMap "commands" $Commands | Out-Null
    Add-Map $groupMap "trx" $TrxPaths | Out-Null
    return ,$groupMap
}

function Get-FilterLimit {
    $limit = 7000
    if (-not $script:TestHostPath) { return $limit }
    if (-not $env:TRAIN_RUN_TESTS_FILTER_LIMIT) { return $limit }
    $parsedLimit = 0
    if ([int]::TryParse([string] $env:TRAIN_RUN_TESTS_FILTER_LIMIT, [ref] $parsedLimit) -and $parsedLimit -ge 20 -and $parsedLimit -lt 7000) {
        return $parsedLimit
    }
    return $limit
}

function Get-CaseBatches {
    param($Cases)
    $limit = Get-FilterLimit
    $batches = [System.Collections.Generic.List[object]]::new()
    $filters = [System.Collections.Generic.List[string]]::new()
    $members = [System.Collections.Generic.List[object]]::new()
    $length = 0
    $ordered = [System.Collections.Generic.List[object]]::new()
    foreach ($case in $Cases) { $ordered.Add($case) }
    $ordered.Sort([System.Comparison[object]] {
        param($left, $right)
        return [string]::Compare([string] $left.Id, [string] $right.Id, [StringComparison]::Ordinal)
    })
    foreach ($case in $ordered) {
        $piece = "FullyQualifiedName=$($case.Id)"
        if ($members.Count -gt 0 -and ($case.Project -ne $members[0].Project -or $length + $piece.Length + 1 -gt $limit)) {
            $batches.Add([pscustomobject]@{ Filter = ($filters -join "|"); Cases = $members.ToArray() })
            $filters = [System.Collections.Generic.List[string]]::new()
            $members = [System.Collections.Generic.List[object]]::new()
            $length = 0
        }
        [void] $filters.Add($piece)
        [void] $members.Add($case)
        $length += $piece.Length + 1
    }
    if ($members.Count -gt 0) {
        $batches.Add([pscustomobject]@{ Filter = ($filters -join "|"); Cases = $members.ToArray() })
    }
    return ,$batches.ToArray()
}

function Merge-TrxResult {
    param($Merged, [string] $Id, $Entry)
    if (-not $Merged.ContainsKey($Id)) {
        $Merged[$Id] = $Entry
        return
    }
    $Merged[$Id] = Merge-ResultEntry $Merged[$Id] $Entry
}

function Add-PrepStep {
    param($Preparation, [string] $Name, [string] $Outcome, [string] $Reason, [int] $DurationMs, $Command)
    if (-not $Preparation.ContainsKey("steps")) {
        Add-Map $Preparation "steps" (New-List) | Out-Null
    }
    $step = New-Map
    Add-Map $step "name" $Name | Out-Null
    Add-Map $step "outcome" $Outcome | Out-Null
    Add-Map $step "reason" $Reason | Out-Null
    Add-Map $step "durationMs" $DurationMs | Out-Null
    if ($null -ne $Command) { Add-Map $step "command" $Command | Out-Null }
    $Preparation["steps"].Add($step)
    $Preparation["durationMs"] = [int] $Preparation["durationMs"] + $DurationMs
    if ($null -ne $Command) { $Preparation["commandCount"] = [int] $Preparation["commandCount"] + 1 }
    $current = [string] $Preparation["outcome"]
    if (-not $Preparation.ContainsKey('outcome') -or (Get-OutcomeRank $Outcome) -gt (Get-OutcomeRank $current)) {
        $Preparation["outcome"] = $Outcome
        $Preparation["reason"] = $Reason
    }
}

function Read-RepoCatalog {
    if ($script:RepoCatalogLoaded) { return }
    $script:RepoCatalogLoaded = $true
    $script:RepoCatalog = $null
    $path = Join-Path $PSScriptRoot "runner-catalog.json"
    if (Test-Path -LiteralPath $path) {
        $script:RepoCatalog = Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json
    }
}

function New-StringList {
    param($Values)
    $list = [System.Collections.Generic.List[string]]::new()
    foreach ($value in @($Values)) {
        if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string] $value)) {
            $list.Add([string] $value)
        }
    }
    return ,$list
}

function Convert-ExternalCase {
    param($Item)
    $scopes = New-StringList $Item.scopes
    $requirements = New-StringList $Item.requirements
    $arguments = New-StringList $Item.arguments
    $group = [string] $Item.group
    if (-not $group -and $scopes.Count -gt 0) { $group = [string] $scopes[0] }
    $module = if ($Item.PSObject.Properties['module']) { [string] $Item.module } else { 'godot' }
    return [pscustomobject]@{
        Id = [string] $Item.id
        Family = [string] $Item.family
        Scopes = $scopes
        Requirements = $requirements
        Group = $group
        Kind = "external"
        Module = $module
        Claim = [string] $Item.claim
        Script = Resolve-ExistingPath ([string] $Item.script)
        Arguments = $arguments
        NeedsImport = [bool] $Item.needsImport
        NeedsGameBuild = [bool] $Item.needsGameBuild
        NeedsExport = [bool] ($null -ne $Item.PSObject.Properties["needsExport"] -and $Item.needsExport)
        NeedsContentInput = [bool] ($null -ne $Item.PSObject.Properties["needsContentInput"] -and $Item.needsContentInput)
        TimeoutSeconds = [int] $Item.timeoutSeconds
    }
}

function Add-ExternalCases {
    param($Cases, $Catalog, $Modules)
    if ($null -eq $Catalog -or $null -eq $Catalog.checks) { return }
    foreach ($item in @($Catalog.checks)) {
        $case = Convert-ExternalCase $item
        if (@($Modules).Count -eq 0 -or $case.Module -eq 'all' -or @($Modules) -contains $case.Module) { $Cases.Add($case) }
    }
}

function Test-MSTestProject {
    param([string] $Path)
    [xml] $projectXml = Get-Content -LiteralPath $Path -Raw
    foreach ($package in @($projectXml.SelectNodes('/Project/ItemGroup/PackageReference'))) {
        if ([string] $package.Include -in @('MSTest', 'MSTest.TestAdapter')) { return $true }
    }
    return $false
}

function Get-Sha256File {
    param([string] $Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return "" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-ContentIdentity {
    $project = Join-Path $script:Root "project.godot"
    $version = ""
    if (Test-Path -LiteralPath $project) {
        foreach ($line in [System.IO.File]::ReadAllLines($project)) {
            if ($line -match 'config/version="([^"]+)"') { $version = $Matches[1] }
        }
    }
    return [pscustomobject]@{ Version = $version; Sha256 = (Get-Sha256File $project) }
}

function Get-GameUserDataDirectory {
    if (-not $env:APPDATA) { return "" }
    return Join-Path $env:APPDATA "Godot\app_userdata\Train"
}

function Get-GameUserDataStamp {
    try {
        $dir = Get-GameUserDataDirectory
        if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { return "absent" }
        $lines = [System.Collections.Generic.List[string]]::new()
        foreach ($file in @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force | Sort-Object -Property FullName)) {
            $relative = $file.FullName.Substring($dir.Length).TrimStart('\', '/')
            $lines.Add("$relative|$($file.Length)|$($file.LastWriteTimeUtc.Ticks)")
        }
        if ($lines.Count -eq 0) { return "empty" }
        $bytes = $script:Utf8.GetBytes(($lines -join "`n"))
        return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
    }
    catch {
        return "unreadable"
    }
}

function New-InventoryMaps {
    param($Checks)
    Read-RepoCatalog
    $list = New-List
    if ($null -eq $script:RepoCatalog -or $null -eq $script:RepoCatalog.inventory) { return ,$list }
    foreach ($item in @($script:RepoCatalog.inventory)) {
        $map = New-Map
        Add-Map $map "id" ([string] $item.id) | Out-Null
        Add-Map $map "entry" ([string] $item.entry) | Out-Null
        Add-Map $map "contract" ([string] $item.contract) | Out-Null
        Add-Map $map "boundary" ([string] $item.boundary) | Out-Null
        Add-Map $map "destination" ([string] $item.destination) | Out-Null
        Add-Map $map "status" ([string] $item.status) | Out-Null
        $durationStatus = [string] $item.durationStatus
        $durationMs = $null
        $matched = "unmeasured"
        foreach ($check in @($Checks)) {
            if ([string] $check["id"] -ne [string] $item.id) { continue }
            $status = Get-RunDurationStatus -Outcome ([string] $check["outcome"]) -DurationMs ([int] $check["durationMs"])
            if ($status -eq "measured-this-run") {
                $matched = $status
                $durationMs = [int] $check["durationMs"]
            }
            elseif ($status -eq "partial" -and $matched -ne "measured-this-run") {
                $matched = "partial"
                $durationMs = [int] $check["durationMs"]
            }
        }
        if ($matched -ne "unmeasured") { $durationStatus = $matched }
        Add-Map $map "durationStatus" $durationStatus | Out-Null
        if ($null -ne $durationMs) { Add-Map $map "durationMs" $durationMs | Out-Null }
        Add-Map $map "claim" ([string] $item.claim) | Out-Null
        $inventoryRequirements = New-List
        foreach ($requirement in @($item.requirements)) { $inventoryRequirements.Add([string] $requirement) }
        Add-Map $map "requirements" $inventoryRequirements | Out-Null
        $list.Add($map)
    }
    return ,$list
}

function Get-OverrideCfgIdentity {
    $path = Join-Path $script:Root "override.cfg"
    if (-not (Test-Path -LiteralPath $path)) {
        return [pscustomobject]@{ Present = $false; Sha256 = "" }
    }
    return [pscustomobject]@{ Present = $true; Sha256 = (Get-Sha256File $path) }
}

function Get-CachedOverrideCfg {
    if ($null -eq $script:OverrideCfg) { $script:OverrideCfg = Get-OverrideCfgIdentity }
    return $script:OverrideCfg
}

function New-EngineAppData {
    param([string] $RunDirectory, [string] $Name)
    $safe = ($Name -replace '[^A-Za-z0-9._-]', '_')
    $appData = Join-Path $RunDirectory (Join-Path "engine" (Join-Path $safe "appdata"))
    New-Item -ItemType Directory -Force -Path $appData | Out-Null
    return $appData
}

function Add-UsedFiles {
    param($Artifacts, [string] $Root)
    if (-not $Artifacts -or -not $Root -or -not (Test-Path -LiteralPath $Root)) { return }
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { return }
    if (-not $Artifacts.Contains($Root)) { $Artifacts.Add($Root) }
    foreach ($file in @($files | Select-Object -First 40)) {
        if (-not $Artifacts.Contains($file.FullName)) { $Artifacts.Add($file.FullName) }
    }
}

function New-ContentCheckInput {
    param([string] $CaseDirectory)
    $root = Join-Path $CaseDirectory "content-input"
    $sourceResources = Join-Path $script:Root "builtin/core/resources"
    foreach ($state in @("initial", "added")) {
        $resources = Join-Path $root $state
        $voxels = Join-Path $resources "voxels"
        $images = Join-Path $voxels "grain"
        New-Item -ItemType Directory -Force -Path $images | Out-Null
        Copy-Item -LiteralPath (Join-Path $sourceResources "voxels/grain.voxels.json") -Destination $voxels
        Copy-Item -LiteralPath (Join-Path $sourceResources "voxels/grain/placeholder.png") -Destination $images
        # 同一轮改变体素、价格表和类型规格，实际 App 检查断言它们只发布一个完整世代。
        # 两状态的敏感度和供给容量对应 App/Checks/ContentCheck.cs 的独立预期；调整夹具时同步核对。
        $sensitivity = if ($state -eq "added") { 0.75 } else { 0.5 }
        $capacity = if ($state -eq "added") { 3 } else { 2 }
        $market = [ordered]@{
            schema_version = 1
            market_tables = @([ordered]@{
                id = "smoke-prices"
                goods = @([ordered]@{ definition = "core:grain"; sensitivity = $sensitivity; min_price = 1; max_price = 1000 })
            })
        }
        $settlement = [ordered]@{
            schema_version = 1
            settlement_types = @([ordered]@{
                id = "smoke-settlement"; name = "Smoke settlement"
                supply = @([ordered]@{ definition = "core:grain"; capacity = $capacity; restore_per_tick = 1 })
                demand = @(); preferred_terrain = @(); weight = 1
                population_min = 1; population_max = 1; initial_platform_count = 0
            })
        }
        foreach ($directory in @("markets", "settlement-types")) {
            New-Item -ItemType Directory -Force -Path (Join-Path $resources $directory) | Out-Null
        }
        [System.IO.File]::WriteAllText((Join-Path $resources "markets/smoke.market-tables.json"), ($market | ConvertTo-Json -Depth 10), $script:Utf8)
        [System.IO.File]::WriteAllText((Join-Path $resources "settlement-types/smoke.settlement-types.json"), ($settlement | ConvertTo-Json -Depth 10), $script:Utf8)
    }
    $sourceDefinition = Join-Path $sourceResources "voxels/grain.voxels.json"
    $probe = [System.IO.File]::ReadAllText($sourceDefinition) | ConvertFrom-Json
    $probe.voxels[0].id = "smoke-ore"
    $probe.voxels[0].name = "Smoke Ore"
    $smokeDefinition = $probe | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $root "added/voxels/smoke-ore.voxels.json"), $smokeDefinition, $script:Utf8)
    return $root
}

function Invoke-ExternalCheck {
    param($Case, [string] $RunDirectory, [string] $CancelFile, [int] $TimeoutSeconds, [string] $ExportBinary = "")
    $safe = ($Case.Id -replace '[^A-Za-z0-9._-]', '_')
    $groupDir = Join-Path $RunDirectory (Join-Path "groups" $Case.Group)
    $caseDir = Join-Path $groupDir $safe
    New-Item -ItemType Directory -Force -Path $caseDir | Out-Null
    $output = Join-Path $caseDir "output"
    $needsContentRoot = [bool] $Case.NeedsContentInput
    $contentRoot = if ($needsContentRoot) { New-ContentCheckInput -CaseDirectory $output } else { "" }
    $logPath = Join-Path $caseDir "stdout.log"
    $arguments = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @($Case.Arguments)) {
        $text = [string] $argument
        $text = $text.Replace("{output}", $output).Replace("{exportBinary}", $ExportBinary).Replace("{configuration}", $script:Parsed.Configuration)
        $arguments.Add($text)
    }
    $empty = [pscustomobject]@{
        Missing = $false
        Run = $null
        DurationMs = 0
        Artifacts = (New-List)
        Command = (New-List)
        LogPath = $logPath
        AppData = ""
    }
    if (-not (Test-Path -LiteralPath $Case.Script)) {
        $empty.Missing = $true
        return $empty
    }
    $appData = ""
    $needsGodot = $false
    foreach ($requirement in @($Case.Requirements)) {
        if ([string] $requirement -eq "godot") { $needsGodot = $true }
    }
    if ($needsGodot) {
        $appData = New-EngineAppData -RunDirectory $RunDirectory -Name $Case.Id
    }
    $pwsh = (Get-Process -Id $PID).Path
    $argumentList = @("-NoProfile", "-File", $Case.Script) + @($arguments)
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $ran = Invoke-OwnedProcess -FileName $pwsh -Arguments $argumentList -WorkingDirectory $script:Root -TempDirectory (Join-Path $RunDirectory "tmp") -TimeoutSeconds $TimeoutSeconds -CancelFile $CancelFile -LogPath $logPath -StripTrainSwitches -AppDataDirectory $appData
    $watch.Stop()
    $artifacts = New-List
    if (Test-Path -LiteralPath $logPath) { $artifacts.Add($logPath) }
    if ($needsContentRoot) { Add-UsedFiles -Artifacts $artifacts -Root $contentRoot }
    Add-UsedFiles -Artifacts $artifacts -Root $output
    if ($appData) { Add-UsedFiles -Artifacts $artifacts -Root $appData }
    $command = New-List
    $command.Add($pwsh)
    foreach ($argument in $argumentList) { $command.Add($argument) }
    return [pscustomobject]@{
        Missing = $false
        Run = $ran
        DurationMs = [int] $watch.ElapsedMilliseconds
        Artifacts = $artifacts
        Command = $command
        LogPath = $logPath
        AppData = $appData
    }
}

function Invoke-Main {
    Protect-StandardHandles
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        [Console]::Error.WriteLine("error: invalid-argument PowerShell 7 is required")
        return 4
    }
    Set-Location -LiteralPath $script:Root
    $parsed = Parse-RunArgs -Raw $script:RawArgs
    $script:Parsed = $parsed
    $script:TestHostPath = [string] $parsed.TestHostPath
    if ($parsed.Error) {
        [Console]::Error.WriteLine($parsed.Error)
        [Console]::Out.WriteLine((Get-HelpText -GroupText ""))
        return 4
    }
    foreach ($family in @($parsed.Families)) {
        if (Test-PendingFamily $family) {
            [Console]::Error.WriteLine("error: pending $family is not a runnable group")
            return 2
        }
    }
    $hasSelection = $parsed.Modules.Count -gt 0 -or $parsed.Correctness -or $parsed.Families.Count -gt 0 -or $parsed.Scopes.Count -gt 0 -or $parsed.Tests.Count -gt 0
    if (-not $parsed.Help -and -not $parsed.List -and -not $hasSelection) {
        [Console]::Error.WriteLine("error: invalid-argument explicit selection is required")
        [Console]::Out.WriteLine((Get-HelpText -GroupText ""))
        return 4
    }

    if ($parsed.Project -and $parsed.Modules.Count -gt 0) {
        [Console]::Error.WriteLine("error: invalid-argument -Project cannot be combined with -Module")
        return 4
    }
    foreach ($module in $parsed.Modules) {
        if (@("core", "content", "scenes", "godot") -cnotcontains $module) {
            [Console]::Error.WriteLine("error: unknown-name module $module")
            return 4
        }
    }
    $projects = [System.Collections.Generic.List[object]]::new()
    if ($parsed.Project) {
        $path = Resolve-ExistingPath $parsed.Project
        if (-not (Test-Path -LiteralPath $path)) {
            [Console]::Error.WriteLine("error: invalid-argument project not found $path")
            return 4
        }
        $projects.Add([pscustomobject]@{ Module = "explicit"; Path = $path })
    }
    else {
        $testRoot = Join-Path $script:Root "Tests"
        if (Test-Path -LiteralPath $testRoot) {
            foreach ($file in @(Get-ChildItem -LiteralPath $testRoot -Recurse -Filter *.csproj | Sort-Object FullName)) {
                $relative = [System.IO.Path]::GetRelativePath($testRoot, $file.FullName).Replace("\", "/")
                $owner = $relative.Split("/")[0].ToLowerInvariant()
                $module = if (@("core", "content", "scenes", "godot") -contains $owner) { $owner }
                    elseif ($relative.StartsWith("ContractSupport/SelfTests/")) { "contract-support" }
                    else { "legacy" }
                if ($parsed.Modules.Count -gt 0 -and $parsed.Modules -notcontains $module -and
                    -not ($module -eq 'contract-support' -and @($parsed.Modules | Where-Object { $_ -in @('core', 'content', 'scenes') }).Count -gt 0)) { continue }
                if (-not (Test-MSTestProject $file.FullName)) { continue }
                $projects.Add([pscustomobject]@{ Module = $module; Path = $file.FullName })
            }
        }
        if ($parsed.Correctness -and $parsed.Modules.Count -eq 0) {
            foreach ($module in @('core', 'content', 'godot')) {
                if (@($projects | Where-Object Module -EQ $module).Count -eq 0) {
                    [Console]::Error.WriteLine("error: blocked module-entry-missing $module")
                    return 2
                }
            }
        }
        foreach ($module in $parsed.Modules) {
            if (@($projects | Where-Object Module -EQ $module).Count -eq 0) {
                [Console]::Error.WriteLine("error: blocked module-entry-missing $module")
                return 2
            }
        }
    }
    if ($projects.Count -eq 0) {
        [Console]::Error.WriteLine("error: zero-match projects")
        return 4
    }
    Read-RepoCatalog
    if ($script:RepoCatalog -and $script:RepoCatalog.feedback) {
        $script:MigrationFeedback = [string] $script:RepoCatalog.feedback
    }
    if ($parsed.RunnerCatalog -eq "none") {
        $script:RunnerCatalogMode = "none"
        $script:ExternalCatalog = $null
    }
    elseif ($parsed.RunnerCatalog) {
        $catalogPath = Resolve-ExistingPath $parsed.RunnerCatalog
        if (-not (Test-Path -LiteralPath $catalogPath)) {
            [Console]::Error.WriteLine("error: invalid-argument runner catalog not found $catalogPath")
            return 4
        }
        $script:ExternalCatalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding utf8 | ConvertFrom-Json
        $script:RunnerCatalogMode = "override"
    }
    elseif ($script:RepoCatalog) {
        $script:ExternalCatalog = $script:RepoCatalog
        $script:RunnerCatalogMode = "repository"
    }
    else {
        $script:RunnerCatalogMode = "none"
        $script:ExternalCatalog = $null
    }
    $dotnet = $parsed.DotnetPath
    if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue) -and -not (Test-Path -LiteralPath $dotnet)) {
        [Console]::Error.WriteLine("error: blocked dotnet not found")
        return 2
    }

    $runDirectory = ""
    # -List never executes. Help never executes. Any other explicit selection executes.
    $executing = (-not $parsed.Help) -and (-not $parsed.List) 
    if ($executing) {
        $resultsRoot = if ($parsed.ResultsRoot) { Resolve-ExistingPath $parsed.ResultsRoot } else { Join-Path $script:Root "TestResults/run-tests" }
        New-Item -ItemType Directory -Force -Path $resultsRoot | Out-Null
        $runId = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
        $runDirectory = Join-Path $resultsRoot $runId
        New-Item -ItemType Directory -Force -Path $runDirectory | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $runDirectory "tmp") | Out-Null
        [Console]::Out.WriteLine("RUN_DIRECTORY=$runDirectory")
        [Console]::Out.Flush()
    }
    $script:RunDirectory = $runDirectory
    Register-CancelSignal
    $cancelFile = ""
    if ($runDirectory) { $cancelFile = Join-Path $runDirectory "cancel.request" }

    # One preparation cap covers restore and build of the test assembly, Train.csproj,
    # Godot import, and the version probe. -PrepTimeoutSeconds replaces that one limit.
    # Cold measurement on 2026-09-30 with empty NUGET_PACKAGES and NUGET_HTTP_CACHE_PATH:
    # Train.Tests restore 10471ms and build --no-restore 8315ms;
    # Train.csproj restore 5834ms and build --no-restore 8608ms.
    # The mixed run on 2026-09-30 imported in 37726ms. 120 seconds is still about three times that import, so the cap stays.
    # It is a hang cap, not a pass condition.
    $prepTimeout = 120
    if ([int] $parsed.PrepTimeoutSeconds -gt 0) { $prepTimeout = [int] $parsed.PrepTimeoutSeconds }
    $tempForBuild = if ($runDirectory) { Join-Path $runDirectory "tmp" } else { [System.IO.Path]::GetTempPath() }
    $preparation = New-Map
    Add-Map $preparation "durationMs" 0 | Out-Null
    Add-Map $preparation "commandCount" 0 | Out-Null
    $catalog = [pscustomobject]@{ Cases = [System.Collections.Generic.List[object]]::new(); Errors = [System.Collections.Generic.List[string]]::new() }
    $projectEvidence = New-List
    # Capture the tree once before any selected test project is built.
    $script:TestedGit = Get-GitState
    foreach ($project in $projects) {
        $sourceProject = $project.Path
        $isolationRoot = ''
        $isolationEvidence = ''
        $isolationAllowed = @()
        if ($executing -and $parsed.Correctness -and -not $parsed.Project -and $project.Module -in $script:IsolatedModules) {
            try {
                $isolated = Copy-IsolatedProject -Project $project -RunDirectory $runDirectory
                $project.Path = $isolated.Project
                $isolationRoot = $isolated.Root
                $isolationEvidence = $isolated.Evidence
                $isolationAllowed = @($isolated.Allowed)
                $isolationMatrix = $isolated.Matrix
                Add-PrepStep -Preparation $preparation -Name "isolation-inputs-$($project.Module)" -Outcome 'passed' -Reason 'copied' -DurationMs 0 -Command $null
            }
            catch {
                # 缺失源文件/目录和配置错误属于仓库输入；其余 IO、权限问题才是环境阻塞。
                $inputException = $_.Exception.GetBaseException()
                $environmentFailure = $inputException -is [UnauthorizedAccessException] -or
                    ($inputException -is [IO.IOException] -and $inputException -isnot [IO.FileNotFoundException] -and $inputException -isnot [IO.DirectoryNotFoundException])
                $inputOutcome = if ($environmentFailure) { 'blocked' } else { 'failed' }
                $inputExitCode = if ($inputOutcome -eq 'failed') { 1 } else { 2 }
                [Console]::Error.WriteLine("error: $inputOutcome isolation inputs $($project.Module): $_")
                Add-PrepStep -Preparation $preparation -Name "isolation-inputs-$($project.Module)" -Outcome $inputOutcome -Reason $_.Exception.Message -DurationMs 0 -Command $null
                Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode $inputExitCode -Directory $runDirectory
                return $inputExitCode
            }
        }
        $projectPath = $project.Path
        $buildLog = if ($runDirectory) { Join-Path $runDirectory ("prepare-" + $projects.IndexOf($project) + "-" + $project.Module + ".log") } else { [System.IO.Path]::GetTempFileName() }
        $buildWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $buildArgs = @(
            "build", $projectPath,
            "-c", $parsed.Configuration,
            "--nologo",
            "-p:UseSharedCompilation=false"
        )
        $build = Invoke-OwnedProcess -FileName $dotnet -Arguments $buildArgs -WorkingDirectory $script:Root -TempDirectory $tempForBuild -TimeoutSeconds $prepTimeout -CancelFile $cancelFile -LogPath $buildLog
        $buildWatch.Stop()
        if (-not $runDirectory -and (Test-Path -LiteralPath $buildLog)) { Remove-Item -LiteralPath $buildLog -Force -ErrorAction SilentlyContinue }

        $buildCommand = New-List
        $buildCommand.Add($dotnet)
        foreach ($arg in $buildArgs) { $buildCommand.Add($arg) }
        Add-Map $preparation "command" $buildCommand | Out-Null
        $buildOutcome = "passed"
        $buildReason = "reused-for-groups"
        $buildDisposition = Get-ProcessDisposition $build
        if ($buildDisposition.Kind -eq "exit") {
            if ($build.ExitCode -ne 0) {
                $buildOutcome = Get-BuildFailureKind $build.Output
                $buildReason = if ($isolationRoot) { "isolated-build" } else { "build" }
            }
        }
        else {
            $buildOutcome = [string] $buildDisposition.Outcome
            $buildReason = [string] $buildDisposition.Kind
        }
        Add-Map $preparation "outcome" $buildOutcome | Out-Null
        Add-Map $preparation "reason" $buildReason | Out-Null
        Add-PrepStep -Preparation $preparation -Name "build" -Outcome $buildOutcome -Reason $buildReason -DurationMs ([int] $buildWatch.ElapsedMilliseconds) -Command $buildCommand

        # The test assembly is required for discovery, so a timeout or cancel stops the run.
        # A game-build or import timeout does not: it only blocks groups that need that step.
        if ($buildDisposition.Kind -eq "cleanup") {
            [Console]::Error.WriteLine("error: blocked cleanup")
            if ($runDirectory) { Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 2 -Directory $runDirectory }
            return 2
        }
        if ($buildDisposition.Kind -eq "timeout" -or $buildDisposition.Kind -eq "cancel") {
            [Console]::Error.WriteLine("error: cancelled build")
            if ($runDirectory) { Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 3 -Directory $runDirectory }
            return 3
        }
        if ($build.ExitCode -ne 0) {
            if ($buildOutcome -eq 'failed') {
                [Console]::Error.WriteLine("error: failed $buildReason sourceProject=$sourceProject; project=$projectPath; log=$buildLog")
                Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 1 -Directory $runDirectory
                return 1
            }
            [Console]::Error.WriteLine("error: blocked build sourceProject=$sourceProject; project=$projectPath; log=$buildLog")
            if ($parsed.Help -or $parsed.List) { return 2 }
            Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 2 -Directory $runDirectory
            return 2
        }

        if ($isolationRoot) {
            $referenceFailure = Test-IsolatedReferences -ProjectPath $projectPath -IsolationRoot $isolationRoot -EvidenceRoot $isolationEvidence -Allowed $isolationAllowed -Matrix $isolationMatrix -Dotnet $dotnet -TempDirectory $tempForBuild -CancelFile $cancelFile -TimeoutSeconds $prepTimeout
            if ($referenceFailure) {
                $outcome = [string] $referenceFailure.Outcome
                $exitCode = switch ($outcome) { 'failed' { 1 } 'cancelled' { 3 } default { 2 } }
                [Console]::Error.WriteLine("error: $outcome isolation references $($project.Module): $($referenceFailure.Reason); sourceProject=$sourceProject; project=$projectPath; log=$($referenceFailure.LogPath)")
                Add-PrepStep -Preparation $preparation -Name "isolation-references-$($project.Module)" -Outcome $outcome -Reason $referenceFailure.Reason -DurationMs 0 -Command $null
                Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode $exitCode -Directory $runDirectory
                return $exitCode
            }
            Add-PrepStep -Preparation $preparation -Name "isolation-references-$($project.Module)" -Outcome 'passed' -Reason 'resolved-and-output' -DurationMs 0 -Command $null
        }

        $assemblyPath = Get-ProjectAssemblyPath -ProjectPath $projectPath -Configuration $parsed.Configuration
        if (-not (Test-Path -LiteralPath $assemblyPath)) {
            [Console]::Error.WriteLine("error: blocked assembly missing $assemblyPath")
            Add-Map $preparation "outcome" "blocked" | Out-Null
            Add-Map $preparation "reason" "assembly" | Out-Null
            if ($runDirectory) { Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 2 -Directory $runDirectory }
            return 2
        }
        $projectCatalog = Convert-Catalog (Read-TestCatalog -AssemblyPath $assemblyPath) -ProjectPath $projectPath -Module $project.Module
        foreach ($case in $projectCatalog.Cases) {
            if ($parsed.Modules.Count -eq 0 -or $parsed.Modules -contains $case.Module) { $catalog.Cases.Add($case) }
        }
        foreach ($errorItem in $projectCatalog.Errors) { $catalog.Errors.Add($errorItem) }
        $projectEvidence.Add(@{ module = $project.Module; sourceProject = $sourceProject; project = $projectPath; isolationRoot = $isolationRoot; assembly = $assemblyPath; sha256 = (Get-Sha256File $assemblyPath) })
        if (@($catalog.Errors).Count -gt 0) {
            foreach ($discoveryError in @($catalog.Errors)) { [Console]::Error.WriteLine("error: discovery $discoveryError") }
            Add-Map $preparation "outcome" "blocked" | Out-Null
            Add-Map $preparation "reason" "discovery" | Out-Null
            if ($executing) { Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 2 -Directory $runDirectory }
            return 2
        }
    }
    if ($script:RunnerCatalogMode -ne "none") { Add-ExternalCases -Cases $catalog.Cases -Catalog $script:ExternalCatalog -Modules $parsed.Modules }
    if (($parsed.Modules.Count -eq 0 -or $parsed.Modules -contains 'godot') -and
        (($parsed.Correctness -and $parsed.Modules -contains 'godot') -or $parsed.Tests -contains 'godot.isolated-composition')) {
        $catalog.Cases.Add([pscustomobject]@{
            Id = 'godot.isolated-composition'; Kind = 'pending'; Module = 'godot'; Family = 'headless'
            Scopes = @('contracts'); Requirements = @(); Claim = 'isolation'; Project = ''; Group = 'godot-isolation'
            NeedsGameBuild = $false; NeedsImport = $false; NeedsExport = $false; NeedsContentInput = $false; TimeoutSeconds = 0
        })
    }
    $seenIds = @{}
    foreach ($case in @($catalog.Cases)) {
        if ([string]::IsNullOrWhiteSpace([string] $case.Id) -or $seenIds.ContainsKey([string] $case.Id)) {
            [Console]::Error.WriteLine("error: discovery duplicate $($case.Id)")
            Add-Map $preparation "outcome" "blocked" | Out-Null
            Add-Map $preparation "reason" "discovery" | Out-Null
            if ($executing) { Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 2 -Directory $runDirectory }
            return 2
        }
        $seenIds[[string] $case.Id] = $true
    }

    $groupText = Get-GroupSummary -Cases $catalog.Cases
    if ($parsed.Help) {
        [Console]::Out.WriteLine((Get-HelpText -GroupText $groupText))
        return 0
    }

    $selectionResult = Select-Cases -Parsed $parsed -Cases $catalog.Cases
    if ($selectionResult.Error) {
        [Console]::Error.WriteLine($selectionResult.Error)
        if ($runDirectory) { Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode ([int] $selectionResult.ExitCode) -Directory $runDirectory }
        return [int] $selectionResult.ExitCode
    }
    $selected = @($selectionResult.Cases)
    if ($parsed.List) {
        [Console]::Out.WriteLine("test-assembly: $script:TestAssemblyBoundary")
        [Console]::Out.WriteLine("coverage: registered MSTest, Godot, and cross-process checks are not all repository validation.")
        [Console]::Out.WriteLine($groupText)
        foreach ($case in $selected) { [Console]::Out.WriteLine((Format-CaseLine -Case $case)) }
        [Console]::Out.WriteLine("SELECTED count=$($selected.Count)")
        return 0
    }

    $sdk = Get-DotnetVersion -DotnetPath $dotnet
    [Console]::Out.WriteLine("SELECTED count=$($selected.Count)")
    foreach ($case in $selected) { [Console]::Out.WriteLine((Format-CaseLine -Case $case)) }
    $godotCases = @($selected | Where-Object { @($_.Requirements) -contains "godot" })
    $godotState = if ($godotCases.Count -eq 0) { "not-required" } elseif (Test-RequirementAvailable "godot") { $env:GODOT } else { "missing" }
    $timeoutText = if ([int] $parsed.TimeoutSeconds -gt 0) { "timeoutSeconds=$($parsed.TimeoutSeconds)" } else { "timeoutFormula=min(900,max(90,45+20*groupCaseCount))" }
    [Console]::Out.WriteLine("PREREQUISITES dotnet=$sdk godot=$godotState casesRequiringGodot=$($godotCases.Count) $timeoutText")

    $checks = New-List
    $groupReports = New-List
    $byGroup = @{}
    foreach ($case in $selected) {
        if (-not $byGroup.ContainsKey($case.Group)) { $byGroup[$case.Group] = [System.Collections.Generic.List[object]]::new() }
        $byGroup[$case.Group].Add($case)
    }
    $dispatch = New-DispatchState
    $gameBlocked = $false
    $gameOutcome = 'not-requested'
    $gameBlockReason = "build"
    $importBlocked = $false
    $importBlockReason = "import"
    $exportBlocked = $false
    $exportBlockReason = "export"
    $exportBinary = ""
    $godotVersion = ""
    $gameAssembly = ""
    $gameAssemblyReason = "not-requested"
    $null = Get-CachedOverrideCfg
    $contentIdentity = Get-ContentIdentity
    $userDataBefore = if ($runDirectory) { Get-GameUserDataStamp } else { "" }
    $userDataAfter = $userDataBefore

    $script:RunChecks = $checks
    $needsGame = @($selected | Where-Object { $_.NeedsGameBuild })
    if (($parsed.BuildGame -or $needsGame.Count -gt 0) -and -not $dispatch.StopDispatch) {
        $gameProject = Resolve-ExistingPath "Train.csproj"
        $gameArgs = @("build", $gameProject, "-c", $parsed.Configuration, "--nologo", "-p:UseSharedCompilation=false")
        $gameCommand = New-List
        $gameCommand.Add($dotnet)
        foreach ($arg in $gameArgs) { $gameCommand.Add($arg) }
        $gameWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $gameLog = Join-Path $runDirectory "game-build.log"
        $game = Invoke-OwnedProcess -FileName $dotnet -Arguments $gameArgs -WorkingDirectory $script:Root -TempDirectory (Join-Path $runDirectory "tmp") -TimeoutSeconds $prepTimeout -CancelFile $cancelFile -LogPath $gameLog -StripTrainSwitches
        $gameWatch.Stop()
        $gameOutcome = "passed"
        $gameReason = "reused-for-groups"
        $gameDisposition = Get-ProcessDisposition $game
        if ($gameDisposition.Kind -eq "exit") {
            if ($game.ExitCode -ne 0) {
                $gameOutcome = Get-BuildFailureKind $game.Output
                $gameReason = "build"
                $gameBlocked = $true
                $gameBlockReason = "build"
            }
        }
        else {
            $gameOutcome = [string] $gameDisposition.Outcome
            $gameReason = [string] $gameDisposition.Kind
            $gameBlocked = $true
            $gameBlockReason = $gameReason
            Apply-ProcessDisposition $gameDisposition $dispatch -PrepStep
        }
        Add-PrepStep -Preparation $preparation -Name "game-build" -Outcome $gameOutcome -Reason $gameReason -DurationMs ([int] $gameWatch.ElapsedMilliseconds) -Command $gameCommand
        if (-not $gameBlocked) {
            $builtAssembly = Get-GodotGameAssemblyPath -Configuration $parsed.Configuration
            if (Test-Path -LiteralPath $builtAssembly) {
                $gameAssembly = $builtAssembly
                $gameAssemblyReason = "built-this-run"
            }
            else {
                $gameAssembly = ""
                $gameAssemblyReason = "missing-after-build"
            }
        }
        else {
            $gameAssembly = ""
            $gameAssemblyReason = $gameBlockReason
        }
    }

    try {
    $needsImport = @($selected | Where-Object { $_.NeedsImport -and -not ($gameBlocked -and $_.NeedsGameBuild) })
    if ($needsImport.Count -gt 0 -and -not $dispatch.StopDispatch -and $godotState -eq $env:GODOT) {
        $importAppData = New-EngineAppData -RunDirectory $runDirectory -Name "import"
        # Recovery mode still imports resources and skips editor plugins that write the player user-data folder.
        $importEngineLog = Join-Path $runDirectory "engine/import/godot.log"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $importEngineLog) | Out-Null
        $importArgs = @("--headless", "--path", $script:Root, "--recovery-mode", "--log-file", $importEngineLog, "--editor", "--import")
        $importCommand = New-List
        $importCommand.Add($env:GODOT)
        foreach ($arg in $importArgs) { $importCommand.Add($arg) }
        $importWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $import = Invoke-OwnedProcess -FileName $env:GODOT -Arguments $importArgs -WorkingDirectory $script:Root -TempDirectory (Join-Path $runDirectory "tmp") -TimeoutSeconds $prepTimeout -CancelFile $cancelFile -LogPath (Join-Path $runDirectory "import.log") -StripTrainSwitches -AppDataDirectory $importAppData
        $importWatch.Stop()
        $importOutcome = "passed"
        $importReason = "reused-for-groups"
        $importDisposition = Get-ProcessDisposition $import
        if ($importDisposition.Kind -eq "exit") {
            if ($import.ExitCode -ne 0) { $importOutcome = "blocked"; $importReason = "import"; $importBlocked = $true; $importBlockReason = "import" }
        }
        else {
            $importOutcome = [string] $importDisposition.Outcome
            $importReason = [string] $importDisposition.Kind
            $importBlocked = $true
            $importBlockReason = $importReason
            Apply-ProcessDisposition $importDisposition $dispatch -PrepStep
        }
        Add-PrepStep -Preparation $preparation -Name "import" -Outcome $importOutcome -Reason $importReason -DurationMs ([int] $importWatch.ElapsedMilliseconds) -Command $importCommand
    }
    $needsExport = @($selected | Where-Object { $_.NeedsExport -and -not ($gameBlocked -and $_.NeedsGameBuild) -and -not ($importBlocked -and $_.NeedsImport) })
    if ($needsExport.Count -gt 0 -and -not $dispatch.StopDispatch -and $godotState -eq $env:GODOT) {
        $exportBinary = Join-Path $runDirectory "exports/windows/train.exe"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $exportBinary) | Out-Null
        $exportEngineLog = Join-Path $runDirectory "engine/export/godot.log"
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $exportEngineLog) | Out-Null
        $exportArgs = @("--headless", "--path", $script:Root, "--recovery-mode", "--log-file", $exportEngineLog, "--export-release", "Windows Desktop", $exportBinary)
        $exportCommand = New-List
        $exportCommand.Add($env:GODOT)
        foreach ($arg in $exportArgs) { $exportCommand.Add($arg) }
        $exportWatch = [System.Diagnostics.Stopwatch]::StartNew()
        # 导出模板位于真实 APPDATA；这里只隔离随后运行的每项检查，不能隐藏模板目录。
        $export = Invoke-OwnedProcess -FileName $env:GODOT -Arguments $exportArgs -WorkingDirectory $script:Root -TempDirectory (Join-Path $runDirectory "tmp") -TimeoutSeconds $prepTimeout -CancelFile $cancelFile -LogPath (Join-Path $runDirectory "export-windows.log") -StripTrainSwitches
        $exportWatch.Stop()
        $exportOutcome = "passed"
        $exportReason = "reused-for-checks"
        $exportDisposition = Get-ProcessDisposition $export
        if ($exportDisposition.Kind -eq "exit") {
            if ($export.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $exportBinary)) {
                $exportOutcome = "blocked"
                $exportReason = "export"
                $exportBlocked = $true
            }
        }
        else {
            $exportOutcome = [string] $exportDisposition.Outcome
            $exportReason = [string] $exportDisposition.Kind
            $exportBlocked = $true
            $exportBlockReason = $exportReason
            Apply-ProcessDisposition $exportDisposition $dispatch -PrepStep
        }
        Add-PrepStep -Preparation $preparation -Name "windows-export" -Outcome $exportOutcome -Reason $exportReason -DurationMs ([int] $exportWatch.ElapsedMilliseconds) -Command $exportCommand
    }
    if ($godotCases.Count -gt 0 -and $godotState -eq $env:GODOT -and -not $dispatch.StopDispatch) {
        $versionAppData = New-EngineAppData -RunDirectory $runDirectory -Name "godot-version"
        $versionArgs = @("--version")
        $versionCommand = New-List
        $versionCommand.Add($env:GODOT)
        foreach ($arg in $versionArgs) { $versionCommand.Add($arg) }
        $versionWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $version = Invoke-OwnedProcess -FileName $env:GODOT -Arguments $versionArgs -WorkingDirectory $script:Root -TempDirectory (Join-Path $runDirectory "tmp") -TimeoutSeconds $prepTimeout -CancelFile $cancelFile -LogPath (Join-Path $runDirectory "godot-version.log") -StripTrainSwitches -AppDataDirectory $versionAppData
        $versionWatch.Stop()
        $versionOutcome = "passed"
        $versionReason = "probed"
        $versionDisposition = Get-ProcessDisposition $version
        if ($versionDisposition.Kind -eq "exit") {
            if ($version.ExitCode -eq 0) {
                $godotVersion = (($version.Output -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)).Trim()
            }
            else {
                $versionOutcome = "unknown"
                $versionReason = "nonzero"
                $godotVersion = "unknown"
            }
        }
        else {
            $versionOutcome = [string] $versionDisposition.Outcome
            $versionReason = [string] $versionDisposition.Kind
            Apply-ProcessDisposition $versionDisposition $dispatch -PrepStep
        }
        Add-PrepStep -Preparation $preparation -Name "godot-version" -Outcome $versionOutcome -Reason $versionReason -DurationMs ([int] $versionWatch.ElapsedMilliseconds) -Command $versionCommand
    }
        foreach ($groupName in @($byGroup.Keys | Sort-Object)) {
            if ((Test-CancelRequested) -and -not $dispatch.StopDispatch) {
                $dispatch.UserCancel = $true
                $dispatch.StopDispatch = $true
                $dispatch.StopReason = "cancel"
            }
            $groupCases = @($byGroup[$groupName])
            if ($dispatch.StopDispatch) {
                foreach ($case in $groupCases) {
                    $checks.Add((New-Check -Case $case -Outcome "not-executed" -Reason $dispatch.StopReason -DurationMs 0 -Group $groupName))
                }
                $groupReports.Add((New-GroupReport -Name $groupName -Outcome "not-executed" -DurationMs 0))
                continue
            }
            $runnable = [System.Collections.Generic.List[object]]::new()
            foreach ($case in $groupCases) {
                if ($case.Kind -eq 'pending') {
                    $checks.Add((New-Check -Case $case -Outcome 'blocked' -Reason 'not-implemented' -DurationMs 0 -Group $groupName))
                    continue
                }
                $missing = @()
                foreach ($requirement in @($case.Requirements)) {
                    if (-not (Test-RequirementAvailable $requirement)) { $missing += $requirement }
                }
                if ($missing.Count -gt 0) {
                    $checks.Add((New-Check -Case $case -Outcome "blocked" -Reason ("environment:" + ($missing -join ",")) -DurationMs 0 -Group $groupName))
                }
                else {
                    $runnable.Add($case)
                }
            }
            if ($runnable.Count -eq 0) {
                $groupReports.Add((New-GroupReport -Name $groupName -Outcome "blocked" -DurationMs 0))
                continue
            }

            $mstestRunnable = [System.Collections.Generic.List[object]]::new()
            $externalRunnable = [System.Collections.Generic.List[object]]::new()
            foreach ($case in @($runnable)) {
                if ($case.Kind -eq "external") {
                    if ($case.NeedsGameBuild -and $gameBlocked) {
                        $checks.Add((New-Check -Case $case -Outcome "blocked" -Reason $gameBlockReason -DurationMs 0 -Group $groupName))
                    }
                    elseif ($case.NeedsImport -and $importBlocked) {
                        $checks.Add((New-Check -Case $case -Outcome "blocked" -Reason $importBlockReason -DurationMs 0 -Group $groupName))
                    }
                    elseif ($case.NeedsExport -and $exportBlocked) {
                        $checks.Add((New-Check -Case $case -Outcome "blocked" -Reason $exportBlockReason -DurationMs 0 -Group $groupName))
                    }
                    else { $externalRunnable.Add($case) }
                }
                else { $mstestRunnable.Add($case) }
            }
            $timeoutCount = if ($mstestRunnable.Count -gt 0) { $mstestRunnable.Count } else { [Math]::Max(1, $externalRunnable.Count) }
            $timeout = Get-GroupTimeoutSeconds -CaseCount $timeoutCount -Override $parsed.TimeoutSeconds
            [Console]::Out.WriteLine("GROUP name=$groupName cases=$($runnable.Count) timeoutSeconds=$timeout")
            $groupDir = Join-Path $runDirectory (Join-Path "groups" $groupName)
            New-Item -ItemType Directory -Force -Path $groupDir | Out-Null
            $batches = Get-CaseBatches -Cases $mstestRunnable
            $groupWatch = [System.Diagnostics.Stopwatch]::StartNew()
            $filters = New-List
            $commands = New-List
            $trxPaths = New-List
            $batchIndex = 0
            $merged = @{}
            $knownExtras = @{}
            foreach ($batch in @($batches)) {
                $batchCases = @($batch.Cases)
                if ($dispatch.StopDispatch) {
                    foreach ($case in $batchCases) {
                        $checks.Add((New-Check -Case $case -Outcome "not-executed" -Reason $dispatch.StopReason -DurationMs 0 -Group $groupName))
                    }
                    continue
                }
                $trxName = "results-$batchIndex.trx"
                $testArgs = @(
                    "test", $batchCases[0].Project,
                    "-c", $parsed.Configuration,
                    "--no-build",
                    "--no-restore",
                    "--nologo",
                    "--filter", [string] $batch.Filter,
                    "--results-directory", $groupDir,
                    "--logger", "trx;LogFileName=$trxName"
                )
                $fileName = $dotnet
                $argumentList = $testArgs
                if ($parsed.TestHostPath) {
                    $fileName = (Get-Process -Id $PID).Path
                    $argumentList = @("-NoProfile", "-File", $parsed.TestHostPath) + $testArgs
                }
                $elapsedSeconds = [int] [Math]::Floor($groupWatch.Elapsed.TotalSeconds)
                $remaining = $timeout - $elapsedSeconds
                if ($remaining -lt 1) {
                    foreach ($case in $batchCases) {
                        $checks.Add((New-Check -Case $case -Outcome "not-executed" -Reason "timeout" -DurationMs 0 -Group $groupName))
                    }
                    $batchIndex++
                    continue
                }
                $batchStarted = [DateTime]::UtcNow
                $ran = Invoke-OwnedProcess -FileName $fileName -Arguments $argumentList -WorkingDirectory $script:Root -TempDirectory (Join-Path $runDirectory "tmp") -TimeoutSeconds $remaining -CancelFile $cancelFile -LogPath (Join-Path $groupDir "stdout-$batchIndex.log")
                $batchMs = [int] ([DateTime]::UtcNow - $batchStarted).TotalMilliseconds
                $trxPath = Join-Path $groupDir $trxName
                $trxPaths.Add($trxPath)
                $filters.Add([string] $batch.Filter)
                $command = New-List
                $command.Add($fileName)
                foreach ($arg in $argumentList) { $command.Add($arg) }
                $commands.Add($command)
                $batchTrx = Read-TrxResults -TrxPath $trxPath
                foreach ($id in @($batchTrx.Keys)) { Merge-TrxResult -Merged $merged -Id $id -Entry $batchTrx[$id] }
                $batchDisposition = Get-ProcessDisposition $ran
                foreach ($case in $batchCases) {
                    if ($batchDisposition.Kind -ne "exit") {
                        $duration = $batchMs
                        if (($batchDisposition.Kind -eq "timeout" -or $batchDisposition.Kind -eq "cancel") -and $merged.ContainsKey($case.Id) -and [string] $merged[$case.Id].Outcome -eq "failed") {
                            $native = $merged[$case.Id]
                            $checks.Add((New-Check -Case $case -Outcome "failed" -Reason $native.Native -DurationMs $native.DurationMs -Group $groupName))
                        }
                        else {
                            $checks.Add((New-Check -Case $case -Outcome $batchDisposition.Outcome -Reason $batchDisposition.Kind -DurationMs $duration -Group $groupName))
                        }
                    }
                    elseif ($merged.ContainsKey($case.Id)) {
                        $native = $merged[$case.Id]
                        $batchFailed = $false
                        foreach ($id in @($batchTrx.Keys)) {
                            if ([string] $batchTrx[$id].Outcome -eq "failed") { $batchFailed = $true }
                        }
                        if ([string] $native.Outcome -eq "passed" -and $ran.ExitCode -ne 0 -and -not $batchFailed) {
                            $checks.Add((New-Check -Case $case -Outcome "blocked" -Reason "runner-exit:$($ran.ExitCode)" -DurationMs $native.DurationMs -Group $groupName))
                        }
                        else {
                            $checks.Add((New-Check -Case $case -Outcome $native.Outcome -Reason $native.Native -DurationMs $native.DurationMs -Group $groupName))
                        }
                    }
                    else {
                        $checks.Add((New-Check -Case $case -Outcome "not-executed" -Reason "missing-result" -DurationMs 0 -Group $groupName))
                    }
                }
                foreach ($extra in @($batchTrx.Keys)) {
                    $known = $false
                    foreach ($case in $batchCases) { if ($case.Id -eq $extra) { $known = $true } }
                    if (-not $known -and -not $knownExtras.ContainsKey($extra)) {
                        $knownExtras[$extra] = $true
                        $extraCase = [pscustomobject]@{
                            Id = $extra
                            Kind = "mstest"
                            Module = $batchCases[0].Module
                            Project = $batchCases[0].Project
                            Family = ""
                            Scopes = @()
                            Requirements = @()
                            Claim = ""
                        }
                        $checks.Add((New-Check -Case $extraCase -Outcome "failed" -Reason "selection-expanded" -DurationMs 0 -Group $groupName))
                    }
                }
                Apply-ProcessDisposition $batchDisposition $dispatch
                $batchIndex++
            }
            foreach ($case in @($externalRunnable)) {
                if ($dispatch.StopDispatch) {
                    $checks.Add((New-Check -Case $case -Outcome "not-executed" -Reason $dispatch.StopReason -DurationMs 0 -Group $groupName))
                    continue
                }
                $caseTimeout = Get-GroupTimeoutSeconds -CaseCount 1 -Override $parsed.TimeoutSeconds
                if ([int] $parsed.TimeoutSeconds -le 0 -and [int] $case.TimeoutSeconds -gt 0) { $caseTimeout = [int] $case.TimeoutSeconds }
                [Console]::Out.WriteLine("EXTERNAL id=$($case.Id) timeoutSeconds=$caseTimeout")
                $external = Invoke-ExternalCheck -Case $case -RunDirectory $runDirectory -CancelFile $cancelFile -TimeoutSeconds $caseTimeout -ExportBinary $exportBinary
                if ($external.Command) { $commands.Add($external.Command) }
                if ($external.Missing) {
                    $checks.Add((New-Check -Case $case -Outcome "blocked" -Reason "runner-missing" -DurationMs 0 -Group $groupName -Artifacts $external.Artifacts))
                    continue
                }
                $externalRun = $external.Run
                $externalDisposition = Get-ProcessDisposition $externalRun
                if ($externalDisposition.Kind -ne "exit") {
                    $checks.Add((New-Check -Case $case -Outcome $externalDisposition.Outcome -Reason $externalDisposition.Kind -DurationMs $external.DurationMs -Group $groupName -Artifacts $external.Artifacts))
                    Apply-ProcessDisposition $externalDisposition $dispatch
                }
                elseif ($externalRun.ExitCode -eq 0) {
                    $checks.Add((New-Check -Case $case -Outcome "passed" -Reason "exit:0" -DurationMs $external.DurationMs -Group $groupName -Artifacts $external.Artifacts))
                }
                elseif ($externalRun.ExitCode -eq 2) {
                    $checks.Add((New-Check -Case $case -Outcome "blocked" -Reason "runner-exit:2" -DurationMs $external.DurationMs -Group $groupName -Artifacts $external.Artifacts))
                }
                else {
                    $checks.Add((New-Check -Case $case -Outcome "failed" -Reason "exit:$($externalRun.ExitCode)" -DurationMs $external.DurationMs -Group $groupName -Artifacts $external.Artifacts))
                }
            }
            $groupWatch.Stop()
            $groupOutcomes = [System.Collections.Generic.List[string]]::new()
            foreach ($check in @($checks)) {
                if ([string] $check["group"] -eq $groupName) { $groupOutcomes.Add([string] $check["outcome"]) }
            }
            $groupOutcome = "passed"
            foreach ($outcome in @($groupOutcomes)) {
                if ((Get-OutcomeRank $outcome) -gt (Get-OutcomeRank $groupOutcome)) { $groupOutcome = $outcome }
            }
            $groupReports.Add((New-GroupReport -Name $groupName -Outcome $groupOutcome -DurationMs ([int] $groupWatch.ElapsedMilliseconds) -Filters $filters -Commands $commands -TrxPaths $trxPaths))
        }
    }
    finally {
        if ($script:HandlerAdded) {
            Unregister-CancelSignal
            $script:HandlerAdded = $false
        }
    }

    if ($runDirectory) { $userDataAfter = Get-GameUserDataStamp }
    $userDataUnchanged = ($userDataBefore -eq $userDataAfter) -and ($userDataBefore -ne "unreadable")
    $userDataIsolationOutcome = if ($userDataUnchanged) { "passed" } else { "blocked" }
    $userDataIsolationReason = if ($userDataUnchanged) {
        "unchanged"
    }
    elseif ($userDataBefore -eq "unreadable" -or $userDataAfter -eq "unreadable") {
        "user-data-unreadable"
    }
    else {
        "user-data-changed"
    }
    Add-PrepStep -Preparation $preparation -Name "player-user-data-isolation" -Outcome $userDataIsolationOutcome -Reason $userDataIsolationReason -DurationMs 0 -Command $null
    $exitCode = Get-ExitCode -Checks $checks -CleanupFailed:($dispatch.CleanupFailed) -UserCancel:($dispatch.UserCancel) -PrepTimedOut:($dispatch.PrepTimedOut) -RequiredBuildBlocked:($parsed.BuildGame -and $gameBlocked) -RequiredBuildFailed:($gameOutcome -eq 'failed') -IsolationFailed:(-not $userDataUnchanged)
    $environment = New-Map
    Add-Map $environment "dotnetSdk" $sdk | Out-Null
    Add-Map $environment "os" ([System.Environment]::OSVersion.VersionString) | Out-Null
    Add-Map $environment "powershell" $PSVersionTable.PSVersion.ToString() | Out-Null
    Add-Map $environment "configuration" $parsed.Configuration | Out-Null
    Add-Map $environment "projects" $projectEvidence | Out-Null
    Add-Map $environment "godot" $godotState | Out-Null
    Add-Map $environment "godotVersion" $godotVersion | Out-Null
    Add-Map $environment "contentVersion" $contentIdentity.Version | Out-Null
    Add-Map $environment "contentSha256" $contentIdentity.Sha256 | Out-Null
    Add-Map $environment "gameAssembly" $gameAssembly | Out-Null
    Add-Map $environment "gameAssemblySha256" (Get-Sha256File $gameAssembly) | Out-Null
    Add-Map $environment "gameAssemblyReason" $gameAssemblyReason | Out-Null
    Add-Map $environment "gameUserDataBefore" $userDataBefore | Out-Null
    Add-Map $environment "gameUserDataAfter" $userDataAfter | Out-Null
    Add-Map $environment "gameUserDataUnchanged" $userDataUnchanged | Out-Null
    $engineIsolation = if ($script:PerCheckAppDataUsed) { "per-check-appdata" } else { "not-started" }
    Add-Map $environment "engineIsolation" $engineIsolation | Out-Null
    $overrideCfg = Get-CachedOverrideCfg
    Add-Map $environment "overrideCfgPresent" ([bool] $overrideCfg.Present) | Out-Null
    Add-Map $environment "overrideCfgSha256" ([string] $overrideCfg.Sha256) | Out-Null
    $selection = New-RequestedSelection $parsed
    $caseIds = New-List
    foreach ($case in $selected) { $caseIds.Add($case.Id) }
    $selection["caseIds"] = $caseIds
    Publish-Run -Parsed $parsed -Preparation $preparation -Environment $environment -Selection $selection -Checks $checks -Groups $groupReports -ExitCode $exitCode -Directory $runDirectory
    [Console]::Out.WriteLine("REPORT=$runDirectory")
    return $exitCode
}

if ($MyInvocation.InvocationName -ne '.') {
$exitCode = 1
$cleanupErrors = @()
try {
    $result = Invoke-Main
    if ($result -isnot [int] -and $result -isnot [long]) {
        [Console]::Error.WriteLine("error: runner returned no exit code")
    }
    else { $exitCode = [int] $result }
}
catch {
    $exitCode = Get-ExitCode -Checks $script:RunChecks -PriorExitCode 1 -UserCancel:(Test-CancelRequested)
    [Console]::Error.WriteLine("error: $($_.Exception.Message)")
    if ($_.InvocationInfo.PositionMessage) { [Console]::Error.WriteLine($_.InvocationInfo.PositionMessage) }
    if ($_.ScriptStackTrace) { [Console]::Error.WriteLine($_.ScriptStackTrace) }
    if ($script:OwnedPid) { Stop-OwnedProcessTree -ProcessId $script:OwnedPid | Out-Null }
    if ($script:RunDirectory) {
        try {
            $summaryPath = Join-Path $script:RunDirectory "summary.json"
            if (Test-Path -LiteralPath $summaryPath) {
                $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json -AsHashtable
                $summary.exitCode = $exitCode
                $summary.preparation.outcome = "failed"
                $summary.preparation.reason = "runner-error"
                $markdown = New-Markdown -Summary $summary
                Write-RunReports -State ([pscustomobject]@{ Directory = $script:RunDirectory; Summary = $summary; Markdown = $markdown; JsonOptions = (New-JsonOptions) }) | Out-Null
            }
            else {
                $preparation = New-Map
                Add-Map $preparation "durationMs" 0 | Out-Null
                Add-Map $preparation "commandCount" 0 | Out-Null
                Add-Map $preparation "outcome" "failed" | Out-Null
                Add-Map $preparation "reason" "runner-error" | Out-Null
                Publish-Run -Parsed $script:Parsed -Preparation $preparation -Checks $script:RunChecks -ExitCode $exitCode -Directory $script:RunDirectory
            }
        }
        catch { }
    }
}
finally {
    try {
        if ($script:HandlerAdded) { Unregister-CancelSignal }
    }
    finally { $cleanupErrors = @(Remove-IsolatedProjects) }
}
if ($cleanupErrors.Count -gt 0) {
    foreach ($cleanupError in $cleanupErrors) { [Console]::Error.WriteLine("error: isolation cleanup $cleanupError") }
    $exitCode = Get-ExitCode -Checks $script:RunChecks -CleanupFailed -PriorExitCode $exitCode
    if ($script:RunDirectory) {
        $logPath = Join-Path $script:RunDirectory 'isolation/cleanup.log'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $logPath) | Out-Null
        $cleanupErrors | Set-Content -LiteralPath $logPath -Encoding utf8
        $summaryPath = Join-Path $script:RunDirectory 'summary.json'
        if (Test-Path -LiteralPath $summaryPath) {
            $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json -AsHashtable
            $summary.exitCode = $exitCode
            $summary.environment.isolationCleanupErrors = $cleanupErrors
            $markdown = New-Markdown -Summary $summary
            Write-RunReports -State ([pscustomobject]@{ Directory = $script:RunDirectory; Summary = $summary; Markdown = $markdown; JsonOptions = (New-JsonOptions) }) | Out-Null
        }
    }
}
exit [int] $exitCode
}
