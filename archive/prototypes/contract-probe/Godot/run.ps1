param([string]$OutputDirectory = 'TestResults/run-tests/contract-probe-292/godot')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
if (Test-Path $output) { throw "Evidence directory already exists: $output; choose a fresh OutputDirectory" }
New-Item -ItemType Directory -Path $output | Out-Null
$isolated = Join-Path $output 'isolated'
New-Item -ItemType Directory -Path $isolated | Out-Null
$inputs = @('global.json')
foreach ($folder in @('Contracts/Core','Contracts/Content','Contracts/Scenes')) {
    $inputs += Get-ChildItem (Join-Path $repo $folder) -File | Where-Object Extension -In '.cs','.csproj' | ForEach-Object { [IO.Path]::GetRelativePath($repo, $_.FullName) }
}
foreach ($folder in @('prototypes/godot-contract','prototypes/scenes-contract','archive/prototypes/contract-probe/Godot')) {
    $inputs += Get-ChildItem (Join-Path $repo $folder) -File | Where-Object Extension -In '.cs','.csproj','.godot','.tscn' | ForEach-Object { [IO.Path]::GetRelativePath($repo, $_.FullName) }
}
$manifest = foreach ($relative in $inputs) {
    if ($relative -match '(^|[\\/])(bin|obj|\.godot)([\\/]|$)') { throw "Generated input prohibited: $relative" }
    $source = Join-Path $repo $relative
    $target = Join-Path $isolated $relative
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    [PSCustomObject]@{ path = $relative; sha256 = (Get-FileHash $source -Algorithm SHA256).Hash }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output 'inputs.json')
$project = Join-Path $isolated 'archive/prototypes/contract-probe/Godot'
# A fixed triangle: three FLOAT VEC3 positions, little-endian GLB 2.0.
$positions = [byte[]]::new(36)
$floats = [single[]]@(0,0,0, 0.5,0,0, 0,0.5,0)
[Buffer]::BlockCopy($floats, 0, $positions, 0, 36)
$json = '{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"mesh":0}],"meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],"buffers":[{"byteLength":36}],"bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36}],"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[0.5,0.5,0]}]}'
while (($json.Length % 4) -ne 0) { $json += ' ' }
$jsonBytes = [Text.Encoding]::UTF8.GetBytes($json)
$stream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($stream)
$writer.Write([uint32]0x46546C67); $writer.Write([uint32]2); $writer.Write([uint32](28 + $jsonBytes.Length + $positions.Length))
$writer.Write([uint32]$jsonBytes.Length); $writer.Write([uint32]0x4E4F534A); $writer.Write($jsonBytes)
$writer.Write([uint32]$positions.Length); $writer.Write([uint32]0x004E4942); $writer.Write($positions)
[IO.File]::WriteAllBytes((Join-Path $project 'fixture.glb'), $stream.ToArray())
$writer.Dispose(); $stream.Dispose()
Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::new(2, 2)
$bitmap.SetPixel(0, 0, [Drawing.Color]::Red)
$bitmap.SetPixel(1, 0, [Drawing.Color]::Green)
$bitmap.SetPixel(0, 1, [Drawing.Color]::Blue)
$bitmap.SetPixel(1, 1, [Drawing.Color]::White)
$bitmap.Save((Join-Path $project 'fixture.png'), [Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
foreach ($asset in @('fixture.png','fixture.glb')) {
    (Get-FileHash (Join-Path $project $asset)).Hash | Set-Content (Join-Path $project "$asset.sha256")
    "[remap]`nimporter=`"keep`"`n`n[deps]`nsource_file=`"res://$asset`"" | Set-Content (Join-Path $project "$asset.import")
}
Copy-Item (Join-Path $repo 'export_presets.cfg') (Join-Path $project 'export_presets.cfg')
$preset = Get-Content (Join-Path $project 'export_presets.cfg') -Raw
$preset.Replace('*.voxels.json,*.composites.json,*.market-tables.json,*.settlement-types.json,*.png', '*.png,*.glb,*.sha256') | Set-Content (Join-Path $project 'export_presets.cfg')
$runtimeAppData = Join-Path $output 'appdata'
New-Item -ItemType Directory -Path $runtimeAppData | Out-Null
New-Item -ItemType Directory -Path (Join-Path $output 'export') | Out-Null
$engine = 'D:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe'
function Run([string]$name, [string]$exe, [string[]]$arguments) {
    "$exe $($arguments -join ' ')" | Add-Content (Join-Path $output 'commands.txt')
    & $exe @arguments 2>&1 | Tee-Object (Join-Path $output "$name.log")
    if ($LASTEXITCODE -ne 0) { throw "$name failed: $LASTEXITCODE" }
}
Push-Location $isolated
try {
    Run 'sdk' 'dotnet' @('--version')
    Run 'engine-version' $engine @('--headless','--version')
    Run 'solution' 'dotnet' @('new','sln','--format','sln','--name','Probe','--output',$project)
    Run 'solution-add' 'dotnet' @('sln',(Join-Path $project 'Probe.sln'),'add',(Join-Path $project 'Probe.csproj'))
    Run 'build' 'dotnet' @('build',(Join-Path $project 'Probe.csproj'),'-v:normal')
    & dotnet msbuild (Join-Path $project 'Probe.csproj') -nologo -getItem:ProjectReference,ReferencePath -target:ResolveReferences *> (Join-Path $output 'references.json')
    if ($LASTEXITCODE -ne 0) { throw 'Reference resolution failed' }
    $references = Get-Content (Join-Path $output 'references.json') -Raw | ConvertFrom-Json
    $allowedAssemblies = @('Train.Core.Contracts','Train.Content.Contracts','Train.Scenes.Contracts','Train.Godot.ContractSamples','Train.Scenes.ContractSamples')
    $isolatedPrefix = [IO.Path]::GetFullPath($isolated).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($reference in $references.Items.ReferencePath) {
        if ($reference.Filename.StartsWith('Train.')) {
            if ($reference.Filename -notin $allowedAssemblies -or !([IO.Path]::GetFullPath($reference.FullPath).StartsWith($isolatedPrefix, [StringComparison]::OrdinalIgnoreCase))) { throw "Forbidden first-party resolution: $($reference.FullPath)" }
        }
    }
    Run 'import' $engine @('--headless','--path',$project,'--editor','--import','--log-file',(Join-Path $output 'import-native.log'))
    $originalAppData = $env:APPDATA
    try { $env:APPDATA = $runtimeAppData; Run 'source-runtime' $engine @('--headless','--path',$project,'--log-file',(Join-Path $output 'source-native.log')) }
    finally { $env:APPDATA = $originalAppData }
    Run 'export' $engine @('--headless','--path',$project,'--editor','--export-release','Windows Desktop',(Join-Path $output 'export/probe.exe'),'--log-file',(Join-Path $output 'export-native.log'))
    if (Select-String -Path (Join-Path $output 'export.log') -Pattern '^ERROR:' -Quiet) { throw 'Export engine reported ERROR despite exit code 0' }
    try { $env:APPDATA = $runtimeAppData; Run 'export-runtime' (Join-Path $output 'export/probe.exe') @('--headless','--quit-after','60','--log-file',(Join-Path $output 'export-runtime-native.log')) }
    finally { $env:APPDATA = $originalAppData }
    foreach ($name in @('source-runtime','export-runtime')) {
        if (!(Select-String -Path (Join-Path $output "$name.log") -Pattern '^PROBE_SUCCESS$' -Quiet)) { throw "Missing assertions: $name" }
    }
    $exportPrefix = [IO.Path]::GetFullPath((Join-Path $output 'export')).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($line in Get-Content (Join-Path $output 'export-runtime.log')) {
        if ($line -match '^ASSEMBLY (Train\.[^,]+), .*PublicKeyToken=null (.+)$') {
            if ($Matches[1] -notin $allowedAssemblies -or !([IO.Path]::GetFullPath($Matches[2]).StartsWith($exportPrefix, [StringComparison]::OrdinalIgnoreCase))) { throw "Forbidden loaded first-party assembly: $line" }
        }
    }
} finally { Pop-Location }
