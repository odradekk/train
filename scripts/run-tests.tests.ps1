# 从统一入口的输出和产物验证选择、隔离、退出状态与进程清理契约。
# 假宿主只证明编排；真实 MSTest 用例另行执行。
# Ctrl+C 用例依赖 Windows 控制台事件，其他宿主将其报告为未验证并返回非零。

param([string] $Only = "")

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -lt 7) { throw "PowerShell 7 is required" }

$root = Split-Path -Parent $PSScriptRoot
$runner = Join-Path $PSScriptRoot "run-tests.ps1"
$pwsh = (Get-Process -Id $PID).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$runStamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
$evidence = Join-Path $root "TestResults/run-tests-harness/$runStamp"
$fixtureRoot = Join-Path $evidence "fixture"
$project = Join-Path $fixtureRoot "Fixture.csproj"
$bareProject = Join-Path $evidence "bare/Bare.csproj"
$brokenProject = Join-Path $evidence "broken/Broken.csproj"
$hostPath = Join-Path $evidence "fake-host.ps1"
$logPath = Join-Path $evidence "harness.log"
$script:failed = 0
$script:blocked = 0
$script:sentinel = $null
$script:owned = [System.Collections.Generic.List[int]]::new()

New-Item -ItemType Directory -Force -Path $evidence | Out-Null
Set-Content -LiteralPath $logPath -Value "harness $(Get-Date -Format o)" -Encoding utf8

function Note([string] $Text) {
    Write-Host $Text
    Add-Content -LiteralPath $logPath -Value $Text -Encoding utf8
}

Note "HARNESS_ROOT=$evidence"

function It {
    param([string] $Name, [scriptblock] $Body)
    if ($Only) {
        $selected = $false
        foreach ($pattern in $Only.Split(",", [System.StringSplitOptions]::RemoveEmptyEntries)) {
            if ($Name -like "*$($pattern.Trim())*") { $selected = $true; break }
        }
        if (-not $selected) { return }
    }
    Note "RUN $Name"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body
        $watch.Stop()
        Note "ok $Name $($watch.ElapsedMilliseconds)ms"
    }
    catch {
        $watch.Stop()
        $message = [string] $_.Exception.Message
        if ($message -like "blocked:*") {
            Note "BLOCKED $Name $($watch.ElapsedMilliseconds)ms : $message"
            $script:blocked++
        }
        else {
            Note "FAIL $Name $($watch.ElapsedMilliseconds)ms : $message"
            $script:failed++
        }
    }
}

function Assert-True([string] $Name, [bool] $Condition) {
    if (-not $Condition) { throw $Name }
}

function Assert-Equal([string] $Name, $Expected, $Actual) {
    if ($Expected -ne $Actual) { throw "$Name expected [$Expected] actual [$Actual]" }
}

function Assert-Contains([string] $Name, [string] $Haystack, [string] $Needle) {
    if ($Haystack -notlike "*$Needle*") { throw "$Name missing [$Needle]" }
}

function As-List($Value) {
    $list = [System.Collections.Generic.List[object]]::new()
    if ($null -eq $Value) { return $list }
    if ($Value -is [string]) {
        $list.Add($Value)
        return $list
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        foreach ($item in $Value) { $list.Add($item) }
        return $list
    }
    $list.Add($Value)
    return $list
}

function New-Results([string] $Name) {
    $path = Join-Path $evidence "runs/$Name"
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Open-Runner {
    param(
        [string[]] $ArgumentList,
        [hashtable] $Environment,
        [string[]] $Unset,
        [int] $WaitMs = 180000,
        [string] $RunnerPath,
        [string] $WorkingDirectory
    )
    if (-not $RunnerPath) { $RunnerPath = $runner }
    if (-not $WorkingDirectory) { $WorkingDirectory = $root }
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $pwsh
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding = $utf8
    $psi.CreateNoWindow = $true
    $arguments = [System.Collections.Generic.List[string]]::new()
    [void] $arguments.Add("-NoProfile")
    [void] $arguments.Add("-File")
    [void] $arguments.Add($RunnerPath)
    foreach ($argument in @( $ArgumentList )) {
        if ($null -ne $argument -and $argument -ne "") { [void] $arguments.Add([string] $argument) }
    }
    foreach ($argument in $arguments) { [void] $psi.ArgumentList.Add($argument) }
    foreach ($name in @($Unset)) {
        if ($psi.Environment.ContainsKey($name)) { [void] $psi.Environment.Remove($name) }
    }
    if ($Environment) {
        foreach ($key in $Environment.Keys) { $psi.Environment[[string] $key] = [string] $Environment[$key] }
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    [void] $process.Start()
    return [pscustomobject]@{
        Process = $process
        OutTask = $process.StandardOutput.ReadToEndAsync()
        ErrTask = $process.StandardError.ReadToEndAsync()
        WaitMs = $WaitMs
        Watch = [System.Diagnostics.Stopwatch]::StartNew()
    }
}

function Close-Runner($Handle) {
    if (-not $Handle.Process.HasExited) {
        if (-not $Handle.Process.WaitForExit($Handle.WaitMs)) {
            & taskkill.exe /PID $Handle.Process.Id /T /F 1>$null 2>$null
            throw "runner $($Handle.Process.Id) exceeded $($Handle.WaitMs)ms"
        }
    }
    $stdout = ""
    $stderr = ""
    if ($Handle.OutTask.Wait(15000)) { $stdout = [string] $Handle.OutTask.Result }
    if ($Handle.ErrTask.Wait(15000)) { $stderr = [string] $Handle.ErrTask.Result }
    $Handle.Watch.Stop()
    $exitCode = $Handle.Process.ExitCode
    $Handle.Process.Dispose()
    return [pscustomobject]@{
        ExitCode = $exitCode
        Stdout = $stdout
        Stderr = $stderr
        DurationMs = $Handle.Watch.ElapsedMilliseconds
    }
}

function Invoke-Runner {
    param(
        [string[]] $ArgumentList,
        [hashtable] $Environment,
        [string[]] $Unset,
        [int] $WaitMs = 180000,
        [string] $RunnerPath,
        [string] $WorkingDirectory
    )
    $handle = Open-Runner -ArgumentList $ArgumentList -Environment $Environment -Unset $Unset -WaitMs $WaitMs -RunnerPath $RunnerPath -WorkingDirectory $WorkingDirectory
    return Close-Runner $handle
}

function Read-Summary([string] $Stdout) {
    $match = [regex]::Match($Stdout, "RUN_DIRECTORY=(.+)")
    if (-not $match.Success) { throw "no RUN_DIRECTORY in stdout" }
    $directory = $match.Groups[1].Value.Trim()
    $summaryPath = Join-Path $directory "summary.json"
    $reportPath = Join-Path $directory "report.md"
    if (-not (Test-Path -LiteralPath $summaryPath)) { throw "missing summary $summaryPath" }
    $raw = [System.IO.File]::ReadAllText($summaryPath)
    $json = $raw | ConvertFrom-Json
    $report = [System.IO.File]::ReadAllText($reportPath)
    return [pscustomobject]@{ Directory = $directory; Raw = $raw; Json = $json; Report = $report }
}

function Get-Checks($Summary) {
    return As-List $Summary.Json.checks
}

function Find-Check($Checks, [string] $Id) {
    foreach ($check in $Checks) {
        if ([string] $check.id -eq $Id) { return $check }
    }
    return $null
}

function Find-Inventory($Summary, [string] $Id) {
    foreach ($item in (As-List $Summary.Json.inventory)) {
        if ([string] $item.id -eq $Id) { return $item }
    }
    return $null
}

function Get-CaseLines([string] $Stdout) {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($match in [regex]::Matches($Stdout, "(?m)^CASE id=(\S+) family=(\S+) scopes=(\S+) requirements=(\S+) group=(\S+)(?: module=(\S+))?(?: claim=(\S+))?\s*$")) {
        $list.Add([pscustomobject]@{
            Id = $match.Groups[1].Value
            Family = $match.Groups[2].Value
            Scopes = $match.Groups[3].Value
            Requirements = $match.Groups[4].Value
            Group = $match.Groups[5].Value
            Module = $match.Groups[6].Value
            Claim = $match.Groups[7].Value
        })
    }
    return $list
}

function Assert-Exit($Result, [int] $Expected) {
    if ($Result.ExitCode -ne $Expected) {
        $tail = $Result.Stderr
        if ($tail.Length -gt 800) { $tail = $tail.Substring($tail.Length - 800) }
        throw "exit $($Result.ExitCode) expected $Expected stderr=$tail"
    }
}

function Stop-Tree([int] $ProcessId) {
    if ($ProcessId -le 0 -or $ProcessId -eq $PID) { return }
    & taskkill.exe /PID $ProcessId /T /F 1>$null 2>$null
}

function Assert-FinalResult($Result, [int] $Expected) {
    Assert-Exit $Result $Expected
    $summary = Read-Summary $Result.Stdout
    Assert-Equal "JSON final exit" $Expected ([int] $summary.Json.exitCode)
    Assert-Contains "Markdown final exit" $summary.Report "- 退出码 $Expected"
    return $summary
}

function Test-Alive([int] $ProcessId) {
    return $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
}

function Ensure-Sentinel {
    if ($script:sentinel -and (Test-Alive $script:sentinel.Id)) { return }
    $script:sentinel = Start-Process -FilePath $pwsh -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 900") -PassThru -WindowStyle Hidden
    $script:owned.Add($script:sentinel.Id)
}

function Read-PidFile([string] $Path) {
    $list = [System.Collections.Generic.List[int]]::new()
    if (-not (Test-Path -LiteralPath $Path)) { return $list }
    foreach ($part in ([System.IO.File]::ReadAllText($Path) -split "[,\s]+")) {
        $number = 0
        if ([int]::TryParse($part, [ref] $number) -and $number -gt 0) { $list.Add($number) }
    }
    return $list
}

function Wait-PidFile([string] $Path, [int] $Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function Wait-RunDirectory([string] $Results, [int] $Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $dirs = @(Get-ChildItem -LiteralPath $Results -Directory -ErrorAction SilentlyContinue)
        if ($dirs.Count -gt 0) { return $dirs[0].FullName }
        Start-Sleep -Milliseconds 20
    }
    return ""
}

function Assert-OverrideUntouched([string] $BeforeHash) {
    $path = Join-Path $root "override.cfg"
    $exists = Test-Path -LiteralPath $path
    if ($BeforeHash -eq "absent") { Assert-True "override.cfg absent" (-not $exists); return }
    Assert-True "override.cfg still present" $exists
    $after = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    Assert-Equal "override.cfg unchanged" $BeforeHash $after
}

function Get-OverrideHash {
    $path = Join-Path $root "override.cfg"
    if (-not (Test-Path -LiteralPath $path)) { return "absent" }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

function Get-GitFacts {
    $commit = (& git -C $root rev-parse HEAD | Out-String).Trim()
    $dirty = $false
    foreach ($line in @(git -C $root status --porcelain)) {
        if (-not [string]::IsNullOrWhiteSpace([string] $line)) { $dirty = $true }
    }
    return [pscustomobject]@{ Commit = $commit; Dirty = $dirty }
}

function Assert-CommonSummary($Summary, [int] $PrepCount = 1) {
    Assert-True "includesAllRepositoryChecks false" ($Summary.Raw -match '"includesAllRepositoryChecks":\s*false')
    Assert-True "registered mstest" ($Summary.Raw -match '"mstest"')
    Assert-True "test assembly boundary present" (-not [string]::IsNullOrWhiteSpace($Summary.Json.testAssemblyBoundary))
    Assert-Contains "report source" $Summary.Report $Summary.Json.testAssemblyBoundary
    Assert-Contains "manual section" $Summary.Report "人工观察"
    $gaps = As-List $Summary.Json.migrationGaps
    foreach ($gap in @("benchmark", "long", "platform")) {
        $found = $false
        foreach ($item in $gaps) { if ([string] $item -eq $gap) { $found = $true } }
        Assert-True "gap $gap" $found
    }
    foreach ($gone in @("ci", "godot", "cross-process", "legacy-runner-inventory")) {
        foreach ($item in $gaps) { Assert-True "closed gap $gone" ([string] $item -ne $gone) }
    }
    $inventory = As-List $Summary.Json.inventory
    Assert-True "inventory" ($inventory.Count -ge 1)
    $unmeasured = 0
    foreach ($item in $inventory) {
        Assert-True "inventory entry $($item.id)" (-not [string]::IsNullOrWhiteSpace([string] $item.entry))
        Assert-True "inventory contract $($item.id)" (-not [string]::IsNullOrWhiteSpace([string] $item.contract))
        Assert-True "inventory boundary $($item.id)" (-not [string]::IsNullOrWhiteSpace([string] $item.boundary))
        Assert-True "inventory destination $($item.id)" (-not [string]::IsNullOrWhiteSpace([string] $item.destination))
        $status = [string] $item.status
        Assert-True "inventory status $($item.id)" (-not [string]::IsNullOrWhiteSpace($status))
        Assert-True "duration status $($item.id)" (-not [string]::IsNullOrWhiteSpace([string] $item.durationStatus))
        if ([string] $item.durationStatus -eq "unmeasured") { $unmeasured++ }
    }
    Assert-True "unmeasured support inventory" ($unmeasured -ge 1)
    Assert-Contains "serial execution" $Summary.Report "串行执行"
    Assert-Contains "isolation policy" $Summary.Report "隔离策略是 per-check-appdata"
    $isolation = ""
    if ($Summary.Json.environment -and $Summary.Json.environment.PSObject.Properties.Name -contains "engineIsolation") {
        $isolation = [string] $Summary.Json.environment.engineIsolation
    }
    Assert-True "isolation records a godot start" ($isolation -eq "" -or $isolation -eq "not-started")
    $overridePath = Join-Path $root "override.cfg"
    $overridePresent = Test-Path -LiteralPath $overridePath
    Assert-Equal "override present" $overridePresent ([bool] $Summary.Json.environment.overrideCfgPresent)
    if ($overridePresent) {
        $overrideHash = (Get-FileHash -LiteralPath $overridePath -Algorithm SHA256).Hash
        Assert-Equal "override sha" $overrideHash ([string] $Summary.Json.environment.overrideCfgSha256)
        Assert-Contains "override present contract" $Summary.Report "present=true"
        Assert-Contains "override sha in report" $Summary.Report $overrideHash
    }
    else {
        Assert-Equal "override sha empty" "" ([string] $Summary.Json.environment.overrideCfgSha256)
        Assert-Contains "override absent contract" $Summary.Report "present=false"
    }
    $runners = As-List $Summary.Json.registeredRunners
    $runnerText = ($runners | ForEach-Object { [string] $_ }) -join ","
    Assert-Contains "mstest runner" $runnerText "mstest"
    if ([string] $Summary.Json.runnerCatalog -eq "none") {
        Assert-True "none does not claim godot" ($runnerText -notlike "*godot*")
        Assert-True "none does not claim cross-process" ($runnerText -notlike "*cross-process*")
    }
    $manual = As-List $Summary.Json.manual
    Assert-Equal "manual empty" 0 $manual.Count
    $git = Get-GitFacts
    Assert-Equal "commit" $git.Commit ([string] $Summary.Json.commit)
    Assert-Equal "dirty" $git.Dirty ([bool] $Summary.Json.dirty)
    Assert-Equal "end commit" $git.Commit ([string] $Summary.Json.gitAtEnd.commit)
    Assert-Equal "end dirty" $git.Dirty ([bool] $Summary.Json.gitAtEnd.dirty)
    Assert-Equal "identity stable" $false ([bool] $Summary.Json.testedIdentityChanged)
    Assert-Equal "identity known" $true ([bool] $Summary.Json.identityKnown)
    Assert-Equal "end identity known" $true ([bool] $Summary.Json.gitAtEnd.identityKnown)
    Assert-Equal "clean proven" (-not $git.Dirty) ([bool] $Summary.Json.cleanCommitProven)
    Assert-Equal "prep count" $PrepCount ([int] $Summary.Json.preparation.commandCount)
    if ($Summary.Json.environment -and $Summary.Json.environment.PSObject.Properties.Name -contains "gameUserDataUnchanged") {
        $isolationStep = @($Summary.Json.preparation.steps | Where-Object name -eq "player-user-data-isolation")
        Assert-Equal "one passed isolation step" 1 $isolationStep.Count
        Assert-Equal "isolation passed" "passed" $isolationStep[0].outcome
        Assert-Equal "isolation unchanged" "unchanged" $isolationStep[0].reason
    }
}

function New-CheckSpec {
    param(
        [string] $Id,
        [string] $Group,
        [string] $ScriptPath,
        [string[]] $Arguments,
        [string[]] $Requirements = @(),
        [bool] $NeedsImport = $false,
        [bool] $NeedsGameBuild = $false,
        [bool] $NeedsExport = $false,
        [bool] $NeedsContentInput = $false,
        [int] $TimeoutSeconds = 30,
        [string] $Scope = "orchestration"
    )
    $map = [System.Collections.Generic.Dictionary[string, object]]::new()
    $map["id"] = $Id
    $map["family"] = "headless"
    $scopeList = [System.Collections.Generic.List[string]]::new()
    $scopeList.Add($Scope)
    $map["scopes"] = $scopeList
    $requirementList = [System.Collections.Generic.List[string]]::new()
    foreach ($requirement in @($Requirements)) { if ($requirement) { $requirementList.Add($requirement) } }
    $map["requirements"] = $requirementList
    $map["group"] = $Group
    $map["script"] = $ScriptPath
    $args = [System.Collections.Generic.List[string]]::new()
    foreach ($argument in @($Arguments)) { $args.Add([string] $argument) }
    $map["arguments"] = $args
    $map["claim"] = "orchestration"
    $map["needsImport"] = [bool] $NeedsImport
    $map["needsGameBuild"] = [bool] $NeedsGameBuild
    $map["needsExport"] = [bool] $NeedsExport
    $map["needsContentInput"] = [bool] $NeedsContentInput
    $map["timeoutSeconds"] = $TimeoutSeconds
    return $map
}

# 将检查和可选库存写成 JSON；PowerShell 编码器能处理夹具中的哈希表及其包装对象。
function Write-ExternalCatalog([string] $Path, [object[]] $Checks, [object[]] $Inventory = $null) {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($check in @($Checks)) { $list.Add($check) }
    $root = [System.Collections.Generic.Dictionary[string, object]]::new()
    $root["checks"] = $list
    $root["feedback"] = ""
    if ($null -ne $Inventory) { $root["inventory"] = $Inventory }
    $json = ConvertTo-Json -InputObject $root -Depth 10
    [System.IO.File]::WriteAllText($Path, $json)
}

function Get-AppDataStamp {
    $dir = Join-Path $env:APPDATA "Godot\app_userdata\Train"
    if (-not (Test-Path -LiteralPath $dir)) { return "absent" }
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $dir -Recurse -File -Force | Sort-Object -Property FullName)) {
        $relative = $file.FullName.Substring($dir.Length).TrimStart('\', '/')
        $lines.Add("$relative|$($file.Length)|$($file.LastWriteTimeUtc.Ticks)")
    }
    if ($lines.Count -eq 0) { return "empty" }
    $bytes = $utf8.GetBytes(($lines -join "`n"))
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
}

# Expected family / comma-separated scopes / group / optional comma-separated requirements.
# Scope、分组和环境要求是彼此独立的清单契约。
$expected = @{
    VoxelCubeUvTests = "unit/voxels,content/voxels"
    VoxelCollisionDefinitionTests = "unit/content,voxels/content"
    "Train.Core.Tests.Simulation.SplitMix64Tests" = "unit/simulation/simulation"
    KeyedRandomTests = "unit/simulation/simulation"
    SimulationCalculationTests = "unit/simulation/simulation"
    SimulationCheckpointTests = "unit/simulation/simulation"
    SimulationCommandEncodingTests = "unit/simulation/simulation"
    SimulationCommandLogTests = "unit/simulation/simulation"
    SimulationSessionLoggingTests = "headless/simulation/simulation"
    SimulationDriverTests = "unit/simulation/simulation"
    SimulationDriverDeterminismTests = "unit/simulation/simulation"
    SimulationProcessTests = "headless/simulation/simulation/process"
    SimulationSchedulerTests = "unit/simulation/simulation"
    SimulationSessionTests = "unit/simulation/simulation"
    SimulationStateCodecTests = "unit/simulation/simulation"
    BlueprintSchemaTests = "unit/content/content"
    ModManifestTests = "unit/content/content"
    ModDiscoveryTests = "unit/content/content"
    ContentPackageWalkTests = "unit/content/content"
    ExternalPathTests = "unit/content/content"
    IsolatedBuiltInContentTests = "unit/content/content"
    ContentImageTests = "unit/content/content"
    ContentPublicationTests = "headless/content,composites/content"
    DesignAttributeTests = "unit/content/content"
    MarketSettlementSchemaTests = "headless/content,markets,settlements/content"
    MarketSettlementPublicationTests = "headless/content,markets,settlements/content"
    HumanLogFormatTests = "unit/logging/logging"
    RingBufferLogSinkTests = "unit/logging/logging"
    StructuredLoggingTests = "unit/logging/logging"
    StructuredLoggingConcurrencyTests = "unit/logging/logging"
    ConsoleCommandParseTests = "unit/console/console"
    VoxelIdentityTests = "unit/identity/identity"
    FileSaveStoreTests = "headless/saves/saves"
    CompositeContentTests = "headless/composites/composites"
    ModLoadingTests = "headless/content/content"
    ContentReloaderTests = "headless/content/content"
    ConsoleCommandDispatchTests = "headless/console/console"
}

function Write-Project([string] $Directory, [string] $Name, [string] $Csproj, [string] $Source) {
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $Directory "$Name.csproj"), $Csproj, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $Directory "$Name.cs"), $Source, $utf8)
}

