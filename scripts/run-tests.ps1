# Thin PowerShell 7 entry for discovering, selecting, and running MSTest.
# Classification is read from TestCategory metadata (family:, scope:, requires:),
# not from a handwritten method list. Godot, cross-process, legacy inventory, and CI
# are not registered here.
#
# Exit codes:
#   0 selected required checks passed
#   1 an assertion failed
#   2 blocked (build, discovery, missing environment, cleanup, pending benchmark)
#   3 cancelled or timed out
#   4 invalid arguments, unknown names, or an explicit selection that matches nothing

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$script:Utf8 = [System.Text.UTF8Encoding]::new($false)
try { [Console]::OutputEncoding = $script:Utf8 } catch { }
$OutputEncoding = $script:Utf8

$script:RawArgs = @($args)
$script:Root = Split-Path -Parent $PSScriptRoot
$script:ReaderPath = Join-Path $PSScriptRoot "TestMetadataReader.cs"
$script:CancelRequested = $false
$script:TestHostPath = ""
$script:OwnedPid = 0
$script:RunDirectory = ""
$script:HandlerAdded = $false
$script:OnCancel = $null
$script:Parsed = $null

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
      显式选择后执行。同维度多个取值取并集，类别与范围取交集，同一用例只保留一次。

标记
  用例从已构建程序集的 TestCategory 读取：family:unit、family:headless、scope:<稳定功能范围>、
  requires:godot、requires:process、requires:export、requires:graphical。
  类别按测试实际行为标注，可标在类上。普通 C# 的多模块或会话检查是 headless，不是单元测试。

退出码
  0 所选必需检查都通过
  1 有断言失败
  2 阻塞（构建、发现、缺环境、清理未确认、待实现的 Benchmark）
  3 取消或超时
  4 参数非法、未知名称或明确选择零匹配

