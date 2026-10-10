param([string]$OutputDirectory = 'TestResults/run-tests/contract-probe-292/scenes')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
New-Item -ItemType Directory -Force $output | Out-Null
$isolated = Join-Path $output ('isolated-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $isolated | Out-Null
# 只复制契约声明、项目文件、既有消费样例和本原型；不复制生产实现或任何构建产物。
$inputs = @('global.json', 'prototypes/scenes-contract/ConsumerExamples.cs', 'archive/prototypes/contract-probe/Scenes/ScenesProbe.csproj', 'archive/prototypes/contract-probe/Scenes/Program.cs')
foreach ($contract in @('Core','Content','Scenes')) {
    $inputs += Get-ChildItem -LiteralPath (Join-Path $repo "Contracts/$contract") -File | Where-Object { $_.Extension -in @('.cs','.csproj') } | ForEach-Object { [IO.Path]::GetRelativePath($repo,$_.FullName) }
}
$manifest = foreach ($input in $inputs) {
    $source = Join-Path $repo $input; $destination = Join-Path $isolated $input
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
    [pscustomobject]@{Path=$input;Source=$source;Destination=$destination;Sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash}
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'inputs.json')
$project = Join-Path $isolated 'archive/prototypes/contract-probe/Scenes/ScenesProbe.csproj'
$program = Join-Path $isolated 'archive/prototypes/contract-probe/Scenes/Program.cs'
$original = Get-Content -LiteralPath $program -Raw
$commands = [Collections.Generic.List[string]]::new()
Push-Location $isolated
try {
    $sdk = & dotnet --version; if ($LASTEXITCODE -ne 0) { throw 'SDK unavailable' }
    $sdk | Set-Content (Join-Path $output 'sdk.txt')
    $commands.Add("dotnet build $project --nologo")
    & dotnet build $project --nologo *> (Join-Path $output 'correct-build.log'); if ($LASTEXITCODE -ne 0) { throw 'correct build failed' }
    $commands.Add("dotnet run --project $project --no-build")
    & dotnet run --project $project --no-build *> (Join-Path $output 'correct-run.log'); if ($LASTEXITCODE -ne 0) { throw 'correct run failed' }
    $commands.Add("dotnet msbuild $project -target:ResolveReferences -getItem:ProjectReference,ReferencePath")
    & dotnet msbuild $project -target:ResolveReferences '-getItem:ProjectReference,ReferencePath' *> (Join-Path $output 'resolved-references.json'); if ($LASTEXITCODE -ne 0) { throw 'reference evaluation failed' }
    $resolved = Get-Content (Join-Path $output 'resolved-references.json') -Raw | ConvertFrom-Json
    foreach ($reference in $resolved.Items.ReferencePath | Where-Object { $_.Filename -like 'Train.*' }) {
        if ($reference.Filename -notin @('Train.Core.Contracts','Train.Content.Contracts','Train.Scenes.Contracts') -or -not $reference.FullPath.StartsWith($isolated + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "forbidden or outside isolation reference $($reference.FullPath)"
        }
    }
    # 在隔离副本直接破坏接入资格判断，运行原样断言；保存破坏源码及失败日志。
    $needle = 'if (broken || (!candidate.Cancelled && candidate.Binding == active.Binding))'
    if (-not $original.Contains($needle)) { throw 'stale mutation anchor missing' }
    $mutation = $original.Replace($needle, 'if (true) /* #292 deliberate stale publication mutation */')
    Set-Content -LiteralPath $program -Value $mutation
    Copy-Item -LiteralPath $program -Destination (Join-Path $output 'stale-mutated.cs.txt')
    $commands.Add("dotnet build $project --nologo [source mutated: allow stale result]")
    & dotnet build $project --nologo *> (Join-Path $output 'stale-build.log'); if ($LASTEXITCODE -ne 0) { throw 'stale mutation did not build' }
    $commands.Add("dotnet run --project $project --no-build [source mutated]")
    & dotnet run --project $project --no-build *> (Join-Path $output 'stale-run.log'); $mutationExit = $LASTEXITCODE
    if ($mutationExit -eq 0 -or -not (Get-Content (Join-Path $output 'stale-run.log') -Raw).Contains('ASSERT: async.coordinator.cancelled-A-late-return-cannot-publish-or-clear-B-and-releases-A')) { throw 'stale mutation was not caught by intended assertion' }
    Set-Content -LiteralPath $program -Value $original
    $commands.Add("dotnet build $project --nologo [source restored]")
    & dotnet build $project --nologo *> (Join-Path $output 'restored-build.log'); if ($LASTEXITCODE -ne 0) { throw 'restored build failed' }
    $commands.Add("dotnet run --project $project --no-build [source restored]")
    & dotnet run --project $project --no-build *> (Join-Path $output 'restored-run.log'); if ($LASTEXITCODE -ne 0) { throw 'restored run failed' }
    $allowed = @('ScenesProbe','Train.Core.Contracts','Train.Content.Contracts','Train.Scenes.Contracts')
    $bin = Join-Path (Split-Path $project) 'bin/Debug/net10.0'
    $assemblies = Get-ChildItem -LiteralPath $bin -Filter '*.dll' | ForEach-Object { if ($_.BaseName -notin $allowed) { throw "forbidden output assembly $($_.Name)" }; [pscustomobject]@{Name=$_.Name;Path=$_.FullName;Sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash} }
    $assemblies | ConvertTo-Json | Set-Content (Join-Path $output 'assemblies.json')
    $commands | Set-Content (Join-Path $output 'commands.txt')
    [pscustomobject]@{Status='Passed';Evidence='Finite contract substitutes and existing consumer samples; no real scene implementation or Godot host';Isolation=$isolated;CorrectExit=0;MutationExit=$mutationExit;RestoredExit=0;Assertions=@(Get-Content (Join-Path $output 'correct-run.log') | Where-Object { $_ -like 'PASS *' }).Count;Mutation='Cancelled A late return allowed to overwrite B; same assertion catches and restored run passes'} | ConvertTo-Json | Set-Content (Join-Path $output 'result.json')
    Write-Output "SCENES_PROBE_ISOLATED_OK $output"
} finally { Pop-Location }