$sdk = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="MSTest.TestAdapter" Version="3.6.3" />
    <PackageReference Include="MSTest.TestFramework" Version="3.6.3" />
    <Compile Remove="scripts/**" />
  </ItemGroup>
</Project>
'@

$fixtureSource = @'
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fixture;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:voxels")]
public sealed class VoxelRuleTests
{
    [TestMethod]
    public void Adds_two_integers() => Assert.AreEqual(4, 2 + 2);
}

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:voxels")]
public sealed class VoxelSessionTests
{
    [TestMethod]
    public void Session_round_trip() => Assert.AreEqual("voxel", "voxel");
}

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:voxels")]
[TestCategory("scope:content")]
public sealed class SharedRuleTests
{
    [TestMethod]
    public void Counts_once() => Assert.AreEqual(1, 1);
}

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content")]
public sealed class ContentRuleTests
{
    [TestMethod]
    public void Accepts_token() => Assert.AreEqual("ok", "ok");
}

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:content")]
public sealed class ContentFailTests
{
    [TestMethod]
    public void Expects_two() => Assert.AreEqual(1, 2);
}

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:persistence")]
public sealed class PersistPassTests
{
    [TestMethod]
    public void Restores_marker() => Assert.AreEqual("saved", "saved");
}

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:railway")]
[TestCategory("requires:godot")]
public sealed class RailNeedTests
{
    [TestMethod]
    public void Would_launch() => Assert.AreEqual(true, true);
}

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:railway")]
[TestCategory("requires:graphical")]
public sealed class GraphicalNeedTests
{
    [TestMethod]
    public void Needs_a_window() => Assert.AreEqual(true, true);
}

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:rows")]
public sealed class DataMethodTests
{
    [DataTestMethod]
    [DataRow(1)]
    public void DataTestMethod_is_listed(int value) => Assert.Fail("data test failure");
}

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:hold")]
public sealed class HoldTests
{
    [TestMethod]
    public void Holds_the_runner() => Assert.AreEqual(1, 1);

    [TestMethod]
    public void Zzz_not_started() => Assert.AreEqual(1, 1);
}

[TestClass]
[TestCategory("family:headless")]
[TestCategory("scope:later")]
[TestCategory("requires:process")]
public sealed class LaterTests
{
    [TestMethod]
    public void Finishes() => Assert.AreEqual(1, 1);
}

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:identity")]
public sealed class LeftNameTests
{
    [TestMethod]
    public void Shared_name() => Assert.AreEqual(1, 1);
}

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:identity")]
public sealed class RightNameTests
{
    [TestMethod]
    public void Shared_name() => Assert.AreEqual(1, 1);
}
'@

Write-Project $fixtureRoot "Fixture" $sdk $fixtureSource
Write-Project (Join-Path $evidence "bare") "Bare" $sdk @'
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Bare;
[TestClass]
public sealed class BareTests
{
    [TestMethod]
    public void Has_no_category() => Assert.AreEqual(1, 1);
}
'@
Write-Project (Join-Path $evidence "broken") "Broken" @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@ @'
public sealed class Broken
{
    public void Build() { int x = ; }
}
'@

@'
$ErrorActionPreference = "Stop"
$mode = [string]$env:TRAIN_HARNESS_MODE
$log = [string]$env:TRAIN_HARNESS_LOG
$pidFile = [string]$env:TRAIN_HARNESS_PID_FILE
$filter = ""
$results = ""
$logger = ""
for ($i = 0; $i -lt $args.Count; $i++) {
    $token = [string]$args[$i]
    if ($token -eq "--filter" -and ($i + 1) -lt $args.Count) { $filter = [string]$args[$i + 1] }
    if ($token -eq "--results-directory" -and ($i + 1) -lt $args.Count) { $results = [string]$args[$i + 1] }
    if ($token -eq "--logger" -and ($i + 1) -lt $args.Count) { $logger = [string]$args[$i + 1] }
}
$trxName = "results.trx"
if ($logger -match "LogFileName=([^;]+)") { $trxName = $Matches[1] }
$ids = [System.Collections.Generic.List[string]]::new()
foreach ($part in ($filter -split "\|")) {
    if ($part -match "FullyQualifiedName=(.+)$") { $ids.Add($Matches[1].Trim()) }
}
if ($log) {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("BEGIN")
    $lines.Add("MODE=$mode")
    $lines.Add("FILTER=$filter")
    $lines.Add("TEMP=$env:TEMP")
    $lines.Add("TMP=$env:TMP")
    $lines.Add("FAULT=$env:TRAIN_RUN_TESTS_FAULT")
    $lines.Add("NOLOGO=$env:DOTNET_NOLOGO")
    $lines.Add("REUSE=$env:MSBUILDDISABLENODEREUSE")
    $lines.Add("SERVER=$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER")
    $lines.Add("TELEMETRY=$env:DOTNET_CLI_TELEMETRY_OPTOUT")
    $lines.Add("SHARED=$env:UseSharedCompilation")
    foreach ($arg in $args) { $lines.Add("ARG=$arg") }
    $lines.Add("END")
    Add-Content -LiteralPath $log -Value ($lines -join [Environment]::NewLine) -Encoding utf8
}
if (($mode -eq "sleep" -or $mode -eq "fail-sleep") -and $filter -match "Holds_the_runner") {
    if ($mode -eq "fail-sleep" -and $results) {
        New-Item -ItemType Directory -Force -Path $results | Out-Null
        $failedXml = '<?xml version="1.0" encoding="utf-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><TestDefinitions><UnitTest name="Holds_the_runner" id="1"><TestMethod className="Fixture.HoldTests" name="Holds_the_runner" /></UnitTest></TestDefinitions><Results><UnitTestResult testId="1" testName="Holds_the_runner" outcome="Failed" duration="00:00:00.0100000" /></Results></TestRun>'
        [System.IO.File]::WriteAllText((Join-Path $results $trxName), $failedXml)
    }
    $hostPwsh = (Get-Process -Id $PID).Path
    $child = Start-Process -FilePath $hostPwsh -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 300") -PassThru -WindowStyle Hidden
    if ($pidFile) { Set-Content -LiteralPath $pidFile -Value "$PID,$($child.Id)" -Encoding ascii }
    Start-Sleep -Seconds 300
    exit 0
}
if ($mode -eq "fail-then-pass" -or $mode -eq "pass-then-fail") {
    if ($results -and $ids.Count -gt 0) {
        New-Item -ItemType Directory -Force -Path $results | Out-Null
        $id = $ids[0]
        $dot = $id.LastIndexOf(".")
        $class = $id.Substring(0, $dot)
        $name = $id.Substring($dot + 1)
        $first = "Failed"
        $second = "Passed"
        if ($mode -eq "pass-then-fail") { $first = "Passed"; $second = "Failed" }
        $xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?><TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><TestDefinitions><UnitTest name=`"$name`" id=`"1`"><TestMethod className=`"$class`" name=`"$name`" /></UnitTest></TestDefinitions><Results><UnitTestResult testId=`"1`" testName=`"$name`" outcome=`"$first`" duration=`"00:00:00.1000000`" /><UnitTestResult testId=`"1`" testName=`"$name`" outcome=`"$second`" duration=`"00:00:00.0500000`" /></Results></TestRun>"
        [System.IO.File]::WriteAllText((Join-Path $results $trxName), $xml)
    }
    exit 0
}
if ($mode -eq "extra") { $ids.Add("Fixture.NotSelected.ExtraMethod") }
if ($mode -eq "omit" -and $ids.Count -gt 1) {
    $onlyId = $ids[0]
    $ids = [System.Collections.Generic.List[string]]::new()
    $ids.Add($onlyId)
}
if ($results) {
    New-Item -ItemType Directory -Force -Path $results | Out-Null
    $defs = [System.Text.StringBuilder]::new()
    $rows = [System.Text.StringBuilder]::new()
    $n = 1
    foreach ($id in $ids) {
        $dot = $id.LastIndexOf(".")
        $class = $id.Substring(0, $dot)
        $name = $id.Substring($dot + 1)
        [void]$defs.Append("<UnitTest name=`"$name`" id=`"$n`"><TestMethod className=`"$class`" name=`"$name`" /></UnitTest>")
        [void]$rows.Append("<UnitTestResult testId=`"$n`" testName=`"$name`" outcome=`"Passed`" duration=`"00:00:00.0100000`" />")
        $n++
    }
    $xml = "<?xml version=`"1.0`" encoding=`"utf-8`"?><TestRun xmlns=`"http://microsoft.com/schemas/VisualStudio/TeamTest/2010`"><TestDefinitions>$defs</TestDefinitions><Results>$rows</Results></TestRun>"
    [System.IO.File]::WriteAllText((Join-Path $results $trxName), $xml)
}
exit 0
'@ | Set-Content -LiteralPath $hostPath -Encoding utf8

$externalHost = Join-Path $evidence "fake-external.ps1"
@'
param(
    [string] $Mode = "pass",
    [string] $PidFile = "",
    [string] $UserData = "",
    [string] $Lock = "",
    [string] $Id = "",
    [string] $ExportBinary = ""
)
$ErrorActionPreference = "Stop"
switch ($Mode) {
    "hold" {
        [Console]::Out.WriteLine("APPDATA=$env:APPDATA")
        [Console]::Out.Flush()
        $hostPwsh = (Get-Process -Id $PID).Path
        $first = Start-Process -FilePath $hostPwsh -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 120") -PassThru -WindowStyle Hidden
        $second = Start-Process -FilePath $hostPwsh -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 120") -PassThru -WindowStyle Hidden
        if ($PidFile) { Set-Content -LiteralPath $PidFile -Value "$PID,$($first.Id),$($second.Id)" -Encoding ascii }
        Start-Sleep -Seconds 120
        exit 0
    }
    "fail" {
        Write-Output "FAIL-KEPT"
        exit 1
    }
    "marker" {
        if ($UserData) {
            New-Item -ItemType Directory -Force -Path $UserData | Out-Null
            Set-Content -LiteralPath (Join-Path $UserData "started.txt") -Value "started" -Encoding ascii
        }
        exit 0
    }
    "export-path" {
        Write-Output "EXPORT_BINARY=$ExportBinary"
        if (-not (Test-Path -LiteralPath $ExportBinary -PathType Leaf)) { exit 8 }
        exit 0
    }
    "content-input" {
        $content = Join-Path $UserData "content-input"
        $required = @(
            "initial/voxels/grain.voxels.json",
            "initial/voxels/grain/placeholder.png",
            "initial/markets/smoke.market-tables.json",
            "initial/settlement-types/smoke.settlement-types.json",
            "added/voxels/grain.voxels.json",
            "added/voxels/grain/placeholder.png",
            "added/markets/smoke.market-tables.json",
            "added/settlement-types/smoke.settlement-types.json",
            "added/voxels/smoke-ore.voxels.json"
        )
        foreach ($relative in $required) {
            if (-not (Test-Path -LiteralPath (Join-Path $content $relative) -PathType Leaf)) { exit 8 }
        }
        if (Test-Path -LiteralPath (Join-Path $content "initial/devblueprints")) { exit 9 }
        if (Test-Path -LiteralPath (Join-Path $content "initial/fixtures")) { exit 9 }
        $expected = @($required | ForEach-Object { [System.IO.Path]::GetFullPath((Join-Path $content $_)) })
        $actual = @(Get-ChildItem -LiteralPath $content -Recurse -File | ForEach-Object { $_.FullName })
        if ($actual.Count -ne $expected.Count -or @($actual | Where-Object { $_ -notin $expected }).Count -gt 0) { exit 9 }
        Write-Output "CONTENT_INPUT=$content"
        exit 0
    }
    "mutate-player-data" {
        $player = Join-Path $env:APPDATA "Godot/app_userdata/Train"
        New-Item -ItemType Directory -Force -Path $player | Out-Null
        Set-Content -LiteralPath (Join-Path $player "mutated.txt") -Value "changed" -Encoding ascii
        exit 0
    }
    "isolate" {
        [Console]::Out.WriteLine("APPDATA=$env:APPDATA")
        [Console]::Out.Flush()
        $inheritedSwitch = if ([string]::IsNullOrEmpty($env:TRAIN_FIXTURE_INHERITED_SWITCH)) { "empty" } else { "set" }
        Write-Output "INHERITED_SWITCH=$inheritedSwitch"
        Write-Output "TEMP=$env:TEMP"
        if ($UserData) {
            New-Item -ItemType Directory -Force -Path $UserData | Out-Null
            Set-Content -LiteralPath (Join-Path $UserData "marker.txt") -Value "only-here" -Encoding ascii
        }
        exit 0
    }
    "serial" {
        $stream = $null
        try {
            $stream = [System.IO.File]::Open($Lock, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        }
        catch {
            Write-Output "OVERLAP $Id"
            exit 9
        }
        Start-Sleep -Milliseconds 800
        $stream.Dispose()
        Remove-Item -LiteralPath $Lock -Force
        Write-Output "CLEAR $Id"
        exit 0
    }
    default {
        Write-Output "PASS"
        exit 0
    }
}
'@ | Set-Content -LiteralPath $externalHost -Encoding utf8

$fakeRoot = Join-Path $evidence "fake-tool"
New-Item -ItemType Directory -Force -Path $fakeRoot | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $fakeRoot "fake-tool.csproj") -Encoding utf8
@'
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
internal static class Program
{
    private static int Main(string[] args)
    {
        string role = Environment.GetEnvironmentVariable("FAKE_ROLE") ?? "";
        string joined = string.Join(" ", args);
        if (role == "godot")
        {
            string mode = Environment.GetEnvironmentVariable("FAKE_GODOT") ?? "";
            if (mode == "version-sleep" && joined.IndexOf("--version", StringComparison.OrdinalIgnoreCase) >= 0) { Thread.Sleep(60000); return 0; }
            if (mode == "version-fail" && joined.IndexOf("--version", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (mode == "sleep" && joined.IndexOf("--import", StringComparison.OrdinalIgnoreCase) >= 0) { Thread.Sleep(60000); return 0; }
            if (mode == "fail" && joined.IndexOf("--import", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (joined.IndexOf("--export-release", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string log = Environment.GetEnvironmentVariable("FAKE_EXPORT_LOG");
                if (!string.IsNullOrEmpty(log)) File.AppendAllText(log, "APPDATA=" + Environment.GetEnvironmentVariable("APPDATA") + Environment.NewLine + joined + Environment.NewLine);
                if ((Environment.GetEnvironmentVariable("FAKE_EXPORT") ?? "") == "fail") return 1;
                string output = args[args.Length - 1];
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.WriteAllText(output, "fake export");
                return 0;
            }
            Console.WriteLine("4.7.2.fake");
            return 0;
        }
        if (joined.IndexOf("Train.csproj", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string game = Environment.GetEnvironmentVariable("FAKE_GAME_BUILD") ?? "";
            if (game == "sleep") { Thread.Sleep(60000); return 0; }
            Console.Error.WriteLine("Fixture.cs(1,1): error CS1002: ; expected");
            return 1;
        }
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start);
        process.WaitForExit();
        return process.ExitCode;
    }
}
'@ | Set-Content -LiteralPath (Join-Path $fakeRoot "Program.cs") -Encoding utf8
& dotnet build (Join-Path $fakeRoot "fake-tool.csproj") -c Release --nologo -p:UseSharedCompilation=false | Out-Null
if ($LASTEXITCODE -ne 0) { throw "fake tool build failed" }
$fakeExe = Join-Path $fakeRoot "bin/Release/net10.0/fake-tool.exe"
if (-not (Test-Path -LiteralPath $fakeExe)) { throw "fake tool exe missing" }

It "fast-invalid" {
    $result = Invoke-Runner -ArgumentList @("-Nope") -WaitMs 30000
    Assert-Exit $result 4
    Assert-Contains "token" $result.Stderr "error: invalid-argument"
    Assert-Contains "test assembly" $result.Stdout "test-assembly:"
    Assert-Contains "coverage" $result.Stdout "not all repository validation"
}

It "fast-pending" {
    foreach ($family in @("benchmark", "long", "manual", "platform")) {
        $result = Invoke-Runner -ArgumentList @("-Family", $family) -WaitMs 30000
        Assert-Exit $result 2
        Assert-Contains $family $result.Stderr "error: pending $family"
    }
}

It "fast-explicit" {
    $result = Invoke-Runner -ArgumentList @("-Configuration", "Debug") -WaitMs 30000
    Assert-Exit $result 4
    Assert-Contains "selection" $result.Stderr "explicit selection is required"
    $result = Invoke-Runner -ArgumentList @("-BuildGame") -WaitMs 30000
    Assert-Exit $result 4
    Assert-Contains "build is not a selection" $result.Stderr "explicit selection is required"
}

It "fast-module" {
    $invalid = Invoke-Runner -ArgumentList @("-Module", "unknown", "-List") -WaitMs 30000
    Assert-Exit $invalid 4
    Assert-Contains "invalid module" $invalid.Stderr "unknown-name module unknown"
    $conflict = Invoke-Runner -ArgumentList @("-Module", "core", "-Project", $project, "-List") -WaitMs 30000
    Assert-Exit $conflict 4
    Assert-Contains "project module conflict" $conflict.Stderr "cannot be combined"
    $missingValue = Invoke-Runner -ArgumentList @("-Module") -WaitMs 30000
    Assert-Exit $missingValue 4
    Assert-Contains "module missing value" $missingValue.Stderr "missing value"
}

It "fast-timeout-value" {
    foreach ($value in @("-1", "abc", "0")) {
        $result = Invoke-Runner -ArgumentList @("-TimeoutSeconds", $value) -WaitMs 30000
        Assert-Exit $result 4
        Assert-Contains $value $result.Stderr "error: invalid-argument -TimeoutSeconds"
    }
}

It "fast-duplicate" {
    $result = Invoke-Runner -ArgumentList @("-List", "-List") -WaitMs 30000
    Assert-Exit $result 4
    Assert-Contains "dup" $result.Stderr "duplicate -List"
    $result = Invoke-Runner -ArgumentList @("-Project", "a", "-Project", "b", "-Family", "unit") -WaitMs 30000
    Assert-Exit $result 4
    Assert-Contains "dup project" $result.Stderr "duplicate -Project"
    $result = Invoke-Runner -ArgumentList @("-BuildGame", "-BuildGame", "-Family", "unit") -WaitMs 30000
    Assert-Exit $result 4
    Assert-Contains "dup build" $result.Stderr "duplicate -BuildGame"
}

It "fast-missing" {
    $result = Invoke-Runner -ArgumentList @("-Family") -WaitMs 30000
    Assert-Exit $result 4
    Assert-Contains "missing" $result.Stderr "missing value"
    $result = Invoke-Runner -ArgumentList @("-Family", "-Scope") -WaitMs 30000
    Assert-Exit $result 4
    Assert-Contains "dash" $result.Stderr "missing value"
}

It "fast-dotnet-missing" {
    $result = Invoke-Runner -ArgumentList @("-Family", "unit", "-DotnetPath", "C:\no\such\dotnet.exe") -WaitMs 30000
    Assert-Exit $result 2
    Assert-Contains "dotnet" $result.Stderr "error: blocked dotnet not found"
}

It "fixture-failure-preserved" {
    $results = New-Results "fixture-failure"
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", "none", "-Scope", "content", "-Scope", "persistence", "-ResultsRoot", $results
    ) -WaitMs 600000
    Assert-Exit $result 1
    $summary = Read-Summary $result.Stdout
    Assert-CommonSummary $summary
    $checks = Get-Checks $summary
    $failed = Find-Check $checks "Fixture.ContentFailTests.Expects_two"
    $passed = Find-Check $checks "Fixture.PersistPassTests.Restores_marker"
    $shared = Find-Check $checks "Fixture.SharedRuleTests.Counts_once"
    Assert-True "fail case" ($null -ne $failed)
    Assert-Equal "fail outcome" "failed" $failed.outcome
    Assert-Equal "later pass" "passed" $passed.outcome
    Assert-Equal "shared once" "passed" $shared.outcome
    $seen = @{}
    foreach ($check in $checks) {
        Assert-True "duplicate $($check.id)" (-not $seen.ContainsKey([string]$check.id))
        $seen[[string]$check.id] = $true
    }
    $trx = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter *.trx
    Assert-True "trx exists" ($trx.Count -gt 0)
    $trxText = [System.IO.File]::ReadAllText($trx[0].FullName)
    Assert-Contains "native failed" $trxText "Expects_two"
    Assert-Contains "native outcome" $trxText "Failed"
    Note "EVIDENCE fixture-failure $($summary.Directory) $($result.DurationMs)ms"
}

It "fixture-godot-blocked" {
    $results = New-Results "fixture-godot"
    $parent = $env:GODOT
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-Family", "headless", "-Scope", "voxels", "-Scope", "railway", "-ResultsRoot", $results
    ) -Unset @("GODOT") -WaitMs 300000
    Assert-Equal "parent godot" $parent $env:GODOT
    Assert-Exit $result 2
    $summary = Read-Summary $result.Stdout
    $checks = Get-Checks $summary
    $session = Find-Check $checks "Fixture.VoxelSessionTests.Session_round_trip"
    $rail = Find-Check $checks "Fixture.RailNeedTests.Would_launch"
    $window = Find-Check $checks "Fixture.GraphicalNeedTests.Needs_a_window"
    Assert-Equal "other group ran" "passed" $session.outcome
    Assert-Equal "godot blocked" "blocked" $rail.outcome
    Assert-Contains "godot reason" $rail.reason "environment:godot"
    Assert-Equal "graphical blocked" "blocked" $window.outcome
    Assert-Contains "graphical reason" $window.reason "environment:graphical"
    Assert-Contains "stdout godot" $result.Stdout "godot=missing"
}

It "fixture-dedup" {
    $results = New-Results "fixture-dedup"
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", "none", "-List", "-Scope", "voxels", "-Scope", "content", "-ResultsRoot", $results
    ) -WaitMs 300000
    Assert-Exit $result 0
    $cases = Get-CaseLines $result.Stdout
    Assert-Equal "dedup count" 5 $cases.Count
    $shared = 0
    foreach ($case in $cases) { if ($case.Id -eq "Fixture.SharedRuleTests.Counts_once") { $shared++ } }
    Assert-Equal "shared once" 1 $shared
    Assert-Contains "selected" $result.Stdout "SELECTED count=5"
    $exact = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-List", "-Test", "Fixture.SharedRuleTests.Counts_once", "-Scope", "voxels", "-Scope", "content"
    ) -WaitMs 180000
    Assert-Exit $exact 0
    Assert-Contains "one" $exact.Stdout "SELECTED count=1"
    $ambiguous = Invoke-Runner -ArgumentList @("-Project", $project, "-List", "-Test", "Shared_name") -WaitMs 180000
    Assert-Exit $ambiguous 4
    Assert-Contains "ambiguous" $ambiguous.Stderr "ambiguous test Shared_name"
    $short = Invoke-Runner -ArgumentList @("-Project", $project, "-List", "-Test", "Adds_two_integers") -WaitMs 180000
    Assert-Exit $short 0
    Assert-Contains "short name" $short.Stdout "CASE id=Fixture.VoxelRuleTests.Adds_two_integers"
    Assert-Contains "short count" $short.Stdout "SELECTED count=1"
}

It "fixture-zero-unknown" {
    $zero = Invoke-Runner -ArgumentList @("-Project", $project, "-Family", "unit", "-Scope", "hold") -WaitMs 180000
    Assert-Exit $zero 4
    Assert-Contains "zero" $zero.Stderr "error: zero-match"
    $match = [regex]::Match($zero.Stdout, "RUN_DIRECTORY=(.+)")
    Assert-True "zero directory recorded" $match.Success
    $kept = $match.Groups[1].Value.Trim()
    Assert-True "zero summary kept" (Test-Path -LiteralPath (Join-Path $kept "summary.json"))
    $zeroRaw = [System.IO.File]::ReadAllText((Join-Path $kept "summary.json"))
    Assert-True "zero exit recorded" ($zeroRaw -match '"exitCode":\s*4')
    $unknown = Invoke-Runner -ArgumentList @("-Project", $project, "-Scope", "buildings") -WaitMs 180000
    Assert-Exit $unknown 4
    Assert-Contains "unknown" $unknown.Stderr "error: unknown-name scope buildings"
    $family = Invoke-Runner -ArgumentList @("-Project", $project, "-Family", "nope") -WaitMs 180000
    Assert-Exit $family 4
    Assert-Contains "family" $family.Stderr "error: unknown-name family nope"
}

It "fixture-unclassified" {
    $results = New-Results "fixture-bare"
    $result = Invoke-Runner -ArgumentList @("-Project", $bareProject, "-Family", "unit", "-ResultsRoot", $results) -WaitMs 300000
    Assert-Exit $result 2
    Assert-Contains "discovery" $result.Stderr "error: discovery"
    Assert-Contains "missing family" $result.Stderr "missing family"
    Assert-True "not listed as unit" ($result.Stdout -notlike "*CASE id=*")
    $summary = Read-Summary $result.Stdout
    Assert-Equal "no checks" 0 (Get-Checks $summary).Count
    Assert-Equal "prep blocked" "blocked" $summary.Json.preparation.outcome
    Assert-Equal "prep reason" "discovery" $summary.Json.preparation.reason
    Assert-CommonSummary $summary
}

It "fixture-build-failed" {
    $results = New-Results "fixture-broken"
    $result = Invoke-Runner -ArgumentList @("-Project", $brokenProject, "-Family", "unit", "-ResultsRoot", $results) -WaitMs 300000
    Assert-Exit $result 1
    $summary = Read-Summary $result.Stdout
    Assert-Equal "compile failure" "failed" $summary.Json.preparation.outcome
    Assert-Equal "no checks" 0 (Get-Checks $summary).Count
    Assert-True "failure recorded" ($summary.Raw -match '"exitCode":\s*1')
    Assert-CommonSummary $summary
}

It "fixture-batch-env" {
    $results = New-Results "fixture-batch"
    $hostLog = Join-Path $results "host.log"
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-Family", "unit", "-Scope", "voxels",
        "-ResultsRoot", $results, "-TestHostPath", $hostPath
    ) -Environment @{
        TRAIN_HARNESS_MODE = "pass"
        TRAIN_HARNESS_LOG = $hostLog
        TRAIN_RUN_TESTS_FILTER_LIMIT = "40"
        TRAIN_RUN_TESTS_FAULT = "cleanup-kill"
    } -WaitMs 180000
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    Assert-CommonSummary $summary
    $text = [System.IO.File]::ReadAllText($hostLog)
    Assert-Equal "two batches" 2 ([regex]::Matches($text, "BEGIN")).Count
    Assert-Contains "no-build" $text "--no-build"
    Assert-True "fault stripped" ($text -match "(?m)^FAULT=\s*$")
    Assert-Contains "nologo" $text "NOLOGO=1"
    Assert-Contains "reuse" $text "REUSE=1"
    Assert-Contains "server" $text "SERVER=1"
    Assert-Contains "telemetry" $text "TELEMETRY=1"
    Assert-Contains "shared" $text "SHARED=false"
    Assert-Contains "temp isolated" $text $results
    $filters = [regex]::Matches($text, "(?m)^FILTER=(.+)$")
    Assert-Equal "filter count" 2 $filters.Count
    foreach ($filter in $filters) {
        Assert-Equal "one id" 1 ([regex]::Matches($filter.Groups[1].Value, "FullyQualifiedName=")).Count
        Assert-True "exact filter" ($filter.Groups[1].Value -notlike "*~*")
    }
    $command = (As-List $summary.Json.preparation.command) -join " "
    Assert-Contains "build flag" $command "UseSharedCompilation=false"
}

It "fixture-expanded" {
    $results = New-Results "fixture-expanded"
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-Test", "Fixture.VoxelRuleTests.Adds_two_integers",
        "-ResultsRoot", $results, "-TestHostPath", $hostPath
    ) -Environment @{ TRAIN_HARNESS_MODE = "extra"; TRAIN_HARNESS_LOG = (Join-Path $results "host.log") } -WaitMs 180000
    Assert-Exit $result 1
    $checks = Get-Checks (Read-Summary $result.Stdout)
    $selected = Find-Check $checks "Fixture.VoxelRuleTests.Adds_two_integers"
    $extra = Find-Check $checks "Fixture.NotSelected.ExtraMethod"
    Assert-Equal "selected passed" "passed" $selected.outcome
    Assert-Equal "extra failed" "failed" $extra.outcome
    Assert-Equal "expanded" "selection-expanded" $extra.reason
}