边界
  source-link: Train.Tests links production source files and does not prove the deployed Godot assembly or scene wiring.
  coverage: registered MSTest correctness is not all repository validation.
  gap: godot and cross-process runners are not registered (#159).
  gap: legacy runner inventory is not registered (#159).
  gap: CI is not registered (#160).
  pending: benchmark, long, manual, and platform are not empty groups that pass.
  manual: none registered.

结果
  每次执行写入新的 TestResults/run-tests/<id>/summary.json、report.md，并保留原始 TRX 与日志。
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
        List = $false
        Project = "Train.Tests/Train.Tests.csproj"
        Configuration = "Debug"
        TimeoutSeconds = 0
        ResultsRoot = ""
        TestHostPath = ""
        DotnetPath = "dotnet"
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
        "-ResultsRoot" = "ResultsRoot"
        "-TestHostPath" = "TestHostPath"
        "-DotnetPath" = "DotnetPath"
    }
    $seenSingle = @{}
    for ($i = 0; $i -lt $Raw.Count; $i++) {
        if ($parsed.Error) { break }
        $token = [string] $Raw[$i]
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
        if ($token -eq "-Family" -or $token -eq "-Scope" -or $token -eq "-Test") {
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
            if ([string]::IsNullOrWhiteSpace($value) -or ($token -ne "-TimeoutSeconds" -and $value.StartsWith("-"))) {
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
    return "CASE id=$($Case.Id) family=$($Case.Family) scopes=$scopes requirements=$requirements group=$($Case.Group)"
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
    $lines.Add("PENDING benchmark")
    $lines.Add("PENDING long")
    $lines.Add("PENDING manual")
    $lines.Add("PENDING platform")
    $lines.Add("GAP godot")
    $lines.Add("GAP cross-process")
    $lines.Add("GAP legacy-runner-inventory")
    $lines.Add("GAP ci")
    return ($lines -join [Environment]::NewLine)
}

function Convert-Catalog {
    param($Catalog)
    $cases = [System.Collections.Generic.List[object]]::new()
    foreach ($item in @($Catalog.Cases)) {
        $scopes = [System.Collections.Generic.List[string]]::new()
        foreach ($scope in $item.Scopes) { [void] $scopes.Add([string] $scope) }
        $requirements = [System.Collections.Generic.List[string]]::new()
        foreach ($requirement in $item.Requirements) { [void] $requirements.Add([string] $requirement) }
        $group = ""
        if ($scopes.Count -gt 0) { $group = [string] $scopes[0] }
        $cases.Add([pscustomobject]@{
            Id = [string] $item.Id
            Family = [string] $item.Family
            Scopes = $scopes
            Requirements = $requirements
            Group = $group
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

function Get-GitState {
    $commit = "unknown"
    $dirty = $true
    $paths = New-List
    try {
        $commit = (& git -C $script:Root rev-parse HEAD 2>$null | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $commit) { $commit = "unknown" }
        $porcelain = & git -C $script:Root status --porcelain
        $text = @($porcelain | ForEach-Object { [string] $_ }) -join [Environment]::NewLine
        $dirty = -not [string]::IsNullOrWhiteSpace($text)
        if ($dirty) {
            foreach ($line in @($porcelain)) {
                if (-not [string]::IsNullOrWhiteSpace([string] $line)) { $paths.Add(([string] $line).Trim()) }
            }
        }
    }
    catch {
        $commit = "unknown"
        $dirty = $true
    }
    return [pscustomobject]@{ Commit = $commit; Dirty = [bool] $dirty; Paths = $paths }
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
        default { return $false }
    }
}

function Get-GroupTimeoutSeconds {
    param([int] $CaseCount, [int] $Override)
    if ($Override -gt 0) { return $Override }
    # Samples recorded for #158 on this workstation: a warm 4-case unit voxel group and a
    # one-case headless console method each finished in about 2 seconds, mostly process
    # startup. The 90-second floor stays above those samples. The 900-second cap still
    # ends a hung group instead of waiting forever.
    $seconds = 45 + (20 * $CaseCount)
    return [Math]::Min(900, [Math]::Max(90, $seconds))
}

function Initialize-ChildProcess {
    param(
        [string] $FileName,
        [string[]] $Arguments,
        [string] $WorkingDirectory,
        [string] $TempDirectory
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
    foreach ($key in $info.Environment.Keys) {
        if ([string] $key -like "TRAIN_RUN_TESTS_*") { $strip.Add([string] $key) }
    }
    foreach ($key in $strip) { [void] $info.Environment.Remove($key) }
    $info.Environment["TEMP"] = $TempDirectory
    $info.Environment["TMP"] = $TempDirectory
    $info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    $info.Environment["DOTNET_NOLOGO"] = "1"
    $info.Environment["MSBUILDDISABLENODEREUSE"] = "1"
    $info.Environment["DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER"] = "1"
    $info.Environment["UseSharedCompilation"] = "false"
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

function Invoke-OwnedProcess {
    param(
        [string] $FileName,
        [string[]] $Arguments,
        [string] $WorkingDirectory,
        [string] $TempDirectory,
        [int] $TimeoutSeconds,
        [string] $CancelFile,
        [string] $LogPath
    )
    $info = Initialize-ChildProcess -FileName $FileName -Arguments $Arguments -WorkingDirectory $WorkingDirectory -TempDirectory $TempDirectory
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    [void] $process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $script:OwnedPid = $process.Id
    $timedOut = $false
    $cancelled = $false
    $started = [DateTime]::UtcNow
    while (-not $process.HasExited) {
        if ($script:CancelRequested -or ($CancelFile -and (Test-Path -LiteralPath $CancelFile))) {
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
        $pretend = $script:TestHostPath -and $env:TRAIN_RUN_TESTS_FAULT -eq "cleanup-kill"
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
        $results[$id] = [pscustomobject]@{ Outcome = $mapped; DurationMs = $durationMs; Native = $outcome }
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

function New-Summary {
    param($Parsed, $Git, $Environment, $Selection, $Checks, $Groups, $Preparation, [int] $ExitCode, [string] $Directory)
    $counts = @{ passed = 0; failed = 0; blocked = 0; cancelled = 0; "not-executed" = 0 }
    foreach ($check in @($Checks)) {
        $outcome = [string] $check["outcome"]
        if ($counts.ContainsKey($outcome)) { $counts[$outcome]++ }
    }
    $summary = New-Map
    Add-Map $summary "schemaVersion" 1 | Out-Null
    Add-Map $summary "includesAllRepositoryChecks" $false | Out-Null
    Add-Map $summary "cleanCommitProven" ([bool] (-not $Git.Dirty)) | Out-Null
    Add-Map $summary "commit" $Git.Commit | Out-Null
    Add-Map $summary "dirty" ([bool] $Git.Dirty) | Out-Null
    Add-Map $summary "dirtyPaths" $Git.Paths | Out-Null
    Add-Map $summary "sourceLinkBoundary" "Train.Tests links production source files and does not prove the deployed Godot assembly or scene wiring." | Out-Null
    $gaps = New-List
    foreach ($gap in @("godot", "cross-process", "legacy-runner-inventory", "ci", "benchmark", "long", "platform")) { $gaps.Add($gap) }
    Add-Map $summary "migrationGaps" $gaps | Out-Null
    $runners = New-List
    $runners.Add("mstest")
    Add-Map $summary "registeredRunners" $runners | Out-Null
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
    if ($Summary["dirty"]) {
        $lines.Add("工作区有未提交修改。该结果不能证明提交 $($Summary["commit"]) 的干净树已通过。")
    }
    else {
        $lines.Add("工作区干净。提交 $($Summary["commit"])。")
    }
    $lines.Add("")
    $lines.Add($Summary["sourceLinkBoundary"])
    $lines.Add("已登记的 MSTest 正确性不是全仓验证。未登记：Godot、跨进程、旧运行器清单、CI、Benchmark。")
    $lines.Add("")
    $selection = $Summary["selection"]
    if ($selection) {
        $lines.Add("## 选择")
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
        $lines.Add("")
    }
    $environment = $Summary["environment"]
    if ($environment) {
        $lines.Add("## 环境")
        $lines.Add("- sdk $($environment["dotnetSdk"])")
        $lines.Add("- os $($environment["os"])")
        $lines.Add("- powershell $($environment["powershell"])")
        $lines.Add("- godot $($environment["godot"])")
        $lines.Add("")
    }
    $lines.Add("## 结果")
    $counts = $Summary["outcomes"]
    $lines.Add("- 通过 $($counts["passed"])")
    $lines.Add("- 失败 $($counts["failed"])")
    $lines.Add("- 阻塞 $($counts["blocked"])")
    $lines.Add("- 取消 $($counts["cancelled"])")
    $lines.Add("- 未执行 $($counts["notExecuted"])")
    $lines.Add("- 退出码 $($Summary["exitCode"])")
    $lines.Add("")
    $lines.Add("## 检查")
    foreach ($check in @($Summary["checks"])) {
        $lines.Add("- $($check["outcome"]) $($check["id"]) $($check["durationMs"])ms $($check["reason"])")
    }
    if (@($Summary["checks"]).Count -eq 0) { $lines.Add("- 无用例结果") }
    $lines.Add("")
    $lines.Add("## 人工观察")
    $lines.Add("- 无。本入口未登记人工检查。")
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
    }
    else {
        Add-Map $selection "correctness" $false | Out-Null
    }
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
    if (-not $Environment.ContainsKey("os")) { Add-Map $Environment "os" [System.Environment]::OSVersion.VersionString | Out-Null }
    if (-not $Environment.ContainsKey("powershell")) { Add-Map $Environment "powershell" $PSVersionTable.PSVersion.ToString() | Out-Null }
    if ($null -eq $Selection) { $Selection = New-RequestedSelection $Parsed }
    if ($null -eq $Checks) { $Checks = New-List }
    if ($null -eq $Groups) { $Groups = New-List }
    $summary = New-Summary -Parsed $Parsed -Git (Get-GitState) -Environment $Environment -Selection $Selection -Checks $Checks -Groups $Groups -Preparation $Preparation -ExitCode $ExitCode -Directory $Directory
    $markdown = New-Markdown -Summary $summary
    Write-RunReports -State ([pscustomobject]@{ Directory = $Directory; Summary = $summary; Markdown = $markdown; JsonOptions = (New-JsonOptions) }) | Out-Null
}

function Get-ExitCode {
    param($Checks, [switch] $CleanupFailed, [switch] $UserCancel)
    $failed = $false
    $blocked = $false
    $cancelled = $false
    $notExecuted = $false
    foreach ($check in @($Checks)) {
        switch ([string] $check["outcome"]) {
            "failed" { $failed = $true }
            "blocked" { $blocked = $true }
            "cancelled" { $cancelled = $true }
            "not-executed" { $notExecuted = $true }
        }
    }
    if ($failed) { return 1 }
    if ($CleanupFailed) { return 2 }
    if ($UserCancel -or $cancelled) { return 3 }
    if ($blocked -or $notExecuted) { return 2 }
    return 0
}

function New-Check {
    param($Case, [string] $Outcome, [string] $Reason, [int] $DurationMs, [string] $Group)
    $check = New-Map
    Add-Map $check "id" $Case.Id | Out-Null
    Add-Map $check "family" $Case.Family | Out-Null
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
        if ($length + $piece.Length + 1 -gt $limit -and $members.Count -gt 0) {
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
    $rank = @{ "passed" = 0; "not-executed" = 1; "cancelled" = 2; "blocked" = 3; "failed" = 4 }
    if (-not $Merged.ContainsKey($Id)) {
        $Merged[$Id] = $Entry
        return
    }
    $previous = [string] $Merged[$Id].Outcome
    $incoming = [string] $Entry.Outcome
    if ($rank[$incoming] -gt $rank[$previous]) { $Merged[$Id] = $Entry }
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
    $hasSelection = $parsed.Correctness -or $parsed.Families.Count -gt 0 -or $parsed.Scopes.Count -gt 0 -or $parsed.Tests.Count -gt 0
    if (-not $parsed.Help -and -not $parsed.List -and -not $hasSelection) {
        [Console]::Error.WriteLine("error: invalid-argument explicit selection is required")
        [Console]::Out.WriteLine((Get-HelpText -GroupText ""))
        return 4
    }

    $projectPath = Resolve-ExistingPath $parsed.Project
    if (-not (Test-Path -LiteralPath $projectPath)) {
        [Console]::Error.WriteLine("error: invalid-argument project not found $projectPath")
        return 4
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
    $script:CancelRequested = $false
    $script:OnCancel = [ConsoleCancelEventHandler] {
        param($sender, $event)
        $event.Cancel = $true
        $script:CancelRequested = $true
    }
    $script:HandlerAdded = $false
    try {
        [Console]::add_CancelKeyPress($script:OnCancel)
        $script:HandlerAdded = $true
    }
    catch {
        $script:HandlerAdded = $false
    }
    $cancelFile = ""
    if ($runDirectory) { $cancelFile = Join-Path $runDirectory "cancel.request" }

    $buildTimeout = 300
    $tempForBuild = if ($runDirectory) { Join-Path $runDirectory "tmp" } else { [System.IO.Path]::GetTempPath() }
    $buildLog = if ($runDirectory) { Join-Path $runDirectory "prepare.log" } else { [System.IO.Path]::GetTempFileName() }
    $buildWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $buildArgs = @(
        "build", $projectPath,
        "-c", $parsed.Configuration,
        "--nologo",
        "-p:UseSharedCompilation=false"
    )
    $build = Invoke-OwnedProcess -FileName $dotnet -Arguments $buildArgs -WorkingDirectory $script:Root -TempDirectory $tempForBuild -TimeoutSeconds $buildTimeout -CancelFile $cancelFile -LogPath $buildLog
    $buildWatch.Stop()
    if (-not $runDirectory -and (Test-Path -LiteralPath $buildLog)) { Remove-Item -LiteralPath $buildLog -Force -ErrorAction SilentlyContinue }

    $preparation = New-Map
    Add-Map $preparation "durationMs" ([int] $buildWatch.ElapsedMilliseconds) | Out-Null
    Add-Map $preparation "commandCount" 1 | Out-Null
    $buildCommand = New-List
    $buildCommand.Add($dotnet)
    foreach ($arg in $buildArgs) { $buildCommand.Add($arg) }
    Add-Map $preparation "command" $buildCommand | Out-Null

    if ($build.CleanupFailed) {
        Add-Map $preparation "outcome" "blocked" | Out-Null
        Add-Map $preparation "reason" "cleanup" | Out-Null
        [Console]::Error.WriteLine("error: blocked cleanup")
        if ($runDirectory) { Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 2 -Directory $runDirectory }
        return 2
    }
    if ($build.TimedOut -or $build.Cancelled) {
        Add-Map $preparation "outcome" "cancelled" | Out-Null
        Add-Map $preparation "reason" $(if ($build.TimedOut) { "timeout" } else { "cancel" }) | Out-Null
        [Console]::Error.WriteLine("error: cancelled build")
        if ($runDirectory) { Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 3 -Directory $runDirectory }
        return 3
    }
    if ($build.ExitCode -ne 0) {
        Add-Map $preparation "outcome" "blocked" | Out-Null
        Add-Map $preparation "reason" "build" | Out-Null
        [Console]::Error.WriteLine("error: blocked build")
        if ($parsed.Help -or $parsed.List) { return 2 }
        Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 2 -Directory $runDirectory
        return 2
    }
    Add-Map $preparation "outcome" "passed" | Out-Null
    Add-Map $preparation "reason" "reused-for-groups" | Out-Null

    $assemblyPath = Get-ProjectAssemblyPath -ProjectPath $projectPath -Configuration $parsed.Configuration
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        [Console]::Error.WriteLine("error: blocked assembly missing $assemblyPath")
        Add-Map $preparation "outcome" "blocked" | Out-Null
        Add-Map $preparation "reason" "assembly" | Out-Null
        if ($runDirectory) { Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 2 -Directory $runDirectory }
        return 2
    }
    $catalog = Convert-Catalog (Read-TestCatalog -AssemblyPath $assemblyPath)
    if (@($catalog.Errors).Count -gt 0) {
        foreach ($discoveryError in @($catalog.Errors)) { [Console]::Error.WriteLine("error: discovery $discoveryError") }
        Add-Map $preparation "outcome" "blocked" | Out-Null
        Add-Map $preparation "reason" "discovery" | Out-Null
        if ($executing) { Publish-Run -Parsed $parsed -Preparation $preparation -ExitCode 2 -Directory $runDirectory }
        return 2
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
        [Console]::Out.WriteLine("source-link: Train.Tests links production source files and does not prove the deployed Godot assembly or scene wiring.")
        [Console]::Out.WriteLine("coverage: registered MSTest correctness is not all repository validation.")
        [Console]::Out.WriteLine($groupText)
        foreach ($case in $selected) { [Console]::Out.WriteLine((Format-CaseLine -Case $case)) }
        [Console]::Out.WriteLine("SELECTED count=$($selected.Count)")
        return 0
    }

    $git = Get-GitState
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
    $stopDispatch = $false
    $stopReason = ""
    $userCancel = $false
    $cleanupFailed = $false
    try {
        foreach ($groupName in @($byGroup.Keys | Sort-Object)) {
            $groupCases = @($byGroup[$groupName])
            if ($stopDispatch) {
                foreach ($case in $groupCases) {
                    $checks.Add((New-Check -Case $case -Outcome "not-executed" -Reason $stopReason -DurationMs 0 -Group $groupName))
                }
                $groupReports.Add((New-GroupReport -Name $groupName -Outcome "not-executed" -DurationMs 0))
                continue
            }
            $runnable = [System.Collections.Generic.List[object]]::new()
            foreach ($case in $groupCases) {
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

            $timeout = Get-GroupTimeoutSeconds -CaseCount $runnable.Count -Override $parsed.TimeoutSeconds
            [Console]::Out.WriteLine("GROUP name=$groupName cases=$($runnable.Count) timeoutSeconds=$timeout")
            $groupDir = Join-Path $runDirectory (Join-Path "groups" $groupName)
            New-Item -ItemType Directory -Force -Path $groupDir | Out-Null
            $batches = Get-CaseBatches -Cases $runnable
            $groupWatch = [System.Diagnostics.Stopwatch]::StartNew()
            $filters = New-List
            $commands = New-List
            $trxPaths = New-List
            $batchIndex = 0
            $merged = @{}
            $knownExtras = @{}
            foreach ($batch in @($batches)) {
                $batchCases = @($batch.Cases)
                if ($stopDispatch) {
                    foreach ($case in $batchCases) {
                        $checks.Add((New-Check -Case $case -Outcome "not-executed" -Reason $stopReason -DurationMs 0 -Group $groupName))
                    }
                    continue
                }
                $trxName = "results-$batchIndex.trx"
                $testArgs = @(
                    "test", $projectPath,
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
                        $checks.Add((New-Check -Case $case -Outcome "cancelled" -Reason "timeout" -DurationMs $timeout -Group $groupName))
                    }
                    $batchIndex++
                    continue
                }
                $ran = Invoke-OwnedProcess -FileName $fileName -Arguments $argumentList -WorkingDirectory $script:Root -TempDirectory (Join-Path $runDirectory "tmp") -TimeoutSeconds $remaining -CancelFile $cancelFile -LogPath (Join-Path $groupDir "stdout-$batchIndex.log")
                $trxPath = Join-Path $groupDir $trxName
                $trxPaths.Add($trxPath)
                $filters.Add([string] $batch.Filter)
                $command = New-List
                $command.Add($fileName)
                foreach ($arg in $argumentList) { $command.Add($arg) }
                $commands.Add($command)
                $batchTrx = Read-TrxResults -TrxPath $trxPath
                foreach ($id in @($batchTrx.Keys)) { Merge-TrxResult -Merged $merged -Id $id -Entry $batchTrx[$id] }
                foreach ($case in $batchCases) {
                    if ($ran.CleanupFailed) {
                        $checks.Add((New-Check -Case $case -Outcome "blocked" -Reason "cleanup" -DurationMs 0 -Group $groupName))
                    }
                    elseif ($ran.TimedOut -or $ran.Cancelled -or $script:CancelRequested) {
                        $reason = if ($ran.TimedOut) { "timeout" } else { "cancel" }
                        $duration = if ($ran.TimedOut) { $timeout } else { 0 }
                        if ($merged.ContainsKey($case.Id) -and [string] $merged[$case.Id].Outcome -eq "failed") {
                            $native = $merged[$case.Id]
                            $checks.Add((New-Check -Case $case -Outcome "failed" -Reason $native.Native -DurationMs $native.DurationMs -Group $groupName))
                        }
                        else {
                            $checks.Add((New-Check -Case $case -Outcome "cancelled" -Reason $reason -DurationMs $duration -Group $groupName))
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
                        $extraCase = [pscustomobject]@{ Id = $extra; Family = ""; Scopes = @(); Requirements = @() }
                        $checks.Add((New-Check -Case $extraCase -Outcome "failed" -Reason "selection-expanded" -DurationMs 0 -Group $groupName))
                    }
                }
                if ($ran.CleanupFailed) {
                    $cleanupFailed = $true
                    $stopDispatch = $true
                    $stopReason = "cleanup"
                }
                elseif ($ran.Cancelled -or $script:CancelRequested) {
                    $userCancel = $true
                    $stopDispatch = $true
                    $stopReason = "cancel"
                }
                $batchIndex++
            }
            $groupWatch.Stop()
            $groupOutcomes = [System.Collections.Generic.List[string]]::new()
            foreach ($check in @($checks)) {
                if ([string] $check["group"] -eq $groupName) { $groupOutcomes.Add([string] $check["outcome"]) }
            }
            $rank = @{ "passed" = 0; "not-executed" = 1; "cancelled" = 2; "blocked" = 3; "failed" = 4 }
            $groupOutcome = "passed"
            foreach ($outcome in @($groupOutcomes)) {
                if ($rank[$outcome] -gt $rank[$groupOutcome]) { $groupOutcome = $outcome }
            }
            $groupReports.Add((New-GroupReport -Name $groupName -Outcome $groupOutcome -DurationMs ([int] $groupWatch.ElapsedMilliseconds) -Filters $filters -Commands $commands -TrxPaths $trxPaths))
        }
    }
    finally {
        if ($script:HandlerAdded) {
            try { [Console]::remove_CancelKeyPress($script:OnCancel) } catch { }
            $script:HandlerAdded = $false
        }
    }

    $exitCode = Get-ExitCode -Checks $checks -CleanupFailed:$cleanupFailed -UserCancel:$userCancel
    $environment = New-Map
    Add-Map $environment "dotnetSdk" $sdk | Out-Null
    Add-Map $environment "os" [System.Environment]::OSVersion.VersionString | Out-Null
    Add-Map $environment "powershell" $PSVersionTable.PSVersion.ToString() | Out-Null
    Add-Map $environment "configuration" $parsed.Configuration | Out-Null
    Add-Map $environment "project" $projectPath | Out-Null
    Add-Map $environment "assembly" $assemblyPath | Out-Null
    Add-Map $environment "godot" $godotState | Out-Null
    $selection = New-Map
    $familyList = New-List
    foreach ($family in @($parsed.Families)) { $familyList.Add($family) }
    $scopeList = New-List
    foreach ($scope in @($parsed.Scopes)) { $scopeList.Add($scope) }
    $testList = New-List
    foreach ($test in @($parsed.Tests)) { $testList.Add($test) }
    $caseIds = New-List
    foreach ($case in $selected) { $caseIds.Add($case.Id) }
    Add-Map $selection "families" $familyList | Out-Null
    Add-Map $selection "scopes" $scopeList | Out-Null
    Add-Map $selection "tests" $testList | Out-Null
    Add-Map $selection "correctness" ([bool] $parsed.Correctness) | Out-Null
    Add-Map $selection "caseIds" $caseIds | Out-Null
    $summary = New-Summary -Parsed $parsed -Git $git -Environment $environment -Selection $selection -Checks $checks -Groups $groupReports -Preparation $preparation -ExitCode $exitCode -Directory $runDirectory
    $markdown = New-Markdown -Summary $summary
    $options = [System.Text.Json.JsonSerializerOptions]::new()
    $options.WriteIndented = $true
    $options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
    Write-RunReports -State ([pscustomobject]@{ Directory = $runDirectory; Summary = $summary; Markdown = $markdown; JsonOptions = $options }) | Out-Null
    [Console]::Out.WriteLine("REPORT=$runDirectory")
    return $exitCode
}

try {
    $exitCode = Invoke-Main
    if ($exitCode -isnot [int] -and $exitCode -isnot [long]) {
        [Console]::Error.WriteLine("error: runner returned no exit code")
        exit 1
    }
    exit [int] $exitCode
}
catch {
    [Console]::Error.WriteLine("error: $($_.Exception.Message)")
    if ($_.InvocationInfo.PositionMessage) { [Console]::Error.WriteLine($_.InvocationInfo.PositionMessage) }
    if ($_.ScriptStackTrace) { [Console]::Error.WriteLine($_.ScriptStackTrace) }
    if ($script:OwnedPid) { Stop-OwnedProcessTree -ProcessId $script:OwnedPid | Out-Null }
    if ($script:RunDirectory -and -not (Test-Path -LiteralPath (Join-Path $script:RunDirectory "summary.json"))) {
        try {
            $preparation = New-Map
            Add-Map $preparation "durationMs" 0 | Out-Null
            Add-Map $preparation "commandCount" 0 | Out-Null
            Add-Map $preparation "outcome" "blocked" | Out-Null
            Add-Map $preparation "reason" "runner-error" | Out-Null
            Publish-Run -Parsed $script:Parsed -Preparation $preparation -ExitCode 1 -Directory $script:RunDirectory
        }
        catch { }
    }
    exit 1
}
finally {
    if ($script:HandlerAdded -and $script:OnCancel) {
        try { [Console]::remove_CancelKeyPress($script:OnCancel) } catch { }
    }
}
