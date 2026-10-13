param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][string] $GameExecutablePath,
    [Parameter(Mandatory)][string] $TemplateAppData,
    [Parameter(Mandatory)][ValidateRange(1, 2147478)][int] $TimeoutSeconds
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-HiddenProcess.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $IsWindows) { throw 'Asset export probe requires Windows.' }
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory) -or -not [IO.Path]::IsPathFullyQualified($GameExecutablePath)) {
    throw 'Asset export output and game executable paths must be absolute.'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
$game = [IO.Path]::GetFullPath($GameExecutablePath)
$gamePck = [IO.Path]::ChangeExtension($game, '.pck')
if (-not (Test-Path -LiteralPath $game -PathType Leaf) -or -not (Test-Path -LiteralPath $gamePck -PathType Leaf)) {
    throw 'Actual Windows game executable and adjacent PCK are required.'
}
if (Test-Path -LiteralPath $output) { throw "Asset probe requires a fresh output directory: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
$environment = @{}
foreach ($name in @('APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP')) { $environment[$name] = [Environment]::GetEnvironmentVariable($name) }

function Invoke-ProbeProcess([string] $Name, [string] $Program, [string[]] $Arguments) {
    $remaining = [int][Math]::Floor($TimeoutSeconds - $watch.Elapsed.TotalSeconds)
    if ($remaining -lt 1) { throw 'Asset export exhausted its time budget.' }
    $seconds = $remaining
    $result = Invoke-HiddenProcess -FilePath $Program -ArgumentList $Arguments -TimeoutSeconds $seconds
    $result.Output | Set-Content -LiteralPath (Join-Path $output "$Name.process.log")
    [ordered]@{ program = $Program; arguments = $Arguments; exit = $result.ExitCode; timedOut = $result.TimedOut;
        cleanupSucceeded = $result.CleanupSucceeded; timeoutSeconds = $seconds } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output "$Name.process.json")
    Write-Host $result.Output
    if ($result.ExitCode -ne 0 -or $result.TimedOut -or -not $result.CleanupSucceeded) {
        throw "Asset export $Name failed (exit=$($result.ExitCode), timeout=$($result.TimedOut))."
    }
}

$templateDirectory = $null
$ownsTemplateDirectory = $false

try {
    $staging = Join-Path $output 'staging'
    $project = Join-Path $staging 'Tests/AssetExport'
    $data = Join-Path $output 'probe-data'
    $buildInputs = Join-Path $data 'build-inputs'
    foreach ($path in @($project, $buildInputs)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    foreach ($name in @('APPDATA', 'LOCALAPPDATA', 'TEMP', 'TMP')) {
        $isolated = Join-Path $output $name.ToLowerInvariant()
        New-Item -ItemType Directory -Path $isolated -Force | Out-Null
        [Environment]::SetEnvironmentVariable($name, $isolated)
    }
    $template = [IO.Path]::GetFullPath((Join-Path $TemplateAppData 'Godot/export_templates/4.7.2.stable.mono/windows_release_x86_64.exe'))
    if (-not (Test-Path -LiteralPath $template -PathType Leaf)) { throw "Godot export template not found: $template" }
    $templateDirectory = [IO.Path]::GetFullPath((Join-Path $env:APPDATA 'Godot/export_templates/4.7.2.stable.mono'))
    if (-not $templateDirectory.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Asset probe templates escaped its output directory.'
    }
    if (Test-Path -LiteralPath $templateDirectory) { throw "Asset probe templates already exist: $templateDirectory" }
    New-Item -ItemType Directory -Path $templateDirectory -Force | Out-Null
    $ownsTemplateDirectory = $true
    Copy-Item -LiteralPath $template -Destination $templateDirectory
    Copy-Item -LiteralPath (Join-Path $root 'global.json') -Destination $staging
    foreach ($file in @('project.godot', 'ProbeRunner.tscn', 'export_presets.cfg')) {
        Copy-Item -LiteralPath (Join-Path $root "Tests/AssetExport/$file") -Destination $project
    }
    New-Item -ItemType HardLink -Path (Join-Path $project 'ProbeRunner.cs') -Target (Join-Path $root 'Tests/AssetExport/ProbeRunner.cs') | Out-Null
    $projectFile = Join-Path $project 'Train.AssetExport.csproj'
    [xml]$definition = Get-Content -LiteralPath (Join-Path $root 'Tests/AssetExport/Train.AssetExport.csproj') -Raw
    foreach ($compile in $definition.Project.ItemGroup.Compile) {
        $compile.Include = [IO.Path]::GetFullPath((Join-Path (Join-Path $root 'Tests/AssetExport') $compile.Include))
    }
    $definition.Save($projectFile)
    foreach ($module in @('Core', 'Content')) {
        $contractDirectory = Join-Path $staging "Contracts/$module"
        New-Item -ItemType Directory -Path $contractDirectory -Force | Out-Null
        [xml]$contract = Get-Content -LiteralPath (Join-Path $root "Contracts/$module/Train.$module.Contracts.csproj") -Raw
        $defaultItems = $contract.CreateElement('EnableDefaultCompileItems')
        $defaultItems.InnerText = 'false'
        $contract.Project.PropertyGroup.AppendChild($defaultItems) | Out-Null
        $items = $contract.CreateElement('ItemGroup')
        $source = $contract.CreateElement('Compile')
        $source.SetAttribute('Include', (Join-Path $root "Contracts/$module/**/*.cs"))
        $source.SetAttribute('Exclude', ((Join-Path $root "Contracts/$module/obj/**") + ';' + (Join-Path $root "Contracts/$module/bin/**")))
        $source.SetAttribute('Link', '%(RecursiveDir)%(Filename)%(Extension)')
        $items.AppendChild($source) | Out-Null
        $contract.Project.AppendChild($items) | Out-Null
        $contract.Save((Join-Path $contractDirectory "Train.$module.Contracts.csproj"))
    }
    $escapedEvidence = [Security.SecurityElement]::Escape($buildInputs)
    @"
<Project>
  <Target Name="RecordAssetProbeCompilation" AfterTargets="CoreCompile">
    <WriteLinesToFile File="$escapedEvidence/`$(MSBuildProjectName).compile.txt" Lines="@(Compile->'%(FullPath)')" Overwrite="true" />
    <WriteLinesToFile File="$escapedEvidence/`$(MSBuildProjectName).references.txt" Lines="@(ReferencePath->'%(FullPath)')" Overwrite="true" />
    <WriteLinesToFile File="$escapedEvidence/`$(MSBuildProjectName).`$(Configuration).compile.txt" Lines="@(Compile->'%(FullPath)')" Overwrite="true" />
    <WriteLinesToFile File="$escapedEvidence/`$(MSBuildProjectName).`$(Configuration).references.txt" Lines="@(ReferencePath->'%(FullPath)')" Overwrite="true" />
  </Target>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $staging 'Directory.Build.targets')
    Copy-Item -LiteralPath (Join-Path $root 'builtin/core/resources/voxels/grain/placeholder.png') -Destination (Join-Path $data 'production-source.png')
    Set-Content -LiteralPath (Join-Path $data 'source-root.txt') -Value $root
    Invoke-ProbeProcess 'solution-create' 'dotnet' @('new', 'sln', '--format', 'sln', '--name', 'Train.AssetExport', '--output', $project)
    Invoke-ProbeProcess 'solution-add' 'dotnet' @('sln', (Join-Path $project 'Train.AssetExport.sln'), 'add', $projectFile)
    Invoke-ProbeProcess 'build' 'dotnet' @('build', $projectFile, '-c', 'Debug')
    Invoke-ProbeProcess 'prepare' $env:GODOT @('--headless', '--path', $project, '--log-file', (Join-Path $output 'prepare.godot.log'), '--', '--prepare', '--data-root', $data)
    Invoke-ProbeProcess 'import' $env:GODOT @('--headless', '--path', $project, '--editor', '--import', '--log-file', (Join-Path $output 'import.godot.log'))
    $exportDirectory = Join-Path $output 'export'
    New-Item -ItemType Directory -Path $exportDirectory | Out-Null
    $probe = Join-Path $exportDirectory 'asset-probe.exe'
    try {
        Invoke-ProbeProcess 'export' $env:GODOT @('--headless', '--path', $project, '--export-release', 'Windows Asset Probe', $probe, '--log-file', (Join-Path $output 'export.godot.log'))
    } finally {
        if (Test-Path -LiteralPath $templateDirectory) { Remove-Item -LiteralPath $templateDirectory -Recurse -Force }
    }
    Invoke-ProbeProcess 'runtime' $probe @('--headless', '--log-file', (Join-Path $output 'runtime.godot.log'), '--', '--data-root', $data, '--game-pck', $gamePck)
}
finally {
    try {
        if ($ownsTemplateDirectory -and (Test-Path -LiteralPath $templateDirectory)) {
            Remove-Item -LiteralPath $templateDirectory -Recurse -Force
        }
        [ordered]@{ elapsedSeconds = $watch.Elapsed.TotalSeconds } | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $output 'duration.json')
    }
    finally {
        foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name]) }
    }
}