It "fixture-missing-result" {
    $results = New-Results "fixture-omit"
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-Family", "unit", "-Scope", "voxels",
        "-ResultsRoot", $results, "-TestHostPath", $hostPath
    ) -Environment @{ TRAIN_HARNESS_MODE = "omit"; TRAIN_HARNESS_LOG = (Join-Path $results "host.log") } -WaitMs 180000
    Assert-Exit $result 2
    $checks = Get-Checks (Read-Summary $result.Stdout)
    $written = Find-Check $checks "Fixture.SharedRuleTests.Counts_once"
    $missing = Find-Check $checks "Fixture.VoxelRuleTests.Adds_two_integers"
    Assert-Equal "written" "passed" $written.outcome
    Assert-Equal "missing" "not-executed" $missing.outcome
    Assert-Equal "reason" "missing-result" $missing.reason
}

It "fixture-timeout" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $results = New-Results "fixture-timeout"
    $pidFile = Join-Path $results "pids.txt"
    $started = [DateTime]::UtcNow
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-Scope", "hold", "-Scope", "later",
        "-TimeoutSeconds", "12", "-ResultsRoot", $results, "-TestHostPath", $hostPath
    ) -Environment @{
        TRAIN_HARNESS_MODE = "sleep"
        TRAIN_HARNESS_LOG = (Join-Path $results "host.log")
        TRAIN_HARNESS_PID_FILE = $pidFile
    } -WaitMs 120000
    $elapsed = ([DateTime]::UtcNow - $started).TotalSeconds
    Assert-True "bounded $elapsed" ($elapsed -lt 90)
    Assert-Exit $result 3
    $checks = Get-Checks (Read-Summary $result.Stdout)
    $hold = Find-Check $checks "Fixture.HoldTests.Holds_the_runner"
    $later = Find-Check $checks "Fixture.LaterTests.Finishes"
    Assert-Equal "hold cancelled" "cancelled" $hold.outcome
    Assert-Equal "hold reason" "timeout" $hold.reason
    Assert-True "hold duration is elapsed milliseconds" ([int] $hold.durationMs -gt 1000)
    Assert-True "hold duration is not the second cap" ([int] $hold.durationMs -ne 12)
    Assert-Equal "later passed" "passed" $later.outcome
    Assert-True "sentinel" (Test-Alive $sentinelId)
    foreach ($owned in (Read-PidFile $pidFile)) { Assert-True "killed $owned" (-not (Test-Alive $owned)) }
    Note "EVIDENCE fixture-timeout $($result.DurationMs)ms"
}

It "fixture-unstarted-batch" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $results = New-Results "fixture-unstarted-batch"
    $pidFile = Join-Path $results "pids.txt"
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-Scope", "hold",
        "-TimeoutSeconds", "8", "-ResultsRoot", $results, "-TestHostPath", $hostPath
    ) -Environment @{
        TRAIN_HARNESS_MODE = "sleep"
        TRAIN_HARNESS_LOG = (Join-Path $results "host.log")
        TRAIN_HARNESS_PID_FILE = $pidFile
        TRAIN_RUN_TESTS_FILTER_LIMIT = "20"
    } -WaitMs 120000
    Assert-Exit $result 3
    $checks = Get-Checks (Read-Summary $result.Stdout)
    $hold = Find-Check $checks "Fixture.HoldTests.Holds_the_runner"
    $later = Find-Check $checks "Fixture.HoldTests.Zzz_not_started"
    Assert-Equal "started batch cancelled" "cancelled" $hold.outcome
    Assert-True "started batch keeps elapsed ms" ([int] $hold.durationMs -gt 1000)
    Assert-Equal "unstarted batch" "not-executed" $later.outcome
    Assert-Equal "unstarted reason" "timeout" $later.reason
    Assert-Equal "unstarted duration" 0 ([int] $later.durationMs)
    Assert-True "sentinel" (Test-Alive $sentinelId)
    foreach ($owned in (Read-PidFile $pidFile)) { Assert-True "killed $owned" (-not (Test-Alive $owned)) }
}

It "fixture-timeout-keeps-failure" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $results = New-Results "fixture-timeout-failure"
    $pidFile = Join-Path $results "pids.txt"
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-Scope", "hold", "-Scope", "later",
        "-TimeoutSeconds", "12", "-ResultsRoot", $results, "-TestHostPath", $hostPath
    ) -Environment @{
        TRAIN_HARNESS_MODE = "fail-sleep"
        TRAIN_HARNESS_LOG = (Join-Path $results "host.log")
        TRAIN_HARNESS_PID_FILE = $pidFile
    } -WaitMs 120000
    Assert-Exit $result 1
    $checks = Get-Checks (Read-Summary $result.Stdout)
    $hold = Find-Check $checks "Fixture.HoldTests.Holds_the_runner"
    $later = Find-Check $checks "Fixture.LaterTests.Finishes"
    Assert-Equal "hold failed" "failed" $hold.outcome
    Assert-Equal "later passed" "passed" $later.outcome
    Assert-True "sentinel" (Test-Alive $sentinelId)
    foreach ($owned in (Read-PidFile $pidFile)) { Assert-True "killed $owned" (-not (Test-Alive $owned)) }
}

It "fixture-cancel" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $results = New-Results "fixture-cancel"
    $pidFile = Join-Path $results "pids.txt"
    $handle = Open-Runner -ArgumentList @(
        "-Project", $project, "-Scope", "hold", "-Scope", "later",
        "-TimeoutSeconds", "60", "-ResultsRoot", $results, "-TestHostPath", $hostPath
    ) -Environment @{
        TRAIN_HARNESS_MODE = "sleep"
        TRAIN_HARNESS_LOG = (Join-Path $results "host.log")
        TRAIN_HARNESS_PID_FILE = $pidFile
    } -WaitMs 180000
    try {
        Assert-True "host started" (Wait-PidFile $pidFile 90)
        $run = Get-ChildItem -LiteralPath $results -Directory | Select-Object -First 1
        Assert-True "run dir" ($null -ne $run)
        [System.IO.File]::WriteAllText((Join-Path $run.FullName "cancel.request"), "cancel")
        $result = Close-Runner $handle
        $handle = $null
        Assert-Exit $result 3
        $summary = Read-Summary $result.Stdout
        $checks = Get-Checks $summary
        $hold = Find-Check $checks "Fixture.HoldTests.Holds_the_runner"
        $later = Find-Check $checks "Fixture.LaterTests.Finishes"
        Assert-Equal "hold cancel" "cancelled" $hold.outcome
        Assert-Equal "cancel reason" "cancel" $hold.reason
        Assert-True "cancel keeps elapsed ms" ([int] $hold.durationMs -gt 0)
        Assert-Equal "later stopped" "not-executed" $later.outcome
        Assert-Equal "later reason" "cancel" $later.reason
        Assert-True "sentinel" (Test-Alive $sentinelId)
        foreach ($owned in (Read-PidFile $pidFile)) { Assert-True "cancel killed $owned" (-not (Test-Alive $owned)) }
        Note "EVIDENCE fixture-cancel $($summary.Directory) $($result.DurationMs)ms"
    }
    finally {
        if ($handle) { Stop-Tree $handle.Process.Id }
    }
}

It "fixture-trx-row-order" {
    $id = "Fixture.VoxelRuleTests.Adds_two_integers"
    foreach ($mode in @("fail-then-pass", "pass-then-fail")) {
        $results = New-Results "trx-$mode"
        $result = Invoke-Runner -ArgumentList @(
            "-Project", $project, "-Test", $id,
            "-ResultsRoot", $results, "-TestHostPath", $hostPath
        ) -Environment @{ TRAIN_HARNESS_MODE = $mode } -WaitMs 180000
        Assert-Exit $result 1
        $check = Find-Check (Get-Checks (Read-Summary $result.Stdout)) $id
        Assert-Equal "$mode outcome" "failed" $check.outcome
        Assert-Equal "$mode reason" "Failed" $check.reason
        Assert-Equal "$mode duration" 150 ([int] $check.durationMs)
    }
}

function Invoke-NestedGit {
    param([string] $Repo, [string[]] $GitArgs)
    $previous = $PSNativeCommandUseErrorActionPreference
    $previousGitDir = $env:GIT_DIR
    $previousWorkTree = $env:GIT_WORK_TREE
    $previousIndex = $env:GIT_INDEX_FILE
    $PSNativeCommandUseErrorActionPreference = $false
    Remove-Item Env:GIT_DIR, Env:GIT_WORK_TREE, Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
    try {
        $output = & git -C $Repo -c commit.gpgsign=false @GitArgs | Out-String
        if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') exit $LASTEXITCODE" }
        return $output.Trim()
    }
    finally {
        $PSNativeCommandUseErrorActionPreference = $previous
        if ($null -eq $previousGitDir) { Remove-Item Env:GIT_DIR -ErrorAction SilentlyContinue } else { $env:GIT_DIR = $previousGitDir }
        if ($null -eq $previousWorkTree) { Remove-Item Env:GIT_WORK_TREE -ErrorAction SilentlyContinue } else { $env:GIT_WORK_TREE = $previousWorkTree }
        if ($null -eq $previousIndex) { Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue } else { $env:GIT_INDEX_FILE = $previousIndex }
    }
}

function New-BindingRepository {
    param([string] $Name, [string] $Source)
    $repo = Join-Path $evidence "git-binding/$Name"
    New-Item -ItemType Directory -Force -Path (Join-Path $repo "scripts") | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $repo ".gitignore"), "bin/`nobj/`nTestResults/`n", $utf8)
    [System.IO.File]::WriteAllText((Join-Path $repo "Binding.csproj"), $sdk, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $repo "BindingTests.cs"), $Source, $utf8)
    [System.IO.File]::Copy($runner, (Join-Path $repo "scripts/run-tests.ps1"), $true)
    New-Item -ItemType Directory -Force -Path (Join-Path $repo "scripts/checks") | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "checks/common-checks.ps1") -Destination (Join-Path $repo "scripts/checks")
    $readerSource = Join-Path $PSScriptRoot "metadata-reader/TestMetadataReader.cs"
    $readerDir = Join-Path $repo "scripts/metadata-reader"
    New-Item -ItemType Directory -Force -Path $readerDir | Out-Null
    [System.IO.File]::Copy($readerSource, (Join-Path $readerDir "TestMetadataReader.cs"), $true)
    $null = Invoke-NestedGit $repo @("init")
    $null = Invoke-NestedGit $repo @("add", "-A")
    $null = Invoke-NestedGit $repo @("commit", "-m", "initial binding fixture")
    $head = Invoke-NestedGit $repo @("rev-parse", "HEAD")
    if ($LASTEXITCODE -ne 0 -or -not $head) { throw "binding repo has no commit" }
    return [pscustomobject]@{ Repo = $repo; Head = $head }
}

$bindingSource = @'
using System;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BindingFixture;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:binding")]
public sealed class BindingTests
{
    [TestMethod]
    public void Runs_built_sources()
    {
        string ready = Environment.GetEnvironmentVariable("BINDING_READY");
        string release = Environment.GetEnvironmentVariable("BINDING_RELEASE");
        if (!string.IsNullOrEmpty(ready))
        {
            File.WriteAllText(ready, "ready");
        }
        if (!string.IsNullOrEmpty(release))
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            while (!File.Exists(release))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Assert.Fail("release was not signaled");
                }
                Thread.Sleep(50);
            }
        }
        Assert.AreEqual("BINDING_VALUE", VersionValue);
    }

    private const string VersionValue = "BINDING_VALUE";
}
'@

function Wait-BindingReady($Handle, [string] $Path, [int] $Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) { return }
        if ($Handle.Process.HasExited) { throw "binding runner exited before ready" }
        Start-Sleep -Milliseconds 100
    }
    throw "binding ready missing after ${Seconds}s"
}

function Assert-BindingIdentity {
    param($Summary, [string] $StartCommit, [bool] $StartDirty, [string] $EndCommit, [bool] $EndDirty, [bool] $Changed, [bool] $Proven)
    Assert-Equal "tested commit" $StartCommit ([string] $Summary.Json.commit)
    Assert-Equal "tested dirty" $StartDirty ([bool] $Summary.Json.dirty)
    Assert-Equal "end commit" $EndCommit ([string] $Summary.Json.gitAtEnd.commit)
    Assert-Equal "end dirty" $EndDirty ([bool] $Summary.Json.gitAtEnd.dirty)
    Assert-Equal "identity changed" $Changed ([bool] $Summary.Json.testedIdentityChanged)
    Assert-Equal "clean proven" $Proven ([bool] $Summary.Json.cleanCommitProven)
    $passed = Find-Check (Get-Checks $Summary) "BindingFixture.BindingTests.Runs_built_sources"
    Assert-Equal "binding passed" "passed" $passed.outcome
}

$script:GitIsolation = @("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE")

function Invoke-BindingHandshake {
    param([string] $Repo, [string] $ResultsName, [scriptblock] $DuringReady)
    $ready = Join-Path $evidence "git-binding/$ResultsName-ready.txt"
    $release = Join-Path $evidence "git-binding/$ResultsName-release.txt"
    $results = New-Results $ResultsName
    $handle = Open-Runner -RunnerPath (Join-Path $Repo "scripts/run-tests.ps1") -WorkingDirectory $Repo -ArgumentList @(
        "-Project", (Join-Path $Repo "Binding.csproj"), "-Correctness", "-ResultsRoot", $results
    ) -Environment @{ BINDING_READY = $ready; BINDING_RELEASE = $release } -Unset $script:GitIsolation -WaitMs 180000
    $closed = $false
    try {
        Wait-BindingReady $handle $ready 90
        & $DuringReady
        [System.IO.File]::WriteAllText($release, "release", $utf8)
        $result = Close-Runner $handle
        $closed = $true
        return $result
    }
    finally {
        if (-not $closed -and $handle) { Stop-Tree $handle.Process.Id }
    }
}

