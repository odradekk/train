# Black-box checks for scripts/run-tests.ps1.
# Literal expectations are the oracle. Fake hosts never replace the real MSTest cases.

param([string] $Only = "")

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -lt 7) { throw "PowerShell 7 is required" }

$root = Split-Path -Parent $PSScriptRoot
$runner = Join-Path $PSScriptRoot "run-tests.ps1"
$pwsh = (Get-Process -Id $PID).Path
$utf8 = [System.Text.UTF8Encoding]::new($false)
$runStamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + [guid]::NewGuid().ToString("N").Substring(0, 8)
$evidence = Join-Path $root "TestResults/issue-158/harness/$runStamp"
$fixtureRoot = Join-Path $evidence "fixture"
$project = Join-Path $fixtureRoot "Fixture.csproj"
$bareProject = Join-Path $evidence "bare/Bare.csproj"
$brokenProject = Join-Path $evidence "broken/Broken.csproj"
$hostPath = Join-Path $evidence "fake-host.ps1"
$logPath = Join-Path $evidence "harness.log"
$script:failed = 0
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
    if ($Only -and $Name -notlike "*$Only*") { return }
    Note "RUN $Name"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Body
        $watch.Stop()
        Note "ok $Name $($watch.ElapsedMilliseconds)ms"
    }
    catch {
        $watch.Stop()
        Note "FAIL $Name $($watch.ElapsedMilliseconds)ms : $($_.Exception.Message)"
        $script:failed++
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
        [int] $WaitMs = 180000
    )
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $pwsh
    $psi.WorkingDirectory = $root
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding = $utf8
    $psi.CreateNoWindow = $true
    $arguments = [System.Collections.Generic.List[string]]::new()
    [void] $arguments.Add("-NoProfile")
    [void] $arguments.Add("-File")
    [void] $arguments.Add($runner)
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
        [int] $WaitMs = 180000
    )
    $handle = Open-Runner -ArgumentList $ArgumentList -Environment $Environment -Unset $Unset -WaitMs $WaitMs
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

