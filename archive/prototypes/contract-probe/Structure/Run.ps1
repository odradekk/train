param([string]$EvidenceDirectory = 'TestResults/run-tests/contract-probe-292/structure-core')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot/../../../..").Path
$evidence = [IO.Path]::GetFullPath((Join-Path $repo $EvidenceDirectory))
New-Item -ItemType Directory -Force $evidence | Out-Null
Set-Content "$evidence/outcomes.jsonl" ''
Set-Content "$evidence/commands.txt" ''
$temp = Join-Path ([IO.Path]::GetTempPath()) ('train-contract-probe-292-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $temp | Out-Null
# Explicit source inputs only; no implementation directories or prior build products.
$inputs = @('global.json',
 'Contracts/Core/Commands.cs','Contracts/Core/ContentInputs.cs','Contracts/Core/Events.cs','Contracts/Core/Lifecycle.cs','Contracts/Core/Queries.cs','Contracts/Core/Values.cs','Contracts/Core/Train.Core.Contracts.csproj',
 'Contracts/Content/Appearance.cs','Contracts/Content/Authoring.cs','Contracts/Content/Designs.cs','Contracts/Content/Lifecycle.cs','Contracts/Content/Sources.cs','Contracts/Content/Values.cs','Contracts/Content/WorldContent.cs','Contracts/Content/Train.Content.Contracts.csproj',
 'Contracts/Scenes/Authoring.cs','Contracts/Scenes/Editing.cs','Contracts/Scenes/Rendering.cs','Contracts/Scenes/Values.cs','Contracts/Scenes/World.cs','Contracts/Scenes/Train.Scenes.Contracts.csproj',
 'prototypes/core-contract/ConsumerExamples.cs','prototypes/core-contract/Train.Core.ContractSamples.csproj',
 'archive/prototypes/contract-probe/Core/Core.csproj','archive/prototypes/contract-probe/Core/Program.cs',
 'archive/prototypes/contract-probe/Structure/Structure.csproj','archive/prototypes/contract-probe/Structure/Program.cs')
$manifest = foreach ($input in $inputs) {
 $source = Join-Path $repo $input
 $destination = Join-Path $temp $input
 New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
 Copy-Item -LiteralPath $source -Destination $destination
 [pscustomobject]@{input=$input;sha256=(Get-FileHash $source -Algorithm SHA256).Hash;copiedTo=$destination}
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content "$evidence/inputs.json"
Set-Content "$evidence/temp-path.txt" $temp
function Invoke-Check([string]$name, [string[]]$arguments, [int]$expected=0) {
 $command = 'dotnet ' + ($arguments -join ' ')
 Add-Content "$evidence/commands.txt" $command
 $watch = [Diagnostics.Stopwatch]::StartNew()
 $output = & dotnet @arguments 2>&1
 $exit = $LASTEXITCODE
 $watch.Stop()
 $output | Set-Content "$evidence/$name.log"
 Add-Content "$evidence/outcomes.jsonl" ([pscustomobject]@{name=$name;expected=$expected;actual=$exit;command=$command;elapsedMilliseconds=$watch.ElapsedMilliseconds} | ConvertTo-Json -Compress)
 if ($exit -ne $expected) { throw "$name exit $exit; expected $expected; see evidence log" }
 $expectedDiagnostic = switch ($name) {
   'core-broken' { 'CONTRACT ASSERTION: retained nested collection unchanged' }
   'dependency-broken' { 'Forbidden assembly dependency: Train.Core.Contracts -> Train.ForbiddenImplementation' }
   'surface-broken' { 'Public surface changed:' }
   'consumer-broken' { 'CS7036' }
 }
 if ($expectedDiagnostic -and -not (($output -join "`n").Contains($expectedDiagnostic))) {
   throw "$name failed for an unrelated reason; missing expected diagnostic $expectedDiagnostic"
 }
}
function Assert-Resolved([string]$name, [string[]]$allowedProjects) {
 $resolved = Get-Content -Raw "$evidence/$name.log" | ConvertFrom-Json
 foreach ($item in $resolved.Items.ProjectReference) {
   if ($item.Filename -notin $allowedProjects -or -not $item.FullPath.StartsWith($temp + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
     throw "Forbidden evaluated project reference: $($item.FullPath)"
   }
 }
 foreach ($item in $resolved.Items.ReferencePath) {
   if ($item.FrameworkReferenceName -eq 'Microsoft.NETCore.App') { continue }
   if (-not $item.FullPath.StartsWith($temp + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
     throw "Assembly resolved outside isolation: $($item.FullPath)"
   }
   if ($item.Filename -notin $allowedProjects) { throw "Forbidden resolved assembly: $($item.FullPath)" }
 }
 Add-Content "$evidence/validated-paths.txt" "$name evaluated project and assembly references checked against allowed set: $($allowedProjects -join ', ')"
}
Push-Location $temp
try {
 if ((& dotnet --version) -ne '10.0.112') { throw 'SDK 10.0.112 required' }
 Invoke-Check 'build-core' @('build','archive/prototypes/contract-probe/Core/Core.csproj','-v:minimal')
 Invoke-Check 'core-correct' @('run','--no-build','--project','archive/prototypes/contract-probe/Core/Core.csproj')
 Invoke-Check 'core-broken' @('run','--no-build','--project','archive/prototypes/contract-probe/Core/Core.csproj','--','--mutable-snapshot') 1
 Invoke-Check 'core-restored' @('run','--no-build','--project','archive/prototypes/contract-probe/Core/Core.csproj')
 Invoke-Check 'build-contracts' @('build','Contracts/Scenes/Train.Scenes.Contracts.csproj','-v:minimal')
 Invoke-Check 'build-inspector' @('build','archive/prototypes/contract-probe/Structure/Structure.csproj','-v:minimal')
 $dlls = Join-Path $temp 'Contracts/Scenes/bin/Debug/net10.0'
 $inspector = @('run','--no-build','--project','archive/prototypes/contract-probe/Structure/Structure.csproj','--')
 $baseline = Join-Path $evidence 'observed-public-surface.txt'
 Invoke-Check 'structure-correct' ($inspector + @('observe',$dlls,$baseline))
 foreach ($module in @('Core','Content','Scenes')) {
   Invoke-Check "msbuild-$module" @('msbuild',"Contracts/$module/Train.$module.Contracts.csproj",'-target:ResolveReferences','-getItem:ProjectReference,ReferencePath','-getProperty:TargetPath,MSBuildSDKsPath')
   $allowed = switch ($module) { 'Core' { @() } 'Content' { @('Train.Core.Contracts') } 'Scenes' { @('Train.Core.Contracts','Train.Content.Contracts') } }
   Assert-Resolved "msbuild-$module" $allowed
 }
 Invoke-Check 'msbuild-consumer' @('msbuild','archive/prototypes/contract-probe/Core/Core.csproj','-target:ResolveReferences','-getItem:ProjectReference,ReferencePath','-getProperty:TargetPath')
 Assert-Resolved 'msbuild-consumer' @('Train.Core.Contracts','Train.Core.ContractSamples')
 # A real compiled forbidden assembly, supplied by an explicit binary reference.
 New-Item -ItemType Directory forbidden | Out-Null
 Set-Content forbidden/Forbidden.csproj '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Train.ForbiddenImplementation</AssemblyName></PropertyGroup></Project>'
 Set-Content forbidden/Marker.cs 'namespace Forbidden; public static class Marker { public static int Value => 1; }'
 Invoke-Check 'build-forbidden' @('build','forbidden/Forbidden.csproj','-v:minimal')
 $project = 'Contracts/Core/Train.Core.Contracts.csproj'
 $originalProject = Get-Content -Raw $project
 $forbiddenDll = Join-Path $temp 'forbidden/bin/Debug/net10.0/Train.ForbiddenImplementation.dll'
 Set-Content $project ($originalProject.Replace('</Project>',"<ItemGroup><Reference Include=`"Train.ForbiddenImplementation`"><HintPath>$forbiddenDll</HintPath></Reference></ItemGroup></Project>"))
 Set-Content Contracts/Core/DeliberateViolation.cs 'namespace Train.Core.Contracts; internal static class DeliberateViolation { internal static int Read() => Forbidden.Marker.Value; }'
 Invoke-Check 'dependency-broken-build' @('build','Contracts/Scenes/Train.Scenes.Contracts.csproj','-v:minimal')
 Invoke-Check 'dependency-broken-msbuild' @('msbuild',$project,'-target:ResolveReferences','-getItem:ProjectReference,ReferencePath')
 Invoke-Check 'dependency-broken' ($inspector + @('compare',$dlls,$baseline)) 1
 Set-Content $project $originalProject
 Remove-Item -LiteralPath Contracts/Core/DeliberateViolation.cs
 Invoke-Check 'dependency-restored-build' @('build','Contracts/Scenes/Train.Scenes.Contracts.csproj','-v:minimal')
 Invoke-Check 'dependency-restored' ($inspector + @('compare',$dlls,$baseline))
 $lifecycle = 'Contracts/Core/Lifecycle.cs'
 $originalLifecycle = Get-Content -Raw $lifecycle
 Set-Content $lifecycle ($originalLifecycle.Replace('ICoreReadView Read();','ICoreReadView Read(int required);'))
 Invoke-Check 'surface-broken-build' @('build','Contracts/Scenes/Train.Scenes.Contracts.csproj','-v:minimal')
 Invoke-Check 'surface-broken' ($inspector + @('compare',$dlls,$baseline)) 1
 Invoke-Check 'consumer-broken' @('build','archive/prototypes/contract-probe/Core/Core.csproj','-v:minimal') 1
 Set-Content $lifecycle $originalLifecycle
 Invoke-Check 'surface-restored-build' @('build','Contracts/Scenes/Train.Scenes.Contracts.csproj','-v:minimal')
 Invoke-Check 'surface-restored' ($inspector + @('compare',$dlls,$baseline))
 Invoke-Check 'consumer-restored' @('run','--project','archive/prototypes/contract-probe/Core/Core.csproj')
 Get-ChildItem -LiteralPath $dlls -Filter 'Train.*.Contracts.dll' | ForEach-Object {
   [pscustomobject]@{path=$_.FullName;identity=[Reflection.AssemblyName]::GetAssemblyName($_.FullName).FullName;sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
 } | ConvertTo-Json | Set-Content "$evidence/restored-assemblies.json"
 Write-Output "PASS evidence=$evidence; isolation=$temp"
} finally { Pop-Location }