It "fixture-git-binding-stable" {
    $stable = New-BindingRepository "stable" $bindingSource
    $stableResults = New-Results "git-binding-stable"
    $stableRun = Invoke-Runner -RunnerPath (Join-Path $stable.Repo "scripts/run-tests.ps1") -WorkingDirectory $stable.Repo -Unset $script:GitIsolation -ArgumentList @(
        "-Project", (Join-Path $stable.Repo "Binding.csproj"), "-Correctness", "-ResultsRoot", $stableResults
    ) -WaitMs 180000
    Assert-Exit $stableRun 0
    $stableSummary = Read-Summary $stableRun.Stdout
    $stableHead = Invoke-NestedGit $stable.Repo @("rev-parse", "HEAD")
    Assert-BindingIdentity $stableSummary $stable.Head $false $stableHead $false $false $true
    Assert-Contains "stable clean" $stableSummary.Report "工作区干净"
    Assert-True "stable not rewritten" ($stableSummary.Report -notlike "*不是本次已验证的代码*")
    Note "EVIDENCE fixture-git-binding-stable $($stableSummary.Directory)"
}

It "fixture-git-binding-dirty-commit" {
    $dirtyRepo = New-BindingRepository "dirty-commit" $bindingSource
    [System.IO.File]::WriteAllText((Join-Path $dirtyRepo.Repo "Notes.txt"), "uncommitted", $utf8)
    $mutate = {
        $null = Invoke-NestedGit $dirtyRepo.Repo @("add", "--", "Notes.txt")
        $null = Invoke-NestedGit $dirtyRepo.Repo @("commit", "-m", "commit note during run")
    }.GetNewClosure()
    $dirtyRun = Invoke-BindingHandshake $dirtyRepo.Repo "git-binding-dirty-commit" $mutate
    Assert-Exit $dirtyRun 0
    $dirtySummary = Read-Summary $dirtyRun.Stdout
    $dirtyEnd = Invoke-NestedGit $dirtyRepo.Repo @("rev-parse", "HEAD")
    Assert-True "dirty commit moved" ($dirtyEnd -ne $dirtyRepo.Head)
    Assert-BindingIdentity $dirtySummary $dirtyRepo.Head $true $dirtyEnd $false $true $false
    Assert-Contains "dirty boundary" $dirtySummary.Report "不是本次已验证的代码"
    Note "EVIDENCE fixture-git-binding-dirty $($dirtySummary.Directory)"
}

It "fixture-git-binding-commit-during" {
    $during = New-BindingRepository "commit-during" $bindingSource
    $mutate = {
        $sourcePath = Join-Path $during.Repo "BindingTests.cs"
        $sourceText = [System.IO.File]::ReadAllText($sourcePath)
        $rewritten = $sourceText.Replace('private const string VersionValue = "BINDING_VALUE";', 'private const string VersionValue = "BINDING_VALUE_NEXT";')
        if ($rewritten -eq $sourceText) { throw "binding constant was not found" }
        [System.IO.File]::WriteAllText($sourcePath, $rewritten, $utf8)
        $null = Invoke-NestedGit $during.Repo @("add", "--", "BindingTests.cs")
        $null = Invoke-NestedGit $during.Repo @("commit", "-m", "change constant during run")
    }.GetNewClosure()
    $duringRun = Invoke-BindingHandshake $during.Repo "git-binding-commit-during" $mutate
    Assert-Exit $duringRun 0
    $duringSummary = Read-Summary $duringRun.Stdout
    $duringEnd = Invoke-NestedGit $during.Repo @("rev-parse", "HEAD")
    Assert-True "during commit moved" ($duringEnd -ne $during.Head)
    Assert-Contains "source moved" ([System.IO.File]::ReadAllText((Join-Path $during.Repo "BindingTests.cs"))) "BINDING_VALUE_NEXT"
    Assert-BindingIdentity $duringSummary $during.Head $false $duringEnd $false $true $false
    Assert-Contains "during boundary" $duringSummary.Report "不是本次已验证的代码"
    Assert-Contains "during start" $duringSummary.Report $during.Head
    Note "EVIDENCE fixture-git-binding-during $($duringSummary.Directory)"
}

It "fixture-git-binding-paths" {
    $pathsRepo = New-BindingRepository "paths" $bindingSource
    [System.IO.File]::WriteAllText((Join-Path $pathsRepo.Repo "Notes.txt"), "uncommitted", $utf8)
    $mutate = {
        [System.IO.File]::WriteAllText((Join-Path $pathsRepo.Repo "Extra.txt"), "also uncommitted", $utf8)
    }.GetNewClosure()
    $pathsRun = Invoke-BindingHandshake $pathsRepo.Repo "git-binding-paths" $mutate
    Assert-Exit $pathsRun 0
    $pathsSummary = Read-Summary $pathsRun.Stdout
    $pathsEnd = Invoke-NestedGit $pathsRepo.Repo @("rev-parse", "HEAD")
    Assert-Equal "paths commit stays" $pathsRepo.Head $pathsEnd
    Assert-BindingIdentity $pathsSummary $pathsRepo.Head $true $pathsEnd $true $true $false
    $startPaths = (@($pathsSummary.Json.dirtyPaths) | ForEach-Object { [string] $_ }) -join "`n"
    $endPaths = (@($pathsSummary.Json.gitAtEnd.dirtyPaths) | ForEach-Object { [string] $_ }) -join "`n"
    Assert-Contains "start notes" $startPaths "Notes.txt"
    Assert-True "start without extra" ($startPaths -notlike "*Extra.txt*")
    Assert-Contains "end notes" $endPaths "Notes.txt"
    Assert-Contains "end extra" $endPaths "Extra.txt"
    Assert-Contains "paths boundary" $pathsSummary.Report "不是本次已验证的代码"
    Note "EVIDENCE fixture-git-binding-paths $($pathsSummary.Directory)"
}

function Invoke-CopiedBindingRunner {
    param([string] $Repo, [string] $ResultsName)
    $results = New-Results $ResultsName
    return Invoke-Runner -RunnerPath (Join-Path $Repo "scripts/run-tests.ps1") -WorkingDirectory $Repo -Unset $script:GitIsolation -ArgumentList @(
        "-Project", (Join-Path $Repo "Binding.csproj"), "-Correctness", "-ResultsRoot", $results
    ) -WaitMs 180000
}

function Break-NestedIndex {
    param([string] $Repo)
    $index = [System.IO.Path]::GetFullPath((Join-Path $Repo ".git/index"))
    $allowed = [System.IO.Path]::GetFullPath($evidence)
    if (-not $allowed.EndsWith([System.IO.Path]::DirectorySeparatorChar)) { $allowed += [System.IO.Path]::DirectorySeparatorChar }
    if (-not $index.StartsWith($allowed, [System.StringComparison]::OrdinalIgnoreCase)) { throw "refusing to rewrite $index" }
    [System.IO.File]::WriteAllBytes($index, [byte[]](98, 97, 100))
    [System.IO.File]::WriteAllText((Join-Path $Repo "Untracked.txt"), "not in the index", $utf8)
}

It "fixture-git-status-clean" {
    $repo = New-BindingRepository "status-clean" $bindingSource
    $run = Invoke-CopiedBindingRunner $repo.Repo "git-status-clean"
    Assert-Exit $run 0
    $summary = Read-Summary $run.Stdout
    $head = Invoke-NestedGit $repo.Repo @("rev-parse", "HEAD")
    Assert-BindingIdentity $summary $repo.Head $false $head $false $false $true
    Assert-Equal "identity known" $true ([bool] $summary.Json.identityKnown)
    Assert-Equal "end identity known" $true ([bool] $summary.Json.gitAtEnd.identityKnown)
    Assert-Equal "identity reason" "" ([string] $summary.Json.identityReason)
    Assert-Contains "clean sentence" $summary.Report "工作区干净"
    $help = Invoke-Runner -RunnerPath (Join-Path $repo.Repo "scripts/run-tests.ps1") -WorkingDirectory $repo.Repo -Unset $script:GitIsolation -ArgumentList @("-Nope") -WaitMs 30000
    Assert-Exit $help 4
    Assert-Contains "unverified help" $help.Stdout "身份未验证"
    Note "EVIDENCE fixture-git-status-clean $($summary.Directory)"
}

It "fixture-git-status-failure" {
    $repo = New-BindingRepository "status-failure" $bindingSource
    Break-NestedIndex $repo.Repo
    $run = Invoke-CopiedBindingRunner $repo.Repo "git-status-failure"
    Assert-Exit $run 0
    $summary = Read-Summary $run.Stdout
    $passed = Find-Check (Get-Checks $summary) "BindingFixture.BindingTests.Runs_built_sources"
    Assert-Equal "status failure still passed" "passed" $passed.outcome
    Assert-Equal "identity unknown" $false ([bool] $summary.Json.identityKnown)
    Assert-Equal "end identity unknown" $false ([bool] $summary.Json.gitAtEnd.identityKnown)
    Assert-Equal "not a recorded change" $false ([bool] $summary.Json.testedIdentityChanged)
    Assert-Equal "not proven" $false ([bool] $summary.Json.cleanCommitProven)
    Assert-Contains "status reason" ([string] $summary.Json.identityReason) "git status exit 128"
    Assert-Contains "unverified report" $summary.Report "Git 身份未验证"
    Assert-Contains "reason in report" $summary.Report "git status exit 128"
    Assert-True "not reported clean" ($summary.Report -notlike "*工作区干净*")
    Assert-True "not reported dirty" ($summary.Report -notlike "*有未提交修改*")
    Note "EVIDENCE fixture-git-status-failure $($summary.Directory)"
}

It "fixture-git-missing-start" {
    $directory = Join-Path $evidence "missing-start"
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $probe = Join-Path $evidence "missing-start-probe.ps1"
    @'
param([string] $Runner, [string] $Directory)
$ErrorActionPreference = "Stop"
. $Runner
$script:TestedGit = $null
$selection = New-Map
Add-Map $selection "families" (New-List) | Out-Null
Add-Map $selection "scopes" (New-List) | Out-Null
Add-Map $selection "tests" (New-List) | Out-Null
Add-Map $selection "correctness" $false | Out-Null
Add-Map $selection "caseIds" (New-List) | Out-Null
$preparation = New-Map
Add-Map $preparation "durationMs" 0 | Out-Null
Add-Map $preparation "commandCount" 0 | Out-Null
Add-Map $preparation "outcome" "passed" | Out-Null
Add-Map $preparation "reason" "none" | Out-Null
Publish-Run -Preparation $preparation -Selection $selection -ExitCode 0 -Directory $Directory
'@ | Set-Content -LiteralPath $probe -Encoding utf8NoBOM
    & $pwsh -NoProfile -File $probe -Runner $runner -Directory $directory
    if ($LASTEXITCODE -ne 0) { throw "missing-start probe exit $LASTEXITCODE" }
    $summaryPath = Join-Path $directory "summary.json"
    $report = [System.IO.File]::ReadAllText((Join-Path $directory "report.md"))
    $json = Get-Content -Raw -LiteralPath $summaryPath | ConvertFrom-Json
    $head = (& git -C $root rev-parse HEAD | Out-String).Trim()
    Assert-Equal "start not the end commit" "unknown" ([string] $json.commit)
    Assert-Equal "end is live" $head ([string] $json.gitAtEnd.commit)
    Assert-Equal "start unknown" $false ([bool] $json.identityKnown)
    Assert-Equal "end known" $true ([bool] $json.gitAtEnd.identityKnown)
    Assert-Equal "missing reason" "构建前没有记录 Git 身份" ([string] $json.identityReason)
    Assert-Equal "not proven" $false ([bool] $json.cleanCommitProven)
    Assert-Contains "unverified report" $report "Git 身份未验证"
    Assert-Contains "missing reason report" $report "构建前没有记录 Git 身份"
    Note "EVIDENCE fixture-git-missing-start $directory"
}

