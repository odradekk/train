param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../../../../TestResults/run-tests/contract-probe-292/content'))
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $output | Out-Null
$snapshot = Join-Path ([IO.Path]::GetTempPath()) ('train-content-probe-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $snapshot | Out-Null
$inputs = @('global.json')
foreach ($folder in @('Contracts/Core', 'Contracts/Content', 'prototypes/content-contract', 'archive/prototypes/contract-probe/Content')) {
    $inputs += Get-ChildItem -LiteralPath (Join-Path $repo $folder) -File |
        Where-Object { $_.Extension -in '.cs', '.csproj' } |
        ForEach-Object { [IO.Path]::GetRelativePath($repo, $_.FullName) }
}
$manifest = foreach ($relative in $inputs) {
    $source = Join-Path $repo $relative
    $target = Join-Path $snapshot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    @{ path = $relative; sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'inputs.json')
$snapshot | Set-Content -LiteralPath (Join-Path $output 'snapshot.txt')
$project = 'archive/prototypes/contract-probe/Content/Train.Content.ContractProbe.csproj'
Push-Location $snapshot
try {
    & dotnet --version | Set-Content -LiteralPath (Join-Path $output 'sdk.txt')
    & dotnet build $project --nologo *> (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw "内容原型构建失败，见 $output/build.log" }
    & dotnet msbuild $project -nologo -getItem:ProjectReference,ReferencePath -target:ResolveReferences *> (Join-Path $output 'references.json')
    if ($LASTEXITCODE -ne 0) { throw '引用求值失败' }
    $references = Get-Content -Raw (Join-Path $output 'references.json') | ConvertFrom-Json
    $allowed = @('Train.Core.Contracts', 'Train.Content.Contracts', 'Train.Content.ContractSamples')
    foreach ($reference in $references.Items.ReferencePath) {
        if ($reference.Filename.StartsWith('Train.')) {
            if ($reference.Filename -notin $allowed -or -not $reference.FullPath.StartsWith($snapshot, [StringComparison]::OrdinalIgnoreCase)) {
                throw "第一方程序集越过隔离：$($reference.FullPath)"
            }
        }
    }
    & dotnet run --project $project --no-build -- (Join-Path $output 'results.json') (Join-Path $output 'io') *> (Join-Path $output 'run.log')
    if ($LASTEXITCODE -ne 0) { throw "内容原型运行失败，见 $output/run.log" }
} finally { Pop-Location }
Write-Output "内容消费与真实文件原型通过；证据：$output"