function Get-CaseLines([string] $Stdout) {
    $list = [System.Collections.Generic.List[object]]::new()
    foreach ($match in [regex]::Matches($Stdout, "(?m)^CASE id=(\S+) family=(\S+) scopes=(\S+) requirements=(\S+) group=(\S+)\s*$")) {
        $list.Add([pscustomobject]@{
            Id = $match.Groups[1].Value
            Family = $match.Groups[2].Value
            Scopes = $match.Groups[3].Value
            Requirements = $match.Groups[4].Value
            Group = $match.Groups[5].Value
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

function Get-GitFacts {
    $commit = (& git -C $root rev-parse HEAD | Out-String).Trim()
    $dirty = $false
    foreach ($line in @(git -C $root status --porcelain)) {
        if (-not [string]::IsNullOrWhiteSpace([string] $line)) { $dirty = $true }
    }
    return [pscustomobject]@{ Commit = $commit; Dirty = $dirty }
}

function Assert-CommonSummary($Summary) {
    Assert-True "includesAllRepositoryChecks false" ($Summary.Raw -match '"includesAllRepositoryChecks":\s*false')
    Assert-True "registered mstest" ($Summary.Raw -match '"mstest"')
    Assert-Contains "source link" $Summary.Json.sourceLinkBoundary "does not prove the deployed Godot assembly"
    Assert-Contains "report source" $Summary.Report "does not prove the deployed Godot assembly"
    Assert-Contains "manual section" $Summary.Report "人工观察"
    $gaps = As-List $Summary.Json.migrationGaps
    foreach ($gap in @("godot", "cross-process", "legacy-runner-inventory", "ci", "benchmark")) {
        $found = $false
        foreach ($item in $gaps) { if ([string] $item -eq $gap) { $found = $true } }
        Assert-True "gap $gap" $found
    }
    $manual = As-List $Summary.Json.manual
    Assert-Equal "manual empty" 0 $manual.Count
    $git = Get-GitFacts
    Assert-Equal "commit" $git.Commit $Summary.Json.commit
    Assert-Equal "dirty" $git.Dirty ([bool] $Summary.Json.dirty)
    Assert-Equal "clean proven" (-not $git.Dirty) ([bool] $Summary.Json.cleanCommitProven)
    Assert-Equal "prep count" 1 ([int] $Summary.Json.preparation.commandCount)
}

$expected = @{
    WorldEditEncodingTests = "unit/world-edit"
    VoxelCubeUvTests = "unit/voxels"
    SplitMix64Tests = "unit/composites"
    FragmentGridSnapTests = "unit/composites"
    SettlementsJsonParserTests = "unit/settlements"
    SettlementTests = "unit/settlements"
    SettlementRegistryTests = "unit/settlements"
    ModManifestTests = "unit/content"
    ModDiscoveryTests = "unit/content"
    ContentPackageWalkTests = "unit/content"
    ExternalPathTests = "unit/content"
    IsolatedBuiltInContentTests = "unit/content"
    HumanLogFormatTests = "unit/logging"
    RingBufferLogSinkTests = "unit/logging"
    StructuredLoggingTests = "unit/logging"
    StructuredLoggingConcurrencyTests = "unit/logging"
    ConsoleCommandParseTests = "unit/console"
    IdentityTests = "unit/identity"
    WorldSaveTests = "headless/persistence"
    WorldEditPersistenceTests = "headless/persistence"
    TrainPersistenceTests = "headless/persistence"
    CompositeVehiclePersistenceTests = "headless/persistence"
    CompositeReloadSaveRejectionTests = "headless/persistence"
    WorldEditOrderingTests = "headless/world-edit"
    WorldEditEntryTests = "headless/world-edit"
    VoxelWorldTests = "headless/voxels"
    VoxelMultiChunkTests = "headless/voxels"
    VoxelLocalBoxTests = "headless/voxels"
    VoxelLiveReloadTests = "headless/voxels"
    VoxelConsoleCommandTests = "headless/voxels"
    VoxelCollisionTests = "headless/voxels"
    VehicleEnvelopeTests = "headless/composites"
    CompositeVehicleReloadTests = "headless/composites"
    CompositeRefitTests = "headless/composites"
    CompositeManufactureTests = "headless/composites"
    CompositeDamageTests = "headless/composites"
    CompositeContentTests = "headless/composites"
    CompositeConsoleCommandTests = "headless/composites"
    TrainTrackSafetyTests = "headless/railway"
    TrainMotionTests = "headless/railway"
    TrainDisplayPresentationTests = "headless/railway"
    TrainCouplerCoastTests = "headless/railway"
    StaticTrainAssemblyTests = "headless/railway"
    RailCommandTests = "headless/railway"
    PhysicalRailTests = "headless/railway"
    PlaygroundTests = "headless/playground"
    PlaygroundSaveSwitchTests = "headless/playground"
    PlaygroundObservationTests = "headless/playground"
    ObserveConsoleCommandTests = "headless/playground"
    ModLoadingTests = "headless/content"
    ContentReloaderTests = "headless/content"
    ConsoleCommandDispatchTests = "headless/console"
    AuthorityCheckpointTests = "headless/authority"
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

It "fast-invalid" {
    $result = Invoke-Runner -ArgumentList @("-Nope") -WaitMs 30000
    Assert-Exit $result 4
    Assert-Contains "token" $result.Stderr "error: invalid-argument"
    Assert-Contains "source" $result.Stdout "source-link:"
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
        "-Project", $project, "-Scope", "content", "-Scope", "persistence", "-ResultsRoot", $results
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
        "-Project", $project, "-List", "-Scope", "voxels", "-Scope", "content", "-ResultsRoot", $results
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

It "fixture-build-blocked" {
    $results = New-Results "fixture-broken"
    $result = Invoke-Runner -ArgumentList @("-Project", $brokenProject, "-Family", "unit", "-ResultsRoot", $results) -WaitMs 300000
    Assert-Exit $result 2
    Assert-Contains "build" $result.Stderr "error: blocked build"
    $summary = Read-Summary $result.Stdout
    Assert-Equal "prep blocked" "blocked" $summary.Json.preparation.outcome
    Assert-Equal "prep reason" "build" $summary.Json.preparation.reason
    Assert-Equal "no checks" 0 (Get-Checks $summary).Count
    Assert-True "not a pass" ($summary.Raw -match '"exitCode":\s*2')
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
    Assert-Equal "later passed" "passed" $later.outcome
    Assert-True "sentinel" (Test-Alive $sentinelId)
    foreach ($owned in (Read-PidFile $pidFile)) { Assert-True "killed $owned" (-not (Test-Alive $owned)) }
    Note "EVIDENCE fixture-timeout $($result.DurationMs)ms"
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

It "fixture-ctrl-c" {
    Ensure-Sentinel
    $sentinelId = $script:sentinel.Id
    $python = (Get-Command python -ErrorAction SilentlyContinue).Source
    if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
    Assert-True "python for ctrl-c probe" ([bool] $python)
    $probeDir = Join-Path $evidence "ctrl-c-probe"
    $outDir = Join-Path $probeDir "out"
    New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
    $enable = @'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ConsoleControlProbe {
 [DllImport("kernel32.dll",SetLastError=true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
"@
[Console]::Out.WriteLine("ENABLE_CTRL_C="+[ConsoleControlProbe]::SetConsoleCtrlHandler([IntPtr]::Zero,$false))
& '__RUNNER__' @args
exit $LASTEXITCODE
'@
    $enablePath = Join-Path $probeDir "enable-control-c.ps1"
    Set-Content -LiteralPath $enablePath -Value $enable.Replace("__RUNNER__", $runner) -Encoding utf8
    $probe = @'
import os,sys,time,subprocess,pathlib,json
out_dir=pathlib.Path(sys.argv[1]); out_dir.mkdir(parents=True, exist_ok=False)
enable,project,host,root=sys.argv[2],sys.argv[3],sys.argv[4],sys.argv[5]
si=subprocess.STARTUPINFO(); si.dwFlags|=subprocess.STARTF_USESHOWWINDOW; si.wShowWindow=0
env=os.environ.copy(); env["TRAIN_HARNESS_MODE"]="sleep"; env["TRAIN_HARNESS_PID_FILE"]=str(out_dir/"pids.txt")
args=["pwsh","-NoProfile","-File",enable,"-Project",project,"-Scope","hold","-Scope","later","-TestHostPath",host,"-TimeoutSeconds","60","-ResultsRoot",str(out_dir/"runs")]
out=(out_dir/"stdout.log").open("w",encoding="utf-8"); err=(out_dir/"stderr.log").open("w",encoding="utf-8")
p=subprocess.Popen(args,stdout=out,stderr=err,env=env,cwd=root,startupinfo=si,creationflags=subprocess.CREATE_NEW_CONSOLE)
owned=[]; record={"runnerPid":p.pid}
try:
 deadline=time.monotonic()+40
 while not (out_dir/"pids.txt").exists() and p.poll() is None and time.monotonic()<deadline: time.sleep(0.1)
 if not (out_dir/"pids.txt").exists(): raise RuntimeError("host readiness missing")
 owned=[int(v) for v in (out_dir/"pids.txt").read_text().strip().split(",") if v]; record["hostChildPids"]=owned
 injector=out_dir/"inject.py"
 injector.write_text("import ctypes,time,sys\nk=ctypes.WinDLL('kernel32',use_last_error=True)\nk.FreeConsole()\nassert k.AttachConsole(int(sys.argv[1])),ctypes.get_last_error()\nassert k.SetConsoleCtrlHandler(None,True)\nassert k.GenerateConsoleCtrlEvent(0,0),ctypes.get_last_error()\ntime.sleep(0.5)\nk.FreeConsole()\n",encoding="utf-8")
 injection=subprocess.run([sys.executable,str(injector),str(p.pid)],capture_output=True,text=True,timeout=5)
 record["injectExit"]=injection.returncode; record["injectError"]=injection.stderr
 try: record["exit"]=p.wait(timeout=20)
 except subprocess.TimeoutExpired: record["exit"]="still running after 20s"
 record["summaries"]=[str(x) for x in out_dir.rglob("summary.json")]
finally:
 import ctypes
 k=ctypes.WinDLL("kernel32",use_last_error=True); alive=[]
 for pid in owned:
  h=k.OpenProcess(0x1000,False,pid)
  if h:
   code=ctypes.c_ulong(); k.GetExitCodeProcess(h,ctypes.byref(code)); k.CloseHandle(h)
   if code.value==259: alive.append(pid)
 record["ownedAliveAfterSignal"]=alive
 for pid in ([p.pid] if p.poll() is None else [])+alive:
  subprocess.run(["taskkill","/PID",str(pid),"/T","/F"],capture_output=True)
 out.close(); err.close()
 (out_dir/"result.json").write_text(json.dumps(record,indent=2),encoding="utf-8")
'@
    $probePath = Join-Path $probeDir "ctrl-c-enabled-probe.py"
    Set-Content -LiteralPath $probePath -Value $probe -Encoding utf8
    $probeRun = & $python $probePath $outDir $enablePath $project $hostPath $root
    if ($LASTEXITCODE -ne 0) { throw "ctrl-c probe exit $LASTEXITCODE $probeRun" }
    $record = Get-Content -Raw -LiteralPath (Join-Path $outDir "result.json") | ConvertFrom-Json
    Assert-Equal "ctrl-c inject" 0 ([int] $record.injectExit)
    Assert-Equal "ctrl-c exit" 3 ([int] $record.exit)
    Assert-True "ctrl-c summary" (@($record.summaries).Count -ge 1)
    Assert-Equal "ctrl-c children" 0 @($record.ownedAliveAfterSignal).Count
    $stderr = [System.IO.File]::ReadAllText((Join-Path $outDir "stderr.log"))
    Assert-True "no runspace crash" ($stderr -notlike "*PSInvalidOperationException*")
    $summaryPath = [string] @($record.summaries)[0]
    $summary = Get-Content -Raw -LiteralPath $summaryPath | ConvertFrom-Json
    Assert-Equal "ctrl-c summary exit" 3 ([int] $summary.exitCode)
    $hold = $null
    foreach ($check in @($summary.checks)) { if ([string] $check.id -eq "Fixture.HoldTests.Holds_the_runner") { $hold = $check } }
    Assert-Equal "ctrl-c hold" "cancelled" ([string] $hold.outcome)
    $later = $null
    foreach ($check in @($summary.checks)) { if ([string] $check.id -eq "Fixture.LaterTests.Finishes") { $later = $check } }
    Assert-Equal "ctrl-c later" "not-executed" ([string] $later.outcome)
    Assert-True "ctrl-c sentinel" (Test-Alive $sentinelId)
    Note "EVIDENCE fixture-ctrl-c $outDir"
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
        "source-link:", "not all repository validation", "gap: godot", "pending: benchmark", "PENDING benchmark"
    )) {
        Assert-Contains $token $result.Stdout $token
    }
    Assert-True "no full pass claim" ($result.Stdout -notlike "*all repository checks passed*")
    Note "EVIDENCE real-help $($result.DurationMs)ms"
}

It "real-list" {
    $result = Invoke-Runner -ArgumentList @("-List") -WaitMs 600000
    Assert-Exit $result 0
    Assert-Contains "coverage" $result.Stdout "not all repository validation"
    $cases = Get-CaseLines $result.Stdout
    Assert-True "discovered" ($cases.Count -gt 0)
    $seen = @{}
    foreach ($case in $cases) {
        $parts = $case.Id.Split(".")
        Assert-True "id shape $($case.Id)" ($parts.Length -ge 3)
        $class = $parts[$parts.Length - 2]
        Assert-True "known class $class" $expected.ContainsKey($class)
        $pair = $expected[$class].Split("/")
        Assert-Equal "$class family" $pair[0] $case.Family
        Assert-Equal "$class scope" $pair[1] $case.Scopes
        Assert-Equal "$class group" $pair[1] $case.Group
        Assert-Equal "$class requirements" "none" $case.Requirements
        $seen[$class] = $true
        Assert-True "family $($case.Family)" ($case.Family -eq "unit" -or $case.Family -eq "headless")
    }
    foreach ($class in $expected.Keys) { Assert-True "missing class $class" $seen.ContainsKey($class) }
    $selected = [regex]::Match($result.Stdout, "SELECTED count=(\d+)")
    Assert-Equal "selected count" $cases.Count ([int] $selected.Groups[1].Value)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    $psi.WorkingDirectory = $root
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding = $utf8
    foreach ($argument in @("test", "Train.Tests/Train.Tests.csproj", "-c", "Debug", "--no-build", "--no-restore", "--nologo", "--list-tests")) {
        [void] $psi.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    [void] $process.Start()
    $listed = $process.StandardOutput.ReadToEndAsync()
    $listErr = $process.StandardError.ReadToEndAsync()
    Assert-True "list-tests finished" $process.WaitForExit(180000)
    $listText = [string] $listed.Result
    $listError = [string] $listErr.Result
    Assert-Equal "list-tests exit" 0 $process.ExitCode
    $nativeIds = [System.Collections.Generic.List[string]]::new()
    $nativeNames = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($listText -split "\r?\n")) {
        $trimmed = $line.Trim()
        if ($trimmed -match "^Train\.Tests\.\S+$") { $nativeIds.Add($trimmed) }
        elseif ($trimmed -match "^[A-Za-z_][A-Za-z0-9_]+$") { $nativeNames.Add($trimmed) }
    }
    if ($nativeIds.Count -gt 0) {
        $ours = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($case in $cases) { [void] $ours.Add($case.Id) }
        Assert-Equal "list size" $nativeIds.Count $ours.Count
        foreach ($id in $nativeIds) { Assert-True "missing from runner $id" $ours.Contains($id) }
    }
    elseif ($nativeNames.Count -gt 0) {
        $ours = [System.Collections.Generic.List[string]]::new()
        foreach ($case in $cases) {
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
    $result = Invoke-Runner -ArgumentList @("-Family", "unit", "-Scope", "voxels", "-ResultsRoot", $results) -WaitMs 600000
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    Assert-CommonSummary $summary
    $checks = Get-Checks $summary
    Assert-True "voxel cases" ($checks.Count -ge 1)
    foreach ($check in $checks) {
        Assert-True "only cube $($check.id)" ([string]$check.id).StartsWith("Train.Tests.VoxelCubeUvTests.")
        Assert-Equal "passed $($check.id)" "passed" $check.outcome
        Assert-True "duration $($check.id)" ([int]$check.durationMs -ge 0)
    }
    $trx = Get-ChildItem -LiteralPath $summary.Directory -Recurse -Filter *.trx
    Assert-True "voxel trx" ($trx.Count -gt 0)
    $trxText = [System.IO.File]::ReadAllText($trx[0].FullName)
    Assert-Contains "voxel method" $trxText "VoxelCubeUvTests"
    Note "EVIDENCE real-voxels count=$($checks.Count) $($summary.Directory) $($result.DurationMs)ms"
}

It "real-headless" {
    $results = New-Results "real-headless"
    $id = "Train.Tests.ConsoleCommandDispatchTests.Blank_input_is_ignored"
    $result = Invoke-Runner -ArgumentList @("-Test", $id, "-ResultsRoot", $results) -WaitMs 600000
    Assert-Exit $result 0
    $summary = Read-Summary $result.Stdout
    $checks = Get-Checks $summary
    Assert-Equal "one headless" 1 $checks.Count
    Assert-Equal "id" $id $checks[0].id
    Assert-Equal "passed" "passed" $checks[0].outcome
    Assert-Equal "family" "headless" $checks[0].family
    Note "EVIDENCE real-headless $($summary.Directory) $($result.DurationMs)ms"
}

It "real-zero-unknown" {
    $zero = Invoke-Runner -ArgumentList @("-Family", "unit", "-Scope", "railway") -WaitMs 600000
    Assert-Exit $zero 4
    Assert-Contains "zero" $zero.Stderr "error: zero-match"
    $unknown = Invoke-Runner -ArgumentList @("-Scope", "buildings") -WaitMs 600000
    Assert-Exit $unknown 4
    Assert-Contains "buildings" $unknown.Stderr "error: unknown-name scope buildings"
}

It "real-correctness-list" {
    $all = Invoke-Runner -ArgumentList @("-List") -WaitMs 600000
    $correctness = Invoke-Runner -ArgumentList @("-Correctness", "-List") -WaitMs 600000
    Assert-Exit $all 0
    Assert-Exit $correctness 0
    $allCount = [regex]::Match($all.Stdout, "SELECTED count=(\d+)").Groups[1].Value
    $correctCount = [regex]::Match($correctness.Stdout, "SELECTED count=(\d+)").Groups[1].Value
    Assert-Equal "correctness lists every discovered case" $allCount $correctCount
    Assert-Contains "boundary" $correctness.Stdout "not all repository validation"
    Note "EVIDENCE real-correctness-list count=$correctCount $($correctness.DurationMs)ms"
}

try {
    Note "done failed=$script:failed"
}
finally {
    foreach ($owned in @($script:owned)) { Stop-Tree $owned }
    if ($script:sentinel) { Stop-Tree $script:sentinel.Id }
}

if ($script:failed -gt 0) { exit 1 }
exit 0