It "fixture-ctrl-c" {
    if (-not $IsWindows) { throw "blocked: Windows console Ctrl+C is unverified on this host" }
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $probeDir = Join-Path $evidence "ctrl-c-probe"
    New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
    $enablePath = Join-Path $probeDir "enable-control-c.ps1"
    $enable = @'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ConsoleControlProbe {
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
"@
$enabled = [ConsoleControlProbe]::SetConsoleCtrlHandler([IntPtr]::Zero, $false)
Set-Content -LiteralPath "__MARKER__" -Value "ENABLE_CTRL_C=$enabled" -Encoding ascii
& '__RUNNER__' @args
$code = $LASTEXITCODE
Add-Content -LiteralPath "__MARKER__" -Value "EXIT=$code" -Encoding ascii
exit $code
'@
    $runnerLiteral = $runner.Replace("'", "''")
    Set-Content -LiteralPath $enablePath -Value ($enable.Replace("__RUNNER__", $runnerLiteral).Replace("__MARKER__", ($probeDir.Replace("'", "''") + "\enable.log"))) -Encoding utf8
    $injectPath = Join-Path $probeDir "inject-ctrl-c.ps1"
    @'
param([int] $TargetPid, [string] $ResultPath)
$ErrorActionPreference = "Stop"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ConsoleCtrlInjector {
 [DllImport("kernel32.dll", SetLastError = true)] public static extern bool FreeConsole();
 [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AttachConsole(uint processId);
 [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
 [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
"@
function Write-InjectResult([int] $Code, [string] $Message) {
    Set-Content -LiteralPath $ResultPath -Value "code=$Code message=$Message" -Encoding ascii
    exit $Code
}
[ConsoleCtrlInjector]::FreeConsole() | Out-Null
if (-not [ConsoleCtrlInjector]::AttachConsole([uint32] $TargetPid)) {
    Write-InjectResult 2 ("AttachConsole " + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error())
}
if (-not [ConsoleCtrlInjector]::SetConsoleCtrlHandler([IntPtr]::Zero, $true)) {
    Write-InjectResult 1 ("SetConsoleCtrlHandler " + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error())
}
if (-not [ConsoleCtrlInjector]::GenerateConsoleCtrlEvent(0, 0)) {
    Write-InjectResult 1 ("GenerateConsoleCtrlEvent " + [System.Runtime.InteropServices.Marshal]::GetLastWin32Error())
}
# 保持连接，直到目标控制台收到事件。
Start-Sleep -Milliseconds 500
[ConsoleCtrlInjector]::FreeConsole() | Out-Null
Write-InjectResult 0 "ok"
'@ | Set-Content -LiteralPath $injectPath -Encoding utf8
    if (-not ("HiddenConsoleProcess" -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HiddenConsoleProcess {
    const uint CREATE_NEW_CONSOLE = 0x00000010;
    const int STARTF_USESHOWWINDOW = 0x00000001;
    const short SW_HIDE = 0;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref STARTUPINFO startup, out PROCESS_INFORMATION process);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    static IntPtr startedProcess;
    public static int QueryExit(int processId) {
        IntPtr handle = startedProcess;
        if (handle == IntPtr.Zero) {
            handle = OpenProcess(0x1000, false, processId);
            if (handle == IntPtr.Zero) {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        try {
            uint code;
            if (!GetExitCodeProcess(handle, out code)) {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            return unchecked((int)code);
        }
        finally {
            if (startedProcess == IntPtr.Zero) { CloseHandle(handle); }
        }
    }
    public static int Start(string application, string commandLine, string directory) {
        if (startedProcess != IntPtr.Zero) {
            CloseHandle(startedProcess);
            startedProcess = IntPtr.Zero;
        }
        STARTUPINFO startup = new STARTUPINFO();
        startup.cb = Marshal.SizeOf<STARTUPINFO>();
        startup.dwFlags = STARTF_USESHOWWINDOW;
        startup.wShowWindow = SW_HIDE;
        PROCESS_INFORMATION process;
        if (!CreateProcessW(application, commandLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_NEW_CONSOLE, IntPtr.Zero, directory, ref startup, out process)) {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        CloseHandle(process.hThread);
        startedProcess = process.hProcess;
        return process.dwProcessId;
    }
}
"@
    }
    function Format-ProcessArgument([string] $Value) {
        if ($Value -match '\s') { return '"' + $Value + '"' }
        return $Value
    }
    function Start-CtrlTarget {
        param([string] $Method, [string] $ResultsRoot, [string] $PidFile)
        $arguments = @(
            "-NoProfile", "-File", $enablePath,
            "-Project", $project, "-Scope", "hold", "-Scope", "later",
            "-TestHostPath", $hostPath, "-TimeoutSeconds", "60", "-ResultsRoot", $ResultsRoot
        )
        $previousMode = $env:TRAIN_HARNESS_MODE
        $previousPidFile = $env:TRAIN_HARNESS_PID_FILE
        $env:TRAIN_HARNESS_MODE = "sleep"
        $env:TRAIN_HARNESS_PID_FILE = $PidFile
        try {
            if ($Method -eq "shell") {
                return Start-Process -FilePath $pwsh -WorkingDirectory $root -WindowStyle Hidden -PassThru -ArgumentList $arguments
            }
            $command = @(Format-ProcessArgument $pwsh) + @($arguments | ForEach-Object { Format-ProcessArgument $_ })
            $created = [HiddenConsoleProcess]::Start($pwsh, ($command -join " "), $root)
            return Get-Process -Id $created
        }
        finally {
            if ($null -eq $previousMode) { Remove-Item Env:TRAIN_HARNESS_MODE -ErrorAction SilentlyContinue }
            else { $env:TRAIN_HARNESS_MODE = $previousMode }
            if ($null -eq $previousPidFile) { Remove-Item Env:TRAIN_HARNESS_PID_FILE -ErrorAction SilentlyContinue }
            else { $env:TRAIN_HARNESS_PID_FILE = $previousPidFile }
        }
    }
    $launched = $false
    $usedMethod = ""
    foreach ($method in @("shell", "new-console")) {
        $outDir = Join-Path $probeDir $method
        $results = Join-Path $outDir "runs"
        $pidFile = Join-Path $outDir "pids.txt"
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        $target = $null
        $targetId = 0
        try {
            $target = Start-CtrlTarget -Method $(if ($method -eq "shell") { "shell" } else { "new-console" }) -ResultsRoot $results -PidFile $pidFile
            $targetId = $target.Id
            if (-not (Wait-PidFile $pidFile 90)) { throw "host readiness missing for $method" }
            $injectResult = Join-Path $outDir "inject.txt"
            $inject = & $pwsh -NoProfile -File $injectPath -TargetPid $targetId -ResultPath $injectResult
            if ($LASTEXITCODE -eq 2) { throw "attach failed for $method $inject" }
            if ($LASTEXITCODE -ne 0) { throw "inject failed for $method $inject" }
            if ($target.HasExited) { throw "ctrl-c target $targetId exited before it could be observed" }
            if (-not $target.WaitForExit(20000)) { throw "ctrl-c target still running after 20s" }
            $nativeExit = if ($method -eq "shell") { [int] $target.ExitCode } else { [HiddenConsoleProcess]::QueryExit($targetId) }
            Assert-Equal "ctrl-c exit" 3 $nativeExit
            $enableLog = [System.IO.File]::ReadAllText((Join-Path $probeDir "enable.log"))
            Assert-Contains "wrapper exit" $enableLog "EXIT=3"
            $summaryFile = Get-ChildItem -LiteralPath $results -Recurse -Filter summary.json | Select-Object -First 1
            Assert-True "ctrl-c summary" ($null -ne $summaryFile)
            $summary = Get-Content -Raw -LiteralPath $summaryFile.FullName | ConvertFrom-Json
            Assert-Equal "ctrl-c summary exit" 3 ([int] $summary.exitCode)
            $hold = $null
            foreach ($check in @($summary.checks)) { if ([string] $check.id -eq "Fixture.HoldTests.Holds_the_runner") { $hold = $check } }
            Assert-Equal "ctrl-c hold" "cancelled" ([string] $hold.outcome)
            $later = $null
            foreach ($check in @($summary.checks)) { if ([string] $check.id -eq "Fixture.LaterTests.Finishes") { $later = $check } }
            Assert-Equal "ctrl-c later" "not-executed" ([string] $later.outcome)
            foreach ($owned in (Read-PidFile $pidFile)) { Assert-True "ctrl-c killed $owned" (-not (Test-Alive $owned)) }
            $launched = $true
            $usedMethod = $method
            Note "EVIDENCE fixture-ctrl-c $outDir method=$method"
            break
        }
        catch {
            $failure = $_.Exception.Message
            if ($targetId -gt 0) { Stop-Tree $targetId }
            foreach ($owned in (Read-PidFile $pidFile)) { Stop-Tree $owned }
            $retryable = $failure -like "attach failed*" -or $failure -like "host readiness missing*"
            if (-not $retryable -or $method -eq "new-console") { throw }
            Note "ctrl-c $method did not attach: $failure"
        }
    }
    Assert-True "ctrl-c launched" $launched
    Assert-True "ctrl-c sentinel" (Test-Alive $sentinelId)
    Note "EVIDENCE fixture-ctrl-c method=$usedMethod"
}

It "fixture-datatestmethod" {
    $id = "Fixture.DataMethodTests.DataTestMethod_is_listed"
    $listed = Invoke-Runner -ArgumentList @("-Project", $project, "-List", "-Scope", "rows") -WaitMs 180000
    Assert-Exit $listed 0
    Assert-Contains "unified lists data method" $listed.Stdout "CASE id=$id "
    $native = & dotnet test $project --no-build --nologo --list-tests
    if ($LASTEXITCODE -ne 0) { throw "native list exit $LASTEXITCODE" }
    Assert-Contains "native lists data method" ($native -join "`n") "DataTestMethod_is_listed"
    $ran = Invoke-Runner -ArgumentList @("-Project", $project, "-Test", $id, "-ResultsRoot", (New-Results "datatestmethod")) -WaitMs 180000
    Assert-Exit $ran 1
    $check = Find-Check (Get-Checks (Read-Summary $ran.Stdout)) $id
    Assert-Equal "data method failed" "failed" $check.outcome
}

It "fixture-cleanup" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $results = New-Results "fixture-cleanup"
    $pidFile = Join-Path $results "pids.txt"
    $started = [DateTime]::UtcNow
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-Scope", "hold", "-Scope", "later",
        "-TimeoutSeconds", "12", "-ResultsRoot", $results, "-TestHostPath", $hostPath
    ) -Environment @{
        TRAIN_HARNESS_MODE = "sleep"
        TRAIN_HARNESS_LOG = (Join-Path $results "host.log")
        TRAIN_HARNESS_PID_FILE = $pidFile
        TRAIN_RUN_TESTS_FAULT = "cleanup-kill"
    } -WaitMs 120000
    try {
        $elapsed = ([DateTime]::UtcNow - $started).TotalSeconds
        Assert-True "cleanup bounded $elapsed" ($elapsed -lt 90)
        Assert-Exit $result 2
        $checks = Get-Checks (Read-Summary $result.Stdout)
        $hold = Find-Check $checks "Fixture.HoldTests.Holds_the_runner"
        $later = Find-Check $checks "Fixture.LaterTests.Finishes"
        Assert-Equal "cleanup blocked" "blocked" $hold.outcome
        Assert-Equal "cleanup reason" "cleanup" $hold.reason
        Assert-True "cleanup keeps elapsed ms" ([int] $hold.durationMs -gt 0)
        Assert-Equal "later not run" "not-executed" $later.outcome
        Assert-Equal "later cleanup" "cleanup" $later.reason
        Assert-True "sentinel" (Test-Alive $sentinelId)
        $pids = Read-PidFile $pidFile
        Assert-True "pid file" ($pids.Count -ge 2)
        foreach ($owned in $pids) {
            Assert-True "still alive $owned" (Test-Alive $owned)
            Assert-True "not sentinel" ($owned -ne $sentinelId)
        }
        Note "EVIDENCE fixture-cleanup $($result.DurationMs)ms"
    }
    finally {
        foreach ($owned in (Read-PidFile $pidFile)) { Stop-Tree $owned }
    }
}

It "real-help" {
    $result = Invoke-Runner -WaitMs 600000
    Assert-Exit $result 0
    foreach ($token in @(
        "GROUP family=unit", "GROUP family=headless", "GROUP scope=voxels",
        "test-assembly:", "not all repository validation", "ci: Windows CI", "pending: benchmark", "PENDING benchmark",
        "REGISTERED", "godot.logging-smoke", "godot.content-smoke", "godot.main-menu", "godot.simulation-host-smoke",
        "godot.content-export-smoke", "godot.mods-export-smoke",
        "-RunnerCatalog", "-PrepTimeoutSeconds", "-BuildGame", "See docs/ci.md.",
        "构建开始前", "不能证明结束时的干净提交已通过"
    )) {
        Assert-Contains $token $result.Stdout $token
    }
    Assert-Contains "partial contract" $result.Stdout "partial"
    Assert-Contains "measured contract" $result.Stdout "measured-this-run"
    Assert-True "no full pass claim" ($result.Stdout -notlike "*all repository checks passed*")
    Assert-True "group output has no CI selector" ($result.Stdout -notmatch "(?m)^CI ")
    Assert-True "help has no workflow selection" ($result.Stdout -notlike "*routine selection*")
    Note "EVIDENCE real-help $($result.DurationMs)ms"
}

It "real-list" {
    $result = Invoke-Runner -ArgumentList @("-List") -WaitMs 600000
    Assert-Exit $result 0
    Assert-Contains "coverage" $result.Stdout "not all repository validation"
    $cases = Get-CaseLines $result.Stdout
    Assert-True "discovered" ($cases.Count -gt 0)
    $mstest = [System.Collections.Generic.List[object]]::new()
    $external = [System.Collections.Generic.List[object]]::new()
    foreach ($case in $cases) {
        if ($case.Id.StartsWith("Train.ContractSupport.Tests.")) { continue }
        if ($case.Id.StartsWith("Train.Core.Tests.") -or $case.Id.StartsWith("Train.Content.Tests.") -or $case.Id.StartsWith("Train.Godot.Tests.")) { $mstest.Add($case) }
        else { $external.Add($case) }
    }
    $seen = @{}
    foreach ($case in $mstest) {
        $parts = $case.Id.Split(".")
        Assert-True "id shape $($case.Id)" ($parts.Length -ge 3)
        $className = $parts[$parts.Length - 2]
        $fullTypeName = ($parts[0..($parts.Length - 2)] -join ".")
        $key = if ($expected.ContainsKey($fullTypeName)) { $fullTypeName } else { $className }
        Assert-True "known class $fullTypeName" $expected.ContainsKey($key)
        $classification = $expected[$key].Split("/")
        Assert-Equal "$key family" $classification[0] $case.Family
        $expectedScopes = ($classification[1].Split(",") | Sort-Object) -join ","
        $actualScopes = ($case.Scopes.Split(",") | Sort-Object) -join ","
        Assert-Equal "$key scopes" $expectedScopes $actualScopes
        Assert-Equal "$key group" $classification[2] $case.Group
        $expectedRequirements = if ($classification.Count -ge 4) { $classification[3] } else { "none" }
        $actualRequirements = ($case.Requirements.Split(",") | Sort-Object) -join ","
        $sortedExpectedRequirements = ($expectedRequirements.Split(",") | Sort-Object) -join ","
        Assert-Equal "$key requirements" $sortedExpectedRequirements $actualRequirements
        $seen[$key] = $true
        Assert-True "family $($case.Family)" ($case.Family -eq "unit" -or $case.Family -eq "headless")
    }
    $externalById = @{}
    foreach ($case in $external) { $externalById[$case.Id] = $case }
    Assert-Equal "external count" 8 $external.Count
    Assert-Equal "structure module" "all" $externalById["contracts.structure"].Module
    Assert-Equal "schema module" "content" $externalById["content.examples-schema"].Module
    $logging = $externalById["godot.logging-smoke"]
    $content = $externalById["godot.content-smoke"]
    $menu = $externalById["godot.main-menu"]
    $simulation = $externalById["godot.simulation-host-smoke"]
    $contentExport = $externalById["godot.content-export-smoke"]
    $modsExport = $externalById["godot.mods-export-smoke"]
    Assert-True "logging listed" ($null -ne $logging)
    Assert-True "content listed" ($null -ne $content)
    Assert-True "menu listed" ($null -ne $menu)
    Assert-True "simulation listed" ($null -ne $simulation)
    Assert-True "content export listed" ($null -ne $contentExport)
    Assert-True "mods export listed" ($null -ne $modsExport)
    Assert-Equal "logging is headless" "headless" $logging.Family
    Assert-True "logging scope" (-not [string]::IsNullOrWhiteSpace([string] $logging.Scopes))
    Assert-Contains "logging needs godot" $logging.Requirements "godot"
    Assert-True "logging is one process" ([string] $logging.Requirements -notlike "*process*")
    Assert-True "logging group" (-not [string]::IsNullOrWhiteSpace([string] $logging.Group))
    Assert-Contains "logging observes the engine" $logging.Claim "engine-observation"
    Assert-Contains "content needs godot" $content.Requirements "godot"
    Assert-Contains "menu needs godot" $menu.Requirements "godot"
    Assert-Equal "simulation is headless" "headless" $simulation.Family
    Assert-Equal "simulation scope" "simulation" $simulation.Scopes
    Assert-Equal "simulation group" "godot-simulation" $simulation.Group
    Assert-Contains "simulation needs godot" $simulation.Requirements "godot"
    Assert-Equal "simulation observes the engine" "engine-observation" $simulation.Claim
    Assert-Contains "content export requirement" $contentExport.Requirements "export"
    Assert-Contains "mods export requirement" $modsExport.Requirements "export"
    foreach ($class in $expected.Keys) { Assert-True "missing class $class" $seen.ContainsKey($class) }
    $selected = [regex]::Match($result.Stdout, "SELECTED count=(\d+)")
    Assert-Equal "selected count" $cases.Count ([int] $selected.Groups[1].Value)
    $listText = ''
    $listError = ''
    # 分别查询每个项目，避免解决方案级 list-tests 并发汇总输出不可靠。
    foreach ($project in @('Tests/Core/Train.Core.Tests.csproj', 'Tests/Content/Train.Content.Tests.csproj', 'Tests/Godot/Train.Godot.Tests.csproj', 'Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj')) {
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = "dotnet"
        $psi.WorkingDirectory = $root
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = $utf8
        $psi.StandardErrorEncoding = $utf8
        foreach ($argument in @("test", $project, "-c", "Debug", "--no-build", "--no-restore", "--nologo", "--list-tests")) {
            [void] $psi.ArgumentList.Add($argument)
        }
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $psi
        [void] $process.Start()
        $listed = $process.StandardOutput.ReadToEndAsync()
        $listErr = $process.StandardError.ReadToEndAsync()
        Assert-True "list-tests finished $project" $process.WaitForExit(180000)
        $listText += [string] $listed.Result + "`n"
        $listError += [string] $listErr.Result + "`n"
        Assert-Equal "list-tests exit $project" 0 $process.ExitCode
    }
    $nativeIds = [System.Collections.Generic.List[string]]::new()
    $nativeNames = [System.Collections.Generic.List[string]]::new()
    $dataMethods = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in ($listText -split "\r?\n")) {
        $trimmed = $line.Trim()
        if ($trimmed -match "^Train\.(Core|Content|Godot|ContractSupport)\.Tests\.\S+$") { $nativeIds.Add($trimmed) }
        elseif ($trimmed -match "^[A-Za-z_][A-Za-z0-9_]+$") { $nativeNames.Add($trimmed) }
        elseif ($trimmed -match "^([A-Za-z_][A-Za-z0-9_]+) \(.+\)$") {
            # MSTest lists each DataRow; the entry discovers the declaring method once.
            if ($dataMethods.Add($Matches[1])) { $nativeNames.Add($Matches[1]) }
        }
    }
    if ($nativeIds.Count -gt 0) {
        $ours = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($case in $cases) {
            if ($case.Id.StartsWith("Train.")) { [void] $ours.Add($case.Id) }
        }
        Assert-Equal "list size" $nativeIds.Count $ours.Count
        foreach ($id in $nativeIds) { Assert-True "missing from runner $id" $ours.Contains($id) }
    }
    elseif ($nativeNames.Count -gt 0) {
        $ours = [System.Collections.Generic.List[string]]::new()
        foreach ($case in $cases) {
            if (-not $case.Id.StartsWith("Train.")) { continue }
            $dot = $case.Id.LastIndexOf(".")
            $ours.Add($case.Id.Substring($dot + 1))
        }
        $left = @($nativeNames | Sort-Object)
        $right = @($ours | Sort-Object)
        Assert-Equal "display-name count" $left.Count $right.Count
        for ($i = 0; $i -lt $left.Count; $i++) {
            Assert-Equal "display name $i" $left[$i] $right[$i]
        }
    }
    else { throw "dotnet --list-tests parsed nothing: $listText $listError" }
    Note "EVIDENCE real-list count=$($cases.Count) $($result.DurationMs)ms"
}

It "real-voxels" {
    $results = New-Results "real-voxels"
    $result = Invoke-Runner -ArgumentList @("-Module", "content", "-Family", "unit", "-Scope", "voxels", "-ResultsRoot", $results) -WaitMs 600000
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    Assert-CommonSummary $summary -PrepCount 2
    $builds = @($summary.Json.preparation.steps | Where-Object name -EQ 'build')
    foreach ($preparedProject in @('Tests/Content/Train.Content.Tests.csproj', 'Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj')) {
        Assert-Equal "prepared $preparedProject" 1 @($builds | Where-Object { $_.command -contains (Join-Path $root $preparedProject) }).Count
    }
    $checks = Get-Checks $summary
    Assert-True "voxel cases" ($checks.Count -ge 1)
    $classes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($check in $checks) {
        Assert-Equal "content module $($check.id)" "content" ([string] $check.module)
        Assert-Equal "unit family $($check.id)" "unit" ([string] $check.family)
        Assert-True "voxel scope $($check.id)" (@($check.scopes) -contains "voxels")
        $id = [string] $check.id
        $parts = $id.Split(".")
        Assert-True "qualified id $id" ($parts.Count -ge 3)
        [void] $classes.Add($parts[$parts.Count - 2])
        Assert-Equal "passed $($check.id)" "passed" $check.outcome
        Assert-True "duration $($check.id)" ([int]$check.durationMs -ge 0)
    }
    Assert-True "cube UV contract selected" $classes.Contains("VoxelCubeUvTests")
    Assert-True "collision definition contract selected" $classes.Contains("VoxelCollisionDefinitionTests")
    $trx = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter *.trx
    Assert-True "voxel trx" ($trx.Count -gt 0)
    $trxText = (($trx | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n")
    Assert-Contains "cube UV results" $trxText "VoxelCubeUvTests"
    Assert-Contains "collision definition results" $trxText "VoxelCollisionDefinitionTests"
    Note "EVIDENCE real-voxels count=$($checks.Count) $($summary.Directory) $($result.DurationMs)ms"
}

It "real-headless" {
    $results = New-Results "real-headless"
    $id = "Train.Godot.Tests.Console.ConsoleCommandDispatchTests.Blank_input_is_ignored"
    $result = Invoke-Runner -ArgumentList @("-Test", $id, "-ResultsRoot", $results) -WaitMs 600000
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    $checks = Get-Checks $summary
    Assert-Equal "one headless" 1 $checks.Count
    Assert-Equal "id" $id $checks[0].id
    Assert-Equal "passed" "passed" $checks[0].outcome
    Assert-Equal "family" "headless" $checks[0].family
    $git = Get-GitFacts
    Assert-Equal "commit" $git.Commit ([string] $summary.Json.commit)
    Assert-Equal "dirty" $git.Dirty ([bool] $summary.Json.dirty)
    Assert-Equal "end commit" $git.Commit ([string] $summary.Json.gitAtEnd.commit)
    Assert-Equal "end dirty" $git.Dirty ([bool] $summary.Json.gitAtEnd.dirty)
    Assert-Equal "identity stable" $false ([bool] $summary.Json.testedIdentityChanged)
    Assert-Equal "identity known" $true ([bool] $summary.Json.identityKnown)
    Assert-Equal "end identity known" $true ([bool] $summary.Json.gitAtEnd.identityKnown)
    Assert-Equal "clean proven" (-not $git.Dirty) ([bool] $summary.Json.cleanCommitProven)
    Note "EVIDENCE real-headless $($summary.Directory) $($result.DurationMs)ms"
}

It "real-zero-unknown" {
    $zero = Invoke-Runner -ArgumentList @("-Family", "unit", "-Scope", "saves") -WaitMs 600000
    Assert-Exit $zero 4
    Assert-Contains "zero" $zero.Stderr "error: zero-match"
    $unknown = Invoke-Runner -ArgumentList @("-Scope", "railway") -WaitMs 600000
    Assert-Exit $unknown 4
    Assert-Contains "railway" $unknown.Stderr "error: unknown-name scope railway"
}

It "real-correctness-list" {
    $all = Invoke-Runner -ArgumentList @("-List") -WaitMs 600000
    $correctness = Invoke-Runner -ArgumentList @("-Correctness", "-List") -WaitMs 600000
    Assert-Exit $all 0
    Assert-Exit $correctness 0
    $allCount = [regex]::Match($all.Stdout, "SELECTED count=(\d+)").Groups[1].Value
    $correctCount = [regex]::Match($correctness.Stdout, "SELECTED count=(\d+)").Groups[1].Value
    Assert-Equal "correctness excludes two export checks" ([int]$allCount - 2) ([int]$correctCount)
    Assert-Contains "boundary" $correctness.Stdout "not all repository validation"
    foreach ($id in @("godot.logging-smoke", "godot.content-smoke", "godot.main-menu", "godot.simulation-host-smoke")) {
        Assert-Contains "all $id" $all.Stdout "CASE id=$id"
        Assert-Contains "correctness $id" $correctness.Stdout "CASE id=$id"
    }
    foreach ($id in @("godot.content-export-smoke", "godot.mods-export-smoke")) {
        Assert-Contains "all $id" $all.Stdout "CASE id=$id"
        Assert-True "correctness excludes $id" ($correctness.Stdout -notlike "*CASE id=$id *")
    }
    Note "EVIDENCE real-correctness-list count=$correctCount $($correctness.DurationMs)ms"
}

It "fixture-no-repo-catalog" {
    $hidden = Invoke-Runner -ArgumentList @("-Project", $project, "-RunnerCatalog", "none", "-List", "-Family", "headless") -WaitMs 300000
    Assert-Exit $hidden 0
    Assert-True "none stays local" ($hidden.Stdout -notlike "*godot.logging-smoke*")
    $shown = Invoke-Runner -ArgumentList @("-Project", $project, "-List", "-Family", "headless") -WaitMs 300000
    Assert-Exit $shown 0
    Assert-Contains "default catalog is not tied to the project path" $shown.Stdout "godot.logging-smoke"
}

It "fixture-external-env-missing" {
    $results = New-Results "fixture-external-env"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.need-godot" -Group "a-godot" -ScriptPath $externalHost -Arguments @("-Mode", "marker", "-UserData", "{output}/user-data") -Requirements @("godot"))
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.need-godot", "-Test", "Fixture.PersistPassTests.Restores_marker",
        "-ResultsRoot", $results
    ) -Unset @("GODOT") -WaitMs 300000
    Assert-Exit $result 2
    $summary = Read-Summary $result.Stdout
    Assert-CommonSummary $summary
    $checks = Get-Checks $summary
    $external = Find-Check $checks "ext.need-godot"
    $later = Find-Check $checks "Fixture.PersistPassTests.Restores_marker"
    Assert-Equal "env blocked" "blocked" $external.outcome
    Assert-Contains "env reason" $external.reason "environment:godot"
    Assert-Equal "sibling passed" "passed" $later.outcome
    $started = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter started.txt -ErrorAction SilentlyContinue
    Assert-Equal "script not started" 0 @($started).Count
}

It "fixture-external-build-timeout" {
    $results = New-Results "fixture-external-build-timeout"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.build" -Group "a-build" -ScriptPath $externalHost -Arguments @("-Mode", "marker", "-UserData", "{output}/user-data") -NeedsGameBuild $true)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog, "-DotnetPath", $fakeExe,
        "-PrepTimeoutSeconds", "20",
        "-Test", "ext.build", "-Test", "Fixture.PersistPassTests.Restores_marker",
        "-ResultsRoot", $results
    ) -Environment @{ FAKE_GAME_BUILD = "sleep" } -WaitMs 180000
    Assert-Exit $result 3
    $summary = Read-Summary $result.Stdout
    Assert-Equal "prep cancelled" "cancelled" $summary.Json.preparation.outcome
    Assert-Equal "prep timeout" "timeout" $summary.Json.preparation.reason
    $checks = Get-Checks $summary
    $external = Find-Check $checks "ext.build"
    $later = Find-Check $checks "Fixture.PersistPassTests.Restores_marker"
    Assert-Equal "build blocked" "blocked" $external.outcome
    Assert-Equal "build timeout" "timeout" $external.reason
    Assert-Equal "independent mstest passed" "passed" $later.outcome
    $started = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter started.txt -ErrorAction SilentlyContinue
    Assert-Equal "script not started" 0 @($started).Count
    Assert-True "game build log kept" (Test-Path -LiteralPath (Join-Path $summary.Directory "game-build.log"))
    $only = Invoke-Runner -ArgumentList @(
        '-Project', $project, '-RunnerCatalog', $catalog, '-DotnetPath', $fakeExe,
        '-Test', 'ext.build', '-ResultsRoot', (New-Results 'fixture-external-build-only')
    ) -Environment @{ FAKE_GAME_BUILD = 'fail' } -WaitMs 300000
    $null = Assert-FinalResult $only 1
}

It "fixture-external-import-blocked" {
    $results = New-Results "fixture-external-import"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.import" -Group "a-import" -ScriptPath $externalHost -Arguments @("-Mode", "marker", "-UserData", "{output}/user-data") -Requirements @("godot") -NeedsImport $true)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.import", "-Test", "Fixture.PersistPassTests.Restores_marker",
        "-ResultsRoot", $results
    ) -Environment @{ GODOT = $fakeExe; FAKE_ROLE = "godot"; FAKE_GODOT = "fail" } -WaitMs 300000
    Assert-Exit $result 2
    $summary = Read-Summary $result.Stdout
    $checks = Get-Checks $summary
    $external = Find-Check $checks "ext.import"
    $later = Find-Check $checks "Fixture.PersistPassTests.Restores_marker"
    Assert-Equal "import blocked" "blocked" $external.outcome
    Assert-Equal "import reason" "import" $external.reason
    Assert-Equal "sibling passed" "passed" $later.outcome
    $started = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter started.txt -ErrorAction SilentlyContinue
    Assert-Equal "script not started" 0 @($started).Count
    Assert-True "import log kept" (Test-Path -LiteralPath (Join-Path $summary.Directory "import.log"))
    $importStep = @($summary.Json.preparation.steps | Where-Object name -eq "import")[0]
    Assert-True "import recovery mode" (@($importStep.command) -contains "--recovery-mode")
    Assert-True "import explicit log" (@($importStep.command) -contains "--log-file")
    Assert-True "import log path" (@($importStep.command) -contains (Join-Path $summary.Directory "engine/import/godot.log"))
}

It "fixture-external-import-timeout" {
    $results = New-Results "fixture-external-import-timeout"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.import" -Group "a-import" -ScriptPath $externalHost -Arguments @("-Mode", "marker", "-UserData", "{output}/user-data") -Requirements @("godot") -NeedsImport $true)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog, "-PrepTimeoutSeconds", "20",
        "-Test", "ext.import", "-Test", "Fixture.PersistPassTests.Restores_marker",
        "-ResultsRoot", $results
    ) -Environment @{ GODOT = $fakeExe; FAKE_ROLE = "godot"; FAKE_GODOT = "sleep" } -WaitMs 180000
    Assert-Exit $result 3
    $summary = Read-Summary $result.Stdout
    Assert-Equal "prep cancelled" "cancelled" $summary.Json.preparation.outcome
    Assert-Equal "prep timeout" "timeout" $summary.Json.preparation.reason
    $checks = Get-Checks $summary
    $external = Find-Check $checks "ext.import"
    $later = Find-Check $checks "Fixture.PersistPassTests.Restores_marker"
    Assert-Equal "import blocked" "blocked" $external.outcome
    Assert-Equal "import timeout" "timeout" $external.reason
    Assert-Equal "independent mstest passed" "passed" $later.outcome
    Assert-Equal "stale assembly omitted" "" ([string] $summary.Json.environment.gameAssembly)
    Assert-Equal "assembly reason" "not-requested" ([string] $summary.Json.environment.gameAssemblyReason)
    Assert-Equal "import started godot" "per-check-appdata" ([string] $summary.Json.environment.engineIsolation)
    $started = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter started.txt -ErrorAction SilentlyContinue
    Assert-Equal "script not started" 0 @($started).Count
    Assert-True "import log kept" (Test-Path -LiteralPath (Join-Path $summary.Directory "import.log"))
}

It "fixture-prep-timeout-cleanup" {
    $results = New-Results "fixture-prep-timeout-cleanup"
    $pidFile = Join-Path $results "pids.txt"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.import" -Group "a-import" -ScriptPath $externalHost -Arguments @("-Mode", "marker", "-UserData", "{output}/user-data") -Requirements @("godot") -NeedsImport $true),
        (New-CheckSpec -Id "ext.hold" -Group "z-hold" -ScriptPath $externalHost -Arguments @("-Mode", "hold", "-PidFile", $pidFile) -TimeoutSeconds 30)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog, "-PrepTimeoutSeconds", "20", "-TestHostPath", $hostPath,
        "-Test", "ext.import", "-Test", "ext.hold",
        "-TimeoutSeconds", "8", "-ResultsRoot", $results
    ) -Environment @{
        GODOT = $fakeExe
        FAKE_ROLE = "godot"
        FAKE_GODOT = "sleep"
        TRAIN_RUN_TESTS_FAULT = "cleanup-kill"
        TRAIN_RUN_TESTS_FAULT_COMMAND = "-Mode hold"
    } -WaitMs 180000
    foreach ($owned in (Read-PidFile $pidFile)) { $script:owned.Add($owned) }
    Assert-Exit $result 2
    $summary = Read-Summary $result.Stdout
    $importStep = $null
    foreach ($step in @($summary.Json.preparation.steps)) {
        if ([string] $step.name -eq "import") { $importStep = $step }
    }
    Assert-True "import step" ($null -ne $importStep)
    Assert-Equal "import timed out" "cancelled" $importStep.outcome
    Assert-Equal "import timeout reason" "timeout" $importStep.reason
    $checks = Get-Checks $summary
    Assert-Equal "dependent blocked" "blocked" (Find-Check $checks "ext.import").outcome
    Assert-Equal "dependent timeout" "timeout" (Find-Check $checks "ext.import").reason
    Assert-Equal "later cleanup" "blocked" (Find-Check $checks "ext.hold").outcome
    Assert-Equal "later cleanup reason" "cleanup" (Find-Check $checks "ext.hold").reason
    foreach ($owned in (Read-PidFile $pidFile)) { Stop-Tree $owned }
}

It "fixture-version-timeout" {
    $results = New-Results "fixture-version-timeout"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.after-version" -Group "z-after" -ScriptPath $externalHost -Arguments @("-Mode", "pass") -Requirements @("godot") -TimeoutSeconds 30)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog, "-PrepTimeoutSeconds", "20",
        "-Test", "ext.after-version", "-ResultsRoot", $results
    ) -Environment @{ GODOT = $fakeExe; FAKE_ROLE = "godot"; FAKE_GODOT = "version-sleep" } -WaitMs 180000
    Assert-Exit $result 3
    $summary = Read-Summary $result.Stdout
    $versionStep = $null
    foreach ($step in @($summary.Json.preparation.steps)) {
        if ([string] $step.name -eq "godot-version") { $versionStep = $step }
    }
    Assert-True "version step" ($null -ne $versionStep)
    Assert-Equal "version timed out" "cancelled" $versionStep.outcome
    Assert-Equal "version timeout reason" "timeout" $versionStep.reason
    Assert-Equal "independent check passed" "passed" (Find-Check (Get-Checks $summary) "ext.after-version").outcome
    Assert-Equal "version probe used appdata" "per-check-appdata" ([string] $summary.Json.environment.engineIsolation)
}

It "fixture-version-nonzero" {
    $results = New-Results "fixture-version-nonzero"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.after-version" -Group "z-after" -ScriptPath $externalHost -Arguments @("-Mode", "pass") -Requirements @("godot") -TimeoutSeconds 30)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.after-version", "-ResultsRoot", $results
    ) -Environment @{ GODOT = $fakeExe; FAKE_ROLE = "godot"; FAKE_GODOT = "version-fail" } -WaitMs 180000
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    $versionStep = $null
    foreach ($step in @($summary.Json.preparation.steps)) {
        if ([string] $step.name -eq "godot-version") { $versionStep = $step }
    }
    Assert-True "version step" ($null -ne $versionStep)
    Assert-Equal "version unknown" "unknown" $versionStep.outcome
    Assert-Equal "version nonzero" "nonzero" $versionStep.reason
    Assert-True "prep not blocked" ([string] $summary.Json.preparation.outcome -ne "blocked")
    Assert-Equal "version text" "unknown" ([string] $summary.Json.environment.godotVersion)
    Assert-Equal "check still passed" "passed" (Find-Check (Get-Checks $summary) "ext.after-version").outcome
}

It "fixture-assembly-built-this-run" {
    $results = New-Results "fixture-assembly-built"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.game" -Group "a-game" -ScriptPath $externalHost -Arguments @("-Mode", "marker", "-UserData", "{output}/user-data") -Requirements @("godot") -NeedsGameBuild $true -NeedsImport $true)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.game", "-ResultsRoot", $results
    ) -Environment @{ GODOT = $fakeExe; FAKE_ROLE = "godot"; FAKE_GODOT = "fail" } -WaitMs 300000
    Assert-Exit $result 2
    $summary = Read-Summary $result.Stdout
    $expected = Join-Path $root ".godot/mono/temp/bin/Debug/Train.dll"
    Assert-Equal "built path" $expected ([string] $summary.Json.environment.gameAssembly)
    Assert-Equal "built reason" "built-this-run" ([string] $summary.Json.environment.gameAssemblyReason)
    Assert-True "built file exists" (Test-Path -LiteralPath $expected)
    $versionStep = $null
    foreach ($step in @($summary.Json.preparation.steps)) {
        if ([string] $step.name -eq "godot-version") { $versionStep = $step }
    }
    Assert-Equal "version probed" "probed" ([string] $versionStep.reason)
    Assert-Equal "import still blocks the check" "blocked" (Find-Check (Get-Checks $summary) "ext.game").outcome
}

It "fixture-external-build-blocked" {
    $results = New-Results "fixture-external-build"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.build" -Group "a-build" -ScriptPath $externalHost -Arguments @("-Mode", "marker", "-UserData", "{output}/user-data") -NeedsGameBuild $true)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog, "-DotnetPath", $fakeExe,
        "-Test", "ext.build", "-Test", "Fixture.PersistPassTests.Restores_marker",
        "-ResultsRoot", $results
    ) -Environment @{ FAKE_GAME_BUILD = "fail" } -WaitMs 300000
    Assert-Exit $result 1
    $summary = Read-Summary $result.Stdout
    Assert-Equal "game source failure" "failed" $summary.Json.preparation.outcome
    $checks = Get-Checks $summary
    $external = Find-Check $checks "ext.build"
    $later = Find-Check $checks "Fixture.PersistPassTests.Restores_marker"
    Assert-Equal "build blocked" "blocked" $external.outcome
    Assert-Equal "build reason" "build" $external.reason
    Assert-Equal "sibling passed" "passed" $later.outcome
    $started = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter started.txt -ErrorAction SilentlyContinue
    Assert-Equal "script not started" 0 @($started).Count
    Assert-True "game build log kept" (Test-Path -LiteralPath (Join-Path $summary.Directory "game-build.log"))
}

It "fixture-game-build-required" {
    $results = New-Results "fixture-game-build-required"
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", "none", "-BuildGame",
        "-Test", "Fixture.PersistPassTests.Restores_marker", "-ResultsRoot", $results
    ) -Unset @("GODOT") -WaitMs 300000
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    Assert-CommonSummary $summary -PrepCount 2
    Assert-Equal "requested game build recorded" $true $summary.Json.selection.buildGame
    Assert-True "report does not invent a CI selection" ($summary.Report -notlike "*常规选择*")
    $game = @($summary.Json.preparation.steps | Where-Object name -eq "game-build")
    Assert-Equal "one shared game build" 1 $game.Count
    Assert-Equal "game build passed without Godot" "passed" $game[0].outcome
    Assert-True "compiles game project" ($game[0].command -contains (Join-Path $root "Train.csproj"))
    Assert-Equal "unit passed" "passed" (Find-Check (Get-Checks $summary) "Fixture.PersistPassTests.Restores_marker").outcome
    Assert-Equal "no engine needed" "not-required" $summary.Json.environment.godot
    Assert-True "build log retained" (Test-Path -LiteralPath (Join-Path $summary.Directory "game-build.log"))
}

It "fixture-game-build-failed" {
    $results = New-Results "fixture-game-build-failed"
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", "none", "-DotnetPath", $fakeExe, "-BuildGame",
        "-Test", "Fixture.PersistPassTests.Restores_marker", "-ResultsRoot", $results
    ) -Environment @{ FAKE_GAME_BUILD = "fail" } -Unset @("GODOT") -WaitMs 300000
    Assert-Exit $result 1
    $summary = Assert-FinalResult $result 1
    Assert-Equal "required preparation failed" "failed" $summary.Json.preparation.outcome
    Assert-Equal "failed build remains non-success" 1 $summary.Json.exitCode
    Assert-Equal "independent unit still passed" "passed" (Find-Check (Get-Checks $summary) "Fixture.PersistPassTests.Restores_marker").outcome
    Assert-Contains "build failure retained" (Get-Content (Join-Path $summary.Directory "game-build.log") -Raw) "error CS1002"
}

It "fixture-game-build-priority" {
    Ensure-Sentinel
    foreach ($mode in @("cleanup", "timeout")) {
        $results = New-Results "fixture-game-build-$mode"
        $pidFile = Join-Path $results "pids.txt"
        $catalog = Join-Path $results "catalog.json"
        Write-ExternalCatalog $catalog @(
            (New-CheckSpec -Id "ext.hold" -Group "a-hold" -ScriptPath $externalHost -Arguments @("-Mode", "hold", "-PidFile", $pidFile) -TimeoutSeconds 30)
        )
        $environment = @{ FAKE_GAME_BUILD = "fail" }
        if ($mode -eq "cleanup") { $environment.TRAIN_RUN_TESTS_FAULT = "cleanup-kill" }
        $result = Invoke-Runner -ArgumentList @(
            "-Project", $project, "-RunnerCatalog", $catalog, "-DotnetPath", $fakeExe, "-BuildGame",
            "-TestHostPath", $hostPath, "-Test", "ext.hold", "-TimeoutSeconds", "12", "-ResultsRoot", $results
        ) -Environment $environment -WaitMs 180000
        $ownedPids = @(Read-PidFile $pidFile)
        try {
            Assert-True "$mode host started" ($ownedPids.Count -gt 0)
            $summary = Assert-FinalResult $result 1
            Assert-Equal "$mode preparation failed" "failed" $summary.Json.preparation.outcome
            $hold = Find-Check (Get-Checks $summary) "ext.hold"
            Assert-Equal "$mode reason retained" $mode $hold.reason
            Assert-Equal "$mode outcome retained" $(if ($mode -eq "cleanup") { "blocked" } else { "cancelled" }) $hold.outcome
            Assert-True "$mode sentinel preserved" (Test-Alive $script:sentinel.Id)
        }
        finally { foreach ($owned in $ownedPids) { Stop-Tree $owned } }
    }
}

It "fixture-shared-export" {
    $results = New-Results "fixture-shared-export"
    $catalog = Join-Path $results "catalog.json"
    $exportLog = Join-Path $results "export.log"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.export-a" -Group "a-export" -ScriptPath $externalHost -Arguments @("-Mode", "export-path", "-ExportBinary", "{exportBinary}") -Requirements @("godot", "export") -NeedsExport $true),
        (New-CheckSpec -Id "ext.export-b" -Group "b-export" -ScriptPath $externalHost -Arguments @("-Mode", "export-path", "-ExportBinary", "{exportBinary}") -Requirements @("godot", "export") -NeedsExport $true)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.export-a", "-Test", "ext.export-b", "-ResultsRoot", $results
    ) -Environment @{ GODOT = $fakeExe; FAKE_ROLE = "godot"; FAKE_EXPORT_LOG = $exportLog } -WaitMs 300000
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    $exportSteps = @($summary.Json.preparation.steps | Where-Object name -eq "windows-export")
    Assert-Equal "one shared export" 1 $exportSteps.Count
    $binary = Join-Path $summary.Directory "exports/windows/train.exe"
    Assert-True "shared binary exists" (Test-Path -LiteralPath $binary -PathType Leaf)
    $logs = @(Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter stdout.log)
    $text = (($logs | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n")
    Assert-Equal "both checks receive shared binary" 2 ([regex]::Matches($text, [regex]::Escape("EXPORT_BINARY=$binary")).Count)
    $exportText = [System.IO.File]::ReadAllText($exportLog)
    Assert-Equal "export invoked once" 1 ([regex]::Matches($exportText, "--export-release").Count)
    Assert-Contains "export keeps real appdata" $exportText "APPDATA=$env:APPDATA"
    Assert-Contains "export recovery mode" $exportText "--recovery-mode"
    Assert-Contains "export explicit log" $exportText "--log-file"
    Assert-Contains "export log stays in run" $exportText (Join-Path $summary.Directory "engine/export/godot.log")
    Assert-True "export binary is final argument" ($exportText.TrimEnd().EndsWith($binary, [System.StringComparison]::Ordinal))
}

It "fixture-shared-export-failure" {
    $results = New-Results "fixture-shared-export-failure"
    $catalog = Join-Path $results "catalog.json"
    $exportLog = Join-Path $results "export.log"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.export-a" -Group "a-export" -ScriptPath $externalHost -Arguments @("-Mode", "export-path", "-ExportBinary", "{exportBinary}") -Requirements @("godot", "export") -NeedsExport $true),
        (New-CheckSpec -Id "ext.export-b" -Group "b-export" -ScriptPath $externalHost -Arguments @("-Mode", "export-path", "-ExportBinary", "{exportBinary}") -Requirements @("godot", "export") -NeedsExport $true)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.export-a", "-Test", "ext.export-b", "-ResultsRoot", $results
    ) -Environment @{ GODOT = $fakeExe; FAKE_ROLE = "godot"; FAKE_EXPORT = "fail"; FAKE_EXPORT_LOG = $exportLog } -WaitMs 300000
    Assert-Exit $result 2
    $summary = Read-Summary $result.Stdout
    foreach ($id in @("ext.export-a", "ext.export-b")) {
        $check = Find-Check (Get-Checks $summary) $id
        Assert-Equal "$id blocked" "blocked" $check.outcome
        Assert-Equal "$id export reason" "export" $check.reason
    }
    $exportSteps = @($summary.Json.preparation.steps | Where-Object name -eq "windows-export")
    Assert-Equal "one failed export" 1 $exportSteps.Count
    Assert-Equal "export preparation blocked" "blocked" $exportSteps[0].outcome
    Assert-True "export log retained" (Test-Path -LiteralPath (Join-Path $summary.Directory "export-windows.log") -PathType Leaf)
    $checkLogs = @(Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter stdout.log -ErrorAction SilentlyContinue)
    Assert-Equal "checks not started" 0 $checkLogs.Count
    Assert-Equal "export invoked once" 1 ([regex]::Matches([System.IO.File]::ReadAllText($exportLog), "--export-release").Count)
}

It "fixture-content-input" {
    $results = New-Results "fixture-content-input"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.content-input" -Group "content-input" -ScriptPath $externalHost -Arguments @("-Mode", "content-input", "-UserData", "{output}") -NeedsContentInput $true)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.content-input", "-ResultsRoot", $results
    ) -WaitMs 300000
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    Assert-Equal "content input passed" "passed" (Find-Check (Get-Checks $summary) "ext.content-input").outcome
    $input = Join-Path $summary.Directory "groups/content-input/ext.content-input/output/content-input"
    Assert-True "input retained as artifact" (Test-Path -LiteralPath $input -PathType Container)
}

It "fixture-player-data-change" {
    $results = New-Results "fixture-player-data-change"
    $catalog = Join-Path $results "catalog.json"
    $privateAppData = Join-Path $results "private-appdata"
    New-Item -ItemType Directory -Force -Path $privateAppData | Out-Null
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.mutate-player-data" -Group "player-data" -ScriptPath $externalHost -Arguments @("-Mode", "mutate-player-data"))
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.mutate-player-data", "-ResultsRoot", $results
    ) -Environment @{ APPDATA = $privateAppData; LOCALAPPDATA = $privateAppData } -WaitMs 300000
    Assert-Exit $result 2
    $summary = Read-Summary $result.Stdout
    Assert-Equal "check itself passed" "passed" (Find-Check (Get-Checks $summary) "ext.mutate-player-data").outcome
    Assert-Equal "player data changed" $false ([bool] $summary.Json.environment.gameUserDataUnchanged)
    $isolationStep = @($summary.Json.preparation.steps | Where-Object name -eq "player-user-data-isolation")
    Assert-Equal "one isolation step" 1 $isolationStep.Count
    Assert-Equal "isolation blocked" "blocked" $isolationStep[0].outcome
    Assert-Equal "isolation reason" "user-data-changed" $isolationStep[0].reason
    Assert-Contains "report isolation blocker" $summary.Report "player-user-data-isolation blocked user-data-changed"
    Assert-True "private player data changed" (Test-Path -LiteralPath (Join-Path $privateAppData "Godot/app_userdata/Train/mutated.txt") -PathType Leaf)
}

It "fixture-external-timeout-tree" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $results = New-Results "fixture-external-timeout"
    $pidFile = Join-Path $results "pids.txt"
    $fixtureRepo = New-BindingRepository "timeout-inventory" $bindingSource
    $catalog = Join-Path $fixtureRepo.Repo "scripts/runner-catalog.json"
    $fixtureChecks = @(
        (New-CheckSpec -Id "ext.timeout-hold" -Group "a-hold" -ScriptPath $externalHost -Arguments @("-Mode", "hold", "-PidFile", $pidFile) -TimeoutSeconds 30),
        (New-CheckSpec -Id "ext.timeout-later" -Group "z-later" -ScriptPath $externalHost -Arguments @("-Mode", "pass") -TimeoutSeconds 30)
    )
    # 库存来自运行器所在仓库；在隔离仓库提供同名库存，不依赖生产登记项。
    $fixtureInventory = foreach ($check in $fixtureChecks) {
        @{
            id = $check["id"]; entry = $externalHost
            contract = "验证超时或成功后保留耗时状态。"; boundary = "仅运行测试假进程。"
            destination = "测试编排摘要。"; status = "support-harness"
            durationStatus = "unmeasured"; claim = "orchestration"; requirements = @()
        }
    }
    Write-ExternalCatalog $catalog $fixtureChecks -Inventory $fixtureInventory
    $started = [DateTime]::UtcNow
    $result = Invoke-Runner -RunnerPath (Join-Path $fixtureRepo.Repo "scripts/run-tests.ps1") -WorkingDirectory $fixtureRepo.Repo -Unset $script:GitIsolation -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.timeout-hold", "-Test", "ext.timeout-later",
        "-TimeoutSeconds", "12", "-ResultsRoot", $results
    ) -WaitMs 180000
    $elapsed = ([DateTime]::UtcNow - $started).TotalSeconds
    Assert-True "bounded $elapsed" ($elapsed -lt 90)
    Assert-Exit $result 3
    $summary = Read-Summary $result.Stdout
    $checks = Get-Checks $summary
    $hold = Find-Check $checks "ext.timeout-hold"
    $later = Find-Check $checks "ext.timeout-later"
    Assert-Equal "hold cancelled" "cancelled" $hold.outcome
    Assert-Equal "hold reason" "timeout" $hold.reason
    Assert-Equal "later passed" "passed" $later.outcome
    $holdInventory = Find-Inventory $summary "ext.timeout-hold"
    $laterInventory = Find-Inventory $summary "ext.timeout-later"
    Assert-Equal "cancelled inventory is partial" "partial" ([string] $holdInventory.durationStatus)
    Assert-True "partial duration kept" ([int] $holdInventory.durationMs -gt 0)
    Assert-Equal "partial duration matches the check" ([int] $hold.durationMs) ([int] $holdInventory.durationMs)
    Assert-Equal "passed inventory is measured" "measured-this-run" ([string] $laterInventory.durationStatus)
    Assert-Equal "measured duration matches the check" ([int] $later.durationMs) ([int] $laterInventory.durationMs)
    Assert-True "sentinel" (Test-Alive $sentinelId)
    foreach ($owned in (Read-PidFile $pidFile)) {
        if (Test-Alive $owned) { $script:owned.Add($owned) }
        Assert-True "killed $owned" (-not (Test-Alive $owned))
    }
}

It "fixture-external-cancel" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $results = New-Results "fixture-external-cancel"
    $pidFile = Join-Path $results "pids.txt"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.hold" -Group "a-hold" -ScriptPath $externalHost -Arguments @("-Mode", "hold", "-PidFile", $pidFile) -TimeoutSeconds 60),
        (New-CheckSpec -Id "ext.later" -Group "z-later" -ScriptPath $externalHost -Arguments @("-Mode", "pass") -TimeoutSeconds 30)
    )
    $handle = Open-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.hold", "-Test", "ext.later",
        "-TimeoutSeconds", "60", "-ResultsRoot", $results
    ) -WaitMs 180000
    try {
        Assert-True "host started" (Wait-PidFile $pidFile 90)
        foreach ($owned in (Read-PidFile $pidFile)) { $script:owned.Add($owned) }
        $run = Get-ChildItem -LiteralPath $results -Directory | Select-Object -First 1
        Assert-True "run dir" ($null -ne $run)
        [System.IO.File]::WriteAllText((Join-Path $run.FullName "cancel.request"), "cancel")
        $result = Close-Runner $handle
        $handle = $null
        Assert-Exit $result 3
        $checks = Get-Checks (Read-Summary $result.Stdout)
        $hold = Find-Check $checks "ext.hold"
        $later = Find-Check $checks "ext.later"
        Assert-Equal "hold cancel" "cancelled" $hold.outcome
        Assert-Equal "hold reason" "cancel" $hold.reason
        Assert-Equal "later skipped" "not-executed" $later.outcome
        Assert-True "sentinel" (Test-Alive $sentinelId)
        foreach ($owned in (Read-PidFile $pidFile)) { Assert-True "killed $owned" (-not (Test-Alive $owned)) }
    }
    finally {
        if ($handle) { Close-Runner $handle | Out-Null }
    }
}

It "fixture-external-early-exit" {
    $results = New-Results "fixture-external-early"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.fail" -Group "a-early" -ScriptPath $externalHost -Arguments @("-Mode", "fail") -TimeoutSeconds 30)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.fail", "-Test", "Fixture.PersistPassTests.Restores_marker",
        "-ResultsRoot", $results
    ) -WaitMs 300000
    Assert-Exit $result 1
    $summary = Read-Summary $result.Stdout
    $checks = Get-Checks $summary
    $failed = Find-Check $checks "ext.fail"
    $later = Find-Check $checks "Fixture.PersistPassTests.Restores_marker"
    Assert-Equal "early failed" "failed" $failed.outcome
    Assert-Equal "early reason" "exit:1" $failed.reason
    Assert-Equal "later passed" "passed" $later.outcome
    $log = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter stdout.log | Select-Object -First 1
    Assert-True "log kept" ($null -ne $log)
    Assert-Contains "first evidence" ([System.IO.File]::ReadAllText($log.FullName)) "FAIL-KEPT"
}

It "fixture-external-cleanup" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $results = New-Results "fixture-external-cleanup"
    $pidFile = Join-Path $results "pids.txt"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.hold" -Group "a-hold" -ScriptPath $externalHost -Arguments @("-Mode", "hold", "-PidFile", $pidFile) -TimeoutSeconds 30),
        (New-CheckSpec -Id "ext.later" -Group "z-later" -ScriptPath $externalHost -Arguments @("-Mode", "pass") -TimeoutSeconds 30)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog, "-TestHostPath", $hostPath,
        "-Test", "ext.hold", "-Test", "ext.later",
        "-TimeoutSeconds", "12", "-ResultsRoot", $results
    ) -Environment @{ TRAIN_RUN_TESTS_FAULT = "cleanup-kill" } -WaitMs 180000
    foreach ($owned in (Read-PidFile $pidFile)) { $script:owned.Add($owned) }
    Assert-Exit $result 2
    $checks = Get-Checks (Read-Summary $result.Stdout)
    $hold = Find-Check $checks "ext.hold"
    $later = Find-Check $checks "ext.later"
    Assert-Equal "cleanup blocked" "blocked" $hold.outcome
    Assert-Equal "cleanup reason" "cleanup" $hold.reason
    Assert-Equal "later skipped" "not-executed" $later.outcome
    Assert-True "sentinel" (Test-Alive $sentinelId)
    $alive = 0
    foreach ($owned in (Read-PidFile $pidFile)) { if (Test-Alive $owned) { $alive++ } }
    Assert-True "children kept" ($alive -ge 1)
}

It "fixture-external-isolation" {
    $results = New-Results "fixture-external-isolation"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.isolate" -Group "a-isolate" -ScriptPath $externalHost -Arguments @("-Mode", "isolate", "-UserData", "{output}/user-data") -TimeoutSeconds 30)
    )
    $before = Get-AppDataStamp
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.isolate", "-ResultsRoot", $results
    ) -Environment @{ TRAIN_FIXTURE_INHERITED_SWITCH = "1" } -WaitMs 300000
    $after = Get-AppDataStamp
    Assert-Equal "appdata unchanged" $before $after
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    Assert-Equal "summary userdata" $true ([bool] $summary.Json.environment.gameUserDataUnchanged)
    $log = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter stdout.log | Select-Object -First 1
    $text = [System.IO.File]::ReadAllText($log.FullName)
    Assert-Contains "inherited switch stripped" $text "INHERITED_SWITCH=empty"
    Assert-Contains "temp isolated" $text (Join-Path $summary.Directory "tmp")
    $markers = @(Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter marker.txt)
    Assert-Equal "one marker" 1 $markers.Count
    Assert-True "marker in user-data" ($markers[0].FullName -like "*\user-data\*")
}

It "fixture-external-serial" {
    $results = New-Results "fixture-external-serial"
    $lock = Join-Path $results "serial.lock"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.serial-a" -Group "a-serial" -ScriptPath $externalHost -Arguments @("-Mode", "serial", "-Lock", $lock, "-Id", "a") -TimeoutSeconds 30),
        (New-CheckSpec -Id "ext.serial-b" -Group "b-serial" -ScriptPath $externalHost -Arguments @("-Mode", "serial", "-Lock", $lock, "-Id", "b") -TimeoutSeconds 30)
    )
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.serial-a", "-Test", "ext.serial-b",
        "-ResultsRoot", $results
    ) -WaitMs 300000
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    $checks = Get-Checks $summary
    Assert-Equal "a passed" "passed" (Find-Check $checks "ext.serial-a").outcome
    Assert-Equal "b passed" "passed" (Find-Check $checks "ext.serial-b").outcome
    $logs = @(Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter stdout.log)
    $text = (($logs | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }) -join "`n")
    Assert-Contains "a clear" $text "CLEAR a"
    Assert-Contains "b clear" $text "CLEAR b"
    Assert-True "no overlap" ($text -notlike "*OVERLAP*")
}

It "fixture-isolation-timeout" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $results = New-Results "fixture-isolation-timeout"
    $pidFile = Join-Path $results "pids.txt"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.hold" -Group "a-hold" -ScriptPath $externalHost -Arguments @("-Mode", "hold", "-PidFile", $pidFile) -Requirements @("godot") -TimeoutSeconds 30),
        (New-CheckSpec -Id "ext.later" -Group "z-later" -ScriptPath $externalHost -Arguments @("-Mode", "pass") -TimeoutSeconds 30)
    )
    $before = Get-AppDataStamp
    $overrideBefore = Get-OverrideHash
    $result = Invoke-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.hold", "-Test", "ext.later",
        "-TimeoutSeconds", "4", "-ResultsRoot", $results
    ) -Environment @{ GODOT = $fakeExe } -WaitMs 180000
    foreach ($owned in (Read-PidFile $pidFile)) { $script:owned.Add($owned) }
    Assert-Exit $result 3
    $summary = Read-Summary $result.Stdout
    $checks = Get-Checks $summary
    $hold = Find-Check $checks "ext.hold"
    $later = Find-Check $checks "ext.later"
    Assert-Equal "hold cancelled" "cancelled" $hold.outcome
    Assert-Equal "hold reason" "timeout" $hold.reason
    Assert-Equal "later still ran" "passed" $later.outcome
    $log = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter stdout.log | Select-Object -First 1
    Assert-True "first log kept" ($null -ne $log)
    $text = [System.IO.File]::ReadAllText($log.FullName)
    $appData = Join-Path $summary.Directory "engine/ext.hold/appdata"
    Assert-Contains "private appdata" $text $appData
    Assert-True "check appdata kept" (Test-Path -LiteralPath $appData)
    Assert-Equal "player data unchanged" $before (Get-AppDataStamp)
    Assert-OverrideUntouched $overrideBefore
    Assert-True "sentinel" (Test-Alive $sentinelId)
    foreach ($owned in (Read-PidFile $pidFile)) { Assert-True "killed $owned" (-not (Test-Alive $owned)) }
}

It "fixture-isolation-cancel" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $results = New-Results "fixture-isolation-cancel"
    $pidFile = Join-Path $results "pids.txt"
    $catalog = Join-Path $results "catalog.json"
    Write-ExternalCatalog $catalog @(
        (New-CheckSpec -Id "ext.hold" -Group "a-hold" -ScriptPath $externalHost -Arguments @("-Mode", "hold", "-PidFile", $pidFile) -Requirements @("godot") -TimeoutSeconds 60),
        (New-CheckSpec -Id "ext.later" -Group "z-later" -ScriptPath $externalHost -Arguments @("-Mode", "pass") -TimeoutSeconds 30)
    )
    $before = Get-AppDataStamp
    $overrideBefore = Get-OverrideHash
    $handle = Open-Runner -ArgumentList @(
        "-Project", $project, "-RunnerCatalog", $catalog,
        "-Test", "ext.hold", "-Test", "ext.later",
        "-TimeoutSeconds", "60", "-ResultsRoot", $results
    ) -Environment @{ GODOT = $fakeExe } -WaitMs 180000
    try {
        Assert-True "host started" (Wait-PidFile $pidFile 90)
        $run = Wait-RunDirectory $results 5
        Assert-True "run dir" (-not [string]::IsNullOrWhiteSpace($run))
        [System.IO.File]::WriteAllText((Join-Path $run "cancel.request"), "cancel")
        $result = Close-Runner $handle
        $handle = $null
        foreach ($owned in (Read-PidFile $pidFile)) { $script:owned.Add($owned) }
        Assert-Exit $result 3
        $summary = Read-Summary $result.Stdout
        $checks = Get-Checks $summary
        $hold = Find-Check $checks "ext.hold"
        $later = Find-Check $checks "ext.later"
        Assert-Equal "hold cancel" "cancelled" $hold.outcome
        Assert-Equal "cancel reason" "cancel" $hold.reason
        Assert-Equal "later stopped" "not-executed" $later.outcome
        $log = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter stdout.log | Select-Object -First 1
        Assert-True "first log kept" ($null -ne $log)
        $text = [System.IO.File]::ReadAllText($log.FullName)
        Assert-Contains "private appdata" $text (Join-Path $summary.Directory "engine/ext.hold/appdata")
        Assert-Equal "player data unchanged" $before (Get-AppDataStamp)
        Assert-OverrideUntouched $overrideBefore
        Assert-True "sentinel" (Test-Alive $sentinelId)
        foreach ($owned in (Read-PidFile $pidFile)) { Assert-True "cancel killed $owned" (-not (Test-Alive $owned)) }
    }
    finally {
        if ($handle) { Close-Runner $handle | Out-Null }
        foreach ($owned in (Read-PidFile $pidFile)) { Stop-Tree $owned }
    }
}

It "fixture-multi-project-module" {
    $repo = Join-Path $evidence "multi-project"
    $scriptDir = Join-Path $repo "scripts"
    New-Item -ItemType Directory -Force -Path $scriptDir | Out-Null
    Copy-Item -LiteralPath $runner -Destination (Join-Path $scriptDir "run-tests.ps1")
    New-Item -ItemType Directory -Force -Path (Join-Path $scriptDir "checks") | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "checks/common-checks.ps1") -Destination (Join-Path $scriptDir "checks")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "metadata-reader") -Destination $scriptDir -Recurse
    $source = @'
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace MODULE;
[TestClass, TestCategory("family:unit"), TestCategory("scope:shared")]
public sealed class RuleTests {
    [TestMethod] public void Passes() { Assert.AreEqual(2, 1 + 1); }
}

'@
    foreach ($module in @("Core", "Content")) {
        Write-Project (Join-Path $repo "Tests/$module") "$module.Tests" $sdk ($source.Replace("MODULE", $module))
    }
    $repoRunner = Join-Path $scriptDir "run-tests.ps1"
    $all = Invoke-Runner -RunnerPath $repoRunner -WorkingDirectory $repo -ArgumentList @("-Family", "unit") -WaitMs 300000
    Assert-Exit $all 0
    $summary = Read-Summary $all.Stdout
    $checks = @(Get-Checks $summary)
    Assert-Equal "both projects executed" 2 $checks.Count
    Assert-Equal "one core" 1 @($checks | Where-Object module -EQ core).Count
    Assert-Equal "one content" 1 @($checks | Where-Object module -EQ content).Count
    Assert-Equal "both identities" 2 @($summary.Json.environment.projects).Count
    Assert-Equal "core identity" 1 @($summary.Json.environment.projects | Where-Object module -EQ core).Count
    Assert-Equal "content identity" 1 @($summary.Json.environment.projects | Where-Object module -EQ content).Count
    Add-Content -LiteralPath (Join-Path $repo "Tests/Content/Content.Tests.cs") -Value "broken syntax"
    $core = Invoke-Runner -RunnerPath $repoRunner -WorkingDirectory $repo -ArgumentList @("-Module", "core", "-Family", "unit") -WaitMs 300000
    Assert-Exit $core 0
    Assert-Equal "core selected once" 1 @(Get-Checks (Read-Summary $core.Stdout)).Count
    $missing = Invoke-Runner -RunnerPath $repoRunner -WorkingDirectory $repo -ArgumentList @("-Module", "scenes", "-List") -WaitMs 30000
    Assert-Exit $missing 2
    Assert-Contains "missing module blocked" $missing.Stderr "module-entry-missing scenes"
    $default = Invoke-Runner -RunnerPath $repoRunner -WorkingDirectory $repo -ArgumentList @('-Correctness', '-List') -WaitMs 30000
    Assert-Exit $default 2
    Assert-Contains 'missing default project blocked' $default.Stderr 'module-entry-missing godot'
}

It "real-module-contract-selection" {
    foreach ($module in @('core', 'content')) {
        $result = Invoke-Runner -ArgumentList @('-Module', $module, '-Correctness', '-List') -WaitMs 300000
        Assert-Exit $result 0
        Assert-Contains "$module structure" $result.Stdout 'CASE id=contracts.structure '
        Assert-Contains "$module support" $result.Stdout "module=$module"
        Assert-Contains "$module support group" $result.Stdout "scopes=$module-contract-support"
    }
    $daily = Invoke-Runner -ArgumentList @('-Module', 'core', '-Family', 'unit', '-List') -WaitMs 300000
    Assert-Exit $daily 0
    Assert-Contains 'daily core support group' $daily.Stdout 'scopes=core-contract-support'
}

It "fixture-sdk-blocked" {
    $fixture = New-BindingRepository "missing-sdk" $bindingSource
    Set-Content -LiteralPath (Join-Path $fixture.Repo "global.json") -Encoding utf8 -Value '{"sdk":{"version":"10.0.999","rollForward":"disable"}}'
    $result = Invoke-Runner -RunnerPath (Join-Path $fixture.Repo "scripts/run-tests.ps1") -WorkingDirectory $fixture.Repo -Unset $script:GitIsolation -ArgumentList @(
        "-Project", (Join-Path $fixture.Repo "Binding.csproj"), "-Family", "unit", "-ResultsRoot", (New-Results "fixture-sdk-blocked")
    ) -WaitMs 180000
    Assert-Exit $result 2
    $summary = Read-Summary $result.Stdout
    Assert-Equal "missing SDK blocked" "blocked" $summary.Json.preparation.outcome
    Assert-Equal "no checks executed" 0 (Get-Checks $summary).Count
}

It "fixture-build-failure-classification" {
    . (Join-Path $PSScriptRoot "checks/common-checks.ps1")
    foreach ($code in @('NU1605', 'NU1201', 'NU1101', 'MSB4019')) {
        Assert-Equal "$code repository failure" 'failed' (Get-BuildFailureKind "error ${code}: fixture failure")
    }
    $copyLocked = "warning MSB3026: Could not copy obj/Binding.dll. Beginning retry 1.`nerror MSB3027: Could not copy obj/Binding.dll. Exceeded retry count of 10. Failed.`nerror MSB3021: Unable to copy file obj/Binding.dll. The process cannot access the file because it is being used by another process."
    foreach ($message in @('error NETSDK1141: global.json SDK missing', 'error NU1301: service index unavailable', 'No .NET SDKs were found', $copyLocked)) {
        Assert-Equal "$message environment failure" 'blocked' (Get-BuildFailureKind $message)
    }
    Assert-Equal 'repository error takes precedence' 'failed' (Get-BuildFailureKind "error NU1301: service index unavailable`nerror NU1605: package downgrade")
    Assert-Equal 'warning does not hide repository error' 'failed' (Get-BuildFailureKind "error MSB4006: circular dependency`nwarning NU1900: Unable to load the service index")
    Assert-Equal 'unknown repository error takes precedence' 'failed' (Get-BuildFailureKind "error XYZ1234: fixture failure`nerror NU1301: service index unavailable")
    foreach ($code in @('NU1101', 'NU1301')) {
        $fixture = New-BindingRepository "classification-$code" $bindingSource
        $projectPath = Join-Path $fixture.Repo 'Binding.csproj'
        $xml = $sdk.Replace('</Project>', "<Target Name=`"FixtureFailure`" BeforeTargets=`"Build`"><Error Code=`"$code`" Text=`"fixture failure`" /></Target></Project>")
        Set-Content -LiteralPath $projectPath -Value $xml -Encoding utf8
        $result = Invoke-Runner -RunnerPath (Join-Path $fixture.Repo 'scripts/run-tests.ps1') -WorkingDirectory $fixture.Repo -Unset $script:GitIsolation -ArgumentList @(
            '-Project', $projectPath, '-Family', 'unit', '-ResultsRoot', (New-Results "classification-$code")
        ) -WaitMs 180000
        $expected = if ($code -eq 'NU1101') { 1 } else { 2 }
        $summary = Assert-FinalResult $result $expected
        Assert-Equal "$code preparation" $(if ($expected -eq 1) { 'failed' } else { 'blocked' }) $summary.Json.preparation.outcome
    }
}

function New-CoreIsolationRepository([string] $Name, [string] $Source = $bindingSource) {
    $fixture = New-BindingRepository $Name $bindingSource
    Copy-Item -LiteralPath (Join-Path $root 'global.json') -Destination $fixture.Repo
    Write-Project (Join-Path $fixture.Repo 'Tests/Core') 'Train.Core.Tests' $sdk $Source
    Remove-Item -LiteralPath (Join-Path $fixture.Repo 'Binding.csproj'), (Join-Path $fixture.Repo 'BindingTests.cs')
    Set-Content -LiteralPath (Join-Path $fixture.Repo 'scripts/checks/project-boundaries.json') -Value '{"projects":{"Train.Core.Tests":{"path":"Tests/Core/Train.Core.Tests.csproj","references":[]}}}' -Encoding utf8
    return $fixture
}

function Read-IsolatedRoot($Summary, [string] $Module = 'core') {
    $rootFile = Join-Path $Summary.Directory "isolation/$Module/root.txt"
    Assert-True "$Module isolated root recorded" (Test-Path -LiteralPath $rootFile)
    $isolatedRoot = (Get-Content -LiteralPath $rootFile -Raw).Trim()
    Assert-True "$Module owned root identity" ((Split-Path -Leaf $isolatedRoot) -like "train-isolation-*-$Module")
    return $isolatedRoot
}

It "fixture-isolated-reference-interruption" {
    foreach ($mode in @('failure', 'timeout', 'cancel')) {
        $fixture = New-CoreIsolationRepository "reference-$mode"
        $repo = $fixture.Repo
        $ready = Join-Path $repo 'reference.ready'
        $target = @'
  <Target Name="FixtureBuilt" AfterTargets="Build"><WriteLinesToFile File="$(MSBuildProjectDirectory)/bin/built.ready" Lines="ready" /></Target>
  <Target Name="FixtureReference" BeforeTargets="ResolveReferences" Condition="Exists('$(MSBuildProjectDirectory)/bin/built.ready')">
    <WriteLinesToFile File="READY" Lines="ready" />
    ACTION
  </Target>
'@
        $action = if ($mode -eq 'failure') { '<Error Code="NU1101" Text="reference fixture failure" />' } else { '<Exec Command="pwsh -NoProfile -Command &quot;Start-Sleep -Seconds 300&quot;" />' }
        $xml = $sdk.Replace('</Project>', $target.Replace('READY', $ready).Replace('ACTION', $action) + '</Project>')
        Write-Project (Join-Path $repo 'Tests/Core') 'Train.Core.Tests' $xml $bindingSource
        $results = New-Results "reference-$mode"
        $arguments = @('-Module', 'core', '-Correctness', '-ResultsRoot', $results)
        # 只有超时模式使用短上限；首次构建同受此上限约束，其余模式用默认值以免慢机器提前超时。
        if ($mode -eq 'timeout') { $arguments += @('-PrepTimeoutSeconds', '45') }
        $handle = Open-Runner -RunnerPath (Join-Path $repo 'scripts/run-tests.ps1') -WorkingDirectory $repo -Unset $script:GitIsolation -ArgumentList $arguments -WaitMs 240000
        try {
            if ($mode -eq 'cancel') {
                Assert-True 'reference phase reached' (Wait-PidFile $ready 90)
                $run = Wait-RunDirectory $results 5
                Set-Content -LiteralPath (Join-Path $run 'cancel.request') -Value 'cancel'
            }
            $result = Close-Runner $handle
            $handle = $null
            $summary = Read-Summary $result.Stdout
            $isolatedRoot = Read-IsolatedRoot $summary
            Assert-True "$mode isolated root removed" (-not (Test-Path -LiteralPath $isolatedRoot))
            $expected = if ($mode -eq 'failure') { 1 } else { 3 }
            $null = Assert-FinalResult $result $expected
            $referenceStep = @($summary.Json.preparation.steps | Where-Object name -EQ 'isolation-references-core')[0]
            if ($mode -ne 'failure') { Assert-Contains "$mode actual interruption" $referenceStep.reason ":${mode}:" }
            Assert-Contains "$mode source project diagnostic" $result.Stderr (Join-Path $repo 'Tests/Core/Train.Core.Tests.csproj')
            Assert-Contains "$mode reference log diagnostic" $result.Stderr (Join-Path $summary.Directory 'isolation/core/references.log')
            Assert-True "$mode reference evidence retained" (Test-Path -LiteralPath (Join-Path $summary.Directory 'isolation/core/references.log'))
        }
        finally { if ($handle) { Close-Runner $handle | Out-Null } }
    }
}

It "fixture-isolated-input-errors" {
    foreach ($mode in @('matrix-project', 'directory', 'matrix-missing', 'matrix-json', 'file-locked')) {
        $fixture = New-CoreIsolationRepository "input-errors-$mode"
        $repo = $fixture.Repo
        $references = @('Train.Core')
        if ($mode -eq 'file-locked') { $references = @() }
        $projects = @{ 'Train.Core.Tests' = @{ path = 'Tests/Core/Train.Core.Tests.csproj'; references = $references } }
        if ($mode -eq 'directory') { $projects['Train.Core'] = @{ path = 'Core/Train.Core.csproj'; references = @() } }
        @{ projects = $projects } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $repo 'scripts/checks/project-boundaries.json') -Encoding utf8
        if ($mode -eq 'matrix-missing') { Remove-Item -LiteralPath (Join-Path $repo 'scripts/checks/project-boundaries.json') }
        if ($mode -eq 'matrix-json') { Set-Content -LiteralPath (Join-Path $repo 'scripts/checks/project-boundaries.json') -Value '{broken' }
        $lock = $null
        try {
            if ($mode -eq 'file-locked') {
                $lock = [IO.File]::Open((Join-Path $repo 'Tests/Core/Train.Core.Tests.cs'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
            }
            $result = Invoke-Runner -RunnerPath (Join-Path $repo 'scripts/run-tests.ps1') -WorkingDirectory $repo -Unset $script:GitIsolation -ArgumentList @(
                '-Module', 'core', '-Correctness', '-ResultsRoot', (New-Results "input-errors-$mode")
            ) -WaitMs 180000
        }
        finally { if ($lock) { $lock.Dispose() } }
        $expected = if ($mode -eq 'file-locked') { 2 } else { 1 }
        $summary = Assert-FinalResult $result $expected
        Assert-Equal "$mode preparation classification" $(if ($mode -eq 'file-locked') { 'blocked' } else { 'failed' }) $summary.Json.preparation.outcome
        $isolatedRoot = Read-IsolatedRoot $summary
        Assert-True "$mode isolated root removed" (-not (Test-Path -LiteralPath $isolatedRoot))
    }
}

It "fixture-isolated-source-evaluation-log" {
    $fixture = New-CoreIsolationRepository 'source-evaluation-log'
    $repo = $fixture.Repo
    Write-Project (Join-Path $repo 'Core') 'Train.Core' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Import Project="missing.targets" /></Project>' 'public static class CoreFixture { }'
    Set-Content -LiteralPath (Join-Path $repo 'scripts/checks/project-boundaries.json') -Value '{"projects":{"Train.Core.Tests":{"path":"Tests/Core/Train.Core.Tests.csproj","references":["Train.Core"]},"Train.Core":{"path":"Core/Train.Core.csproj","references":[]}}}' -Encoding utf8
    $result = Invoke-Runner -RunnerPath (Join-Path $repo 'scripts/run-tests.ps1') -WorkingDirectory $repo -Unset $script:GitIsolation -ArgumentList @(
        '-Module', 'core', '-Correctness', '-ResultsRoot', (New-Results 'source-evaluation-log')
    ) -WaitMs 180000
    $summary = Assert-FinalResult $result 1
    $step = @($summary.Json.preparation.steps | Where-Object name -EQ 'isolation-references-core')[0]
    Assert-Equal 'source evaluation failure' 'msbuild-source:Train.Core:exit:1' $step.reason
    $actualLog = Join-Path $summary.Directory 'isolation/core/compile-Train.Core.log'
    Assert-True 'source evaluation log retained' (Test-Path -LiteralPath $actualLog)
    Assert-Contains 'source evaluation diagnostic log' $result.Stderr "log=$actualLog"
    Assert-Contains 'source evaluation source project' $result.Stderr (Join-Path $repo 'Tests/Core/Train.Core.Tests.csproj')
    Assert-True 'source evaluation root removed' (-not (Test-Path -LiteralPath (Read-IsolatedRoot $summary)))
}

It "fixture-isolated-reference-variants" {
    $foreignDir = Join-Path $evidence 'reference-foreign'
    Write-Project $foreignDir 'Train.Forbidden' '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>' 'public static class Forbidden { public static string Value() => "BINDING_VALUE"; }'
    $buildOutput = & dotnet build (Join-Path $foreignDir 'Train.Forbidden.csproj') --nologo -p:UseSharedCompilation=false 2>&1 | Out-String
    Assert-Equal "foreign assembly built: $buildOutput" 0 $LASTEXITCODE
    $foreignDll = Join-Path $foreignDir 'bin/Debug/net10.0/Train.Forbidden.dll'
    $hostDll = Join-Path $foreignDir 'Train.dll'
    Copy-Item -LiteralPath $foreignDll -Destination $hostDll
    foreach ($mode in @('project', 'resolved', 'output', 'metadata')) {
        $fixture = New-CoreIsolationRepository "reference-variant-$mode"
        $repo = $fixture.Repo
        $source = $bindingSource
        $extra = switch ($mode) {
            'project' { '<ItemGroup><ProjectReference Include="FOREIGN/Train.Forbidden.csproj" /></ItemGroup>' }
            'resolved' { "<ItemGroup><ReferencePath Include=`"$hostDll`" /></ItemGroup>" }
            'output' { '<Copy SourceFiles="$(TargetPath)" DestinationFiles="$(TargetDir)Train.Forbidden.dll" />' }
            'metadata' { '<ItemGroup><ReferencePath Remove="DLL" /></ItemGroup>' }
        }
        $extra = $extra.Replace('FOREIGN', $foreignDir).Replace('DLL', $foreignDll)
        if ($mode -eq 'output') { $target = "<Target Name=`"FixtureOutput`" AfterTargets=`"Build`">$extra</Target>" }
        else {
            $target = @'
<Target Name="FixtureBuilt" AfterTargets="Build"><WriteLinesToFile File="$(MSBuildProjectDirectory)/bin/built.ready" Lines="ready" /></Target>
<Target Name="FixtureReference" AfterTargets="ResolveReferences" Condition="Exists('$(MSBuildProjectDirectory)/bin/built.ready')">EXTRA</Target>
'@
            $target = $target.Replace('EXTRA', $extra)
        }
        if ($mode -eq 'metadata') {
            $target += "<ItemGroup><Reference Include=`"Train.Forbidden`"><HintPath>$foreignDll</HintPath><Private>false</Private></Reference></ItemGroup>"
            $source = $source.Replace('Assert.AreEqual("BINDING_VALUE", VersionValue);', 'Assert.AreEqual("BINDING_VALUE", Forbidden.Value());')
        }
        Write-Project (Join-Path $repo 'Tests/Core') 'Train.Core.Tests' ($sdk.Replace('</Project>', $target + '</Project>')) $source
        $result = Invoke-Runner -RunnerPath (Join-Path $repo 'scripts/run-tests.ps1') -WorkingDirectory $repo -Unset $script:GitIsolation -ArgumentList @(
            '-Module', 'core', '-Correctness', '-ResultsRoot', (New-Results "reference-variant-$mode")
        ) -WaitMs 180000
        $summary = Assert-FinalResult $result 1
        $step = @($summary.Json.preparation.steps | Where-Object name -EQ 'isolation-references-core')[0]
        $prefix = switch ($mode) { 'project' { 'project-reference:' }; 'resolved' { 'assembly-reference:' }; 'output' { 'output-assembly:' }; 'metadata' { 'metadata-reference:' } }
        Assert-True "$mode actual reference failure" ([string] $step.reason).StartsWith($prefix)
        if ($mode -eq 'resolved') { Assert-Equal 'resolved Train.dll only' "assembly-reference:$hostDll" $step.reason }
        if ($mode -eq 'metadata') { Assert-Equal 'metadata only' 'metadata-reference:Train.Core.Tests.dll:Train.Forbidden' $step.reason }
        $isolatedRoot = Read-IsolatedRoot $summary
        Assert-True "$mode isolated root removed" (-not (Test-Path -LiteralPath $isolatedRoot))
    }
}

It "fixture-isolated-build-cleanup" {
    $fixture = New-CoreIsolationRepository "isolated-cleanup"
    $repo = $fixture.Repo
    $testDir = Join-Path $repo "Tests/Core"
    $checksDir = Join-Path $repo "scripts/checks"
    $foreign = Join-Path ([IO.Path]::GetTempPath()) ("train-isolation-foreign-" + [guid]::NewGuid().ToString("N") + "-core")
    New-Item -ItemType Directory -Path $foreign | Out-Null
    try {
        foreach ($mode in @("success", "failure")) {
            if ($mode -eq "failure") { Add-Content -LiteralPath (Join-Path $testDir "Train.Core.Tests.cs") -Value "broken syntax" }
            $result = Invoke-Runner -RunnerPath (Join-Path $repo "scripts/run-tests.ps1") -WorkingDirectory $repo -Unset $script:GitIsolation -ArgumentList @(
                "-Module", "core", "-Correctness", "-ResultsRoot", (New-Results "fixture-isolated-cleanup-$mode")
            ) -WaitMs 300000
            Assert-Exit $result $(if ($mode -eq "success") { 0 } else { 1 })
            $summary = Read-Summary $result.Stdout
            $build = @($summary.Json.preparation.steps | Where-Object name -EQ "build")[0]
            $isolatedProject = [string] $build.command[2]
            Assert-True "built outside fixture" (-not $isolatedProject.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase))
            $isolatedRoot = Read-IsolatedRoot $summary
            Assert-True "$mode owned directory cleaned" (-not (Test-Path -LiteralPath $isolatedRoot))
            Assert-True "$mode foreign directory preserved" (Test-Path -LiteralPath $foreign)
            Assert-True "$mode evidence kept" (Test-Path -LiteralPath (Join-Path $summary.Directory "isolation/core/inputs.json"))
        }
        Write-Project (Join-Path $repo "Tests/Scenes") "Train.Scenes.Tests" $sdk ($bindingSource + "`nbroken syntax")
        Set-Content -LiteralPath (Join-Path $checksDir "project-boundaries.json") -Encoding utf8 -Value '{"projects":{"Train.Scenes.Tests":{"path":"Tests/Scenes/Train.Scenes.Tests.csproj","references":[]}}}'
        $scenes = Invoke-Runner -RunnerPath (Join-Path $repo "scripts/run-tests.ps1") -WorkingDirectory $repo -Unset $script:GitIsolation -ArgumentList @(
            "-Module", "scenes", "-Correctness", "-ResultsRoot", (New-Results "fixture-scenes-isolated-failure")
        ) -WaitMs 300000
        Assert-Exit $scenes 1
        $summary = Read-Summary $scenes.Stdout
        Assert-Equal "future Scenes compile failure" "failed" $summary.Json.preparation.outcome
        Assert-True "future Scenes inputs isolated" (Test-Path -LiteralPath (Join-Path $summary.Directory "isolation/scenes/inputs.json"))
    }
    finally { Remove-Item -LiteralPath $foreign -Force }
}

It "fixture-isolated-cleanup-failure-report" {
    if (-not $IsWindows) { throw 'blocked: Windows file sharing is required' }
    $foreign = Join-Path ([IO.Path]::GetTempPath()) ("train-isolation-foreign-" + [guid]::NewGuid().ToString('N') + '-core')
    New-Item -ItemType Directory -Path $foreign | Out-Null
    try {
        foreach ($mode in @('success', 'cancel', 'failure')) {
            $source = if ($mode -eq 'failure') { $bindingSource.Replace('Assert.AreEqual("BINDING_VALUE", VersionValue);', 'Assert.Fail("fixture check failure");') } else { $bindingSource }
            $fixture = New-CoreIsolationRepository "cleanup-report-$mode" $source
            $results = New-Results "cleanup-report-$mode"
            $ready = Join-Path $results 'test.ready'
            $release = Join-Path $results 'test.release'
            $handle = $null
            $lock = $null
            $isolatedRoot = $null
            try {
                $handle = Open-Runner -RunnerPath (Join-Path $fixture.Repo 'scripts/run-tests.ps1') -WorkingDirectory $fixture.Repo -Unset $script:GitIsolation -Environment @{ BINDING_READY = $ready; BINDING_RELEASE = $release } -ArgumentList @(
                    '-Module', 'core', '-Correctness', '-ResultsRoot', $results
                ) -WaitMs 180000
                Wait-BindingReady $handle $ready 120
                $run = Wait-RunDirectory $results 5
                $isolatedRoot = Read-IsolatedRoot ([pscustomobject]@{ Directory = $run })
                $lock = [IO.File]::Open((Join-Path $isolatedRoot 'Tests/Core/Train.Core.Tests.cs'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                if ($mode -eq 'cancel') { Set-Content -LiteralPath (Join-Path $run 'cancel.request') -Value 'cancel' }
                else { Set-Content -LiteralPath $release -Value 'release' }
                $result = Close-Runner $handle
                $handle = $null
                $expected = if ($mode -eq 'failure') { 1 } else { 2 }
                $summary = Assert-FinalResult $result $expected
                Assert-True "$mode cleanup errors recorded" (@($summary.Json.environment.isolationCleanupErrors).Count -gt 0)
                Assert-Contains "$mode cleanup report" $summary.Report '隔离目录清理失败'
                Assert-True "$mode no cleanup promise" ($summary.Report -notlike '*运行结束后临时目录会被清理*')
                Assert-True "$mode cleanup log retained" (Test-Path -LiteralPath (Join-Path $run 'isolation/cleanup.log'))
                Assert-True "$mode foreign directory preserved" (Test-Path -LiteralPath $foreign)
                $check = Find-Check (Get-Checks $summary) 'BindingFixture.BindingTests.Runs_built_sources'
                Assert-Equal "$mode check outcome retained" $(switch ($mode) { 'success' { 'passed' }; 'cancel' { 'cancelled' }; 'failure' { 'failed' } }) $check.outcome
            }
            finally {
                try {
                    Set-Content -LiteralPath $release -Value 'release'
                    if ($handle) { Close-Runner $handle | Out-Null }
                }
                finally {
                    if ($lock) { $lock.Dispose() }
                    if ($isolatedRoot -and (Test-Path -LiteralPath $isolatedRoot)) {
                        $resolvedRoot = [IO.Path]::GetFullPath($isolatedRoot)
                        Assert-Equal 'cleanup target belongs to temp' ([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')) (Split-Path -Parent $resolvedRoot)
                        Assert-True 'cleanup target is owned isolation' ((Split-Path -Leaf $resolvedRoot) -like 'train-isolation-*-core')
                        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
                    }
                }
            }
        }
    }
    finally { Remove-Item -LiteralPath $foreign -Force }
}

foreach ($reportState in @('before-report', 'after-report')) {
    It "fixture-entry-exception-cancel-$reportState" {
        $source = if ($reportState -eq 'before-report') { $bindingSource.Replace('Assert.AreEqual("BINDING_VALUE", VersionValue);', 'Assert.Fail("fixture check failure");') } else { $bindingSource }
        $fixture = New-CoreIsolationRepository "entry-exception-$reportState" $source
        $fixtureRunner = Join-Path $fixture.Repo 'scripts/run-tests.ps1'
        $text = [IO.File]::ReadAllText($fixtureRunner)
        $needle = if ($reportState -eq 'before-report') {
            '    Publish-Run -Parsed $parsed -Preparation $preparation -Environment $environment -Selection $selection -Checks $checks -Groups $groupReports -ExitCode $exitCode -Directory $runDirectory'
        } else { '    [Console]::Out.WriteLine("REPORT=$runDirectory")' }
        Assert-Equal "$reportState unique injection point" 1 ([regex]::Matches($text, [regex]::Escape($needle)).Count)
        $injection = "    [IO.File]::WriteAllText(`$cancelFile, 'cancel'); `$script:CancelRequested = `$true; if (-not (Test-CancelRequested)) { throw 'fixture cancellation was not recorded' }; throw 'fixture entry failure'`n"
        [IO.File]::WriteAllText($fixtureRunner, $text.Replace($needle, $injection + $needle), $utf8)
        $result = Invoke-Runner -RunnerPath $fixtureRunner -WorkingDirectory $fixture.Repo -Unset $script:GitIsolation -ArgumentList @(
            '-Module', 'core', '-Correctness', '-ResultsRoot', (New-Results "entry-exception-$reportState")
        ) -WaitMs 180000
        $summary = Assert-FinalResult $result 1
        Assert-Contains "$reportState injected entry failure" $result.Stderr 'error: fixture entry failure'
        if ($reportState -eq 'before-report') {
            Assert-Equal "$reportState entry failure outcome" 'failed' $summary.Json.preparation.outcome
            Assert-Equal "$reportState entry failure reason" 'runner-error' $summary.Json.preparation.reason
        }
        else {
            Assert-Equal "$reportState preparation retained" 'passed' $summary.Json.preparation.outcome
            Assert-Contains "$reportState entry failure recorded" $summary.Json.runnerError 'fixture entry failure'
            Assert-Contains "$reportState markdown entry failure" $summary.Report '入口自身失败'
            Assert-True "$reportState markdown keeps preparation" (-not $summary.Report.Contains('准备阶段失败'))
        }
        $check = Find-Check (Get-Checks $summary) 'BindingFixture.BindingTests.Runs_built_sources'
        Assert-Equal "$reportState original check retained" $(if ($reportState -eq 'before-report') { 'failed' } else { 'passed' }) $check.outcome
        Assert-True "$reportState cancellation requested" (Test-Path -LiteralPath (Join-Path $summary.Directory 'cancel.request'))
        Assert-True "$reportState isolated root removed" (-not (Test-Path -LiteralPath (Read-IsolatedRoot $summary)))
    }
}

It "real-godot-pending-selection" {
    $all = Invoke-Runner -ArgumentList @("-Module", "godot", "-Correctness", "-List") -WaitMs 300000
    Assert-Exit $all 0
    Assert-Contains "pending composition listed" $all.Stdout "CASE id=godot.isolated-composition "
    $unit = Invoke-Runner -ArgumentList @("-Module", "godot", "-Correctness", "-Family", "unit", "-List") -WaitMs 300000
    Assert-Exit $unit 0
    Assert-True "headless pending excluded by unit" ($unit.Stdout -notlike "*CASE id=godot.isolated-composition *")
    $only = Invoke-Runner -ArgumentList @("-Module", "godot", "-Correctness", "-Test", "godot.isolated-composition", "-ResultsRoot", (New-Results "godot-pending")) -WaitMs 300000
    Assert-Exit $only 2
    $checks = @(Get-Checks (Read-Summary $only.Stdout))
    Assert-Equal "only selected pending" 1 $checks.Count
    Assert-Equal "pending blocked" "blocked" $checks[0].outcome
    Assert-Equal "pending reason" "not-implemented" $checks[0].reason
}

try {
    Note "done failed=$script:failed blocked=$script:blocked"
}
finally {
    foreach ($owned in @($script:owned)) { Stop-Tree $owned }
    if ($script:sentinel) { Stop-Tree $script:sentinel.Id }
}

if ($script:failed -gt 0 -or $script:blocked -gt 0) { exit 1 }
exit 0
