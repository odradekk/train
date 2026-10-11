param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
    [string] $OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'TestResults/contract-checks' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()

$projects = [ordered]@{
    'Train.Core.Contracts' = 'Contracts/Core/Train.Core.Contracts.csproj'
    'Train.Content.Contracts' = 'Contracts/Content/Train.Content.Contracts.csproj'
    'Train.Scenes.Contracts' = 'Contracts/Scenes/Train.Scenes.Contracts.csproj'
    'Train.Core' = 'Core/Train.Core.csproj'
    'Train.Content' = 'Content/Train.Content.csproj'
    'Train.Scenes' = 'Scenes/Train.Scenes.csproj'
    'Train' = 'Train.csproj'
    'Train.Core.ContractSupport' = 'Tests/ContractSupport/Core/Train.Core.ContractSupport.csproj'
    'Train.Content.ContractSupport' = 'Tests/ContractSupport/Content/Train.Content.ContractSupport.csproj'
    'Train.Scenes.ContractSupport' = 'Tests/ContractSupport/Scenes/Train.Scenes.ContractSupport.csproj'
    'Train.ContractSupport.Tests' = 'Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj'
    'Train.Core.Tests' = 'Tests/Core/Train.Core.Tests.csproj'
    'Train.Content.Tests' = 'Tests/Content/Train.Content.Tests.csproj'
    'Train.Godot.Tests' = 'Tests/Godot/Train.Godot.Tests.csproj'
    'Train.Core.ContractSamples' = 'prototypes/core-contract/Train.Core.ContractSamples.csproj'
    'Train.Content.ContractSamples' = 'prototypes/content-contract/Train.Content.ContractSamples.csproj'
    'Train.Scenes.ContractSamples' = 'prototypes/scenes-contract/Train.Scenes.ContractSamples.csproj'
    'Train.Godot.ContractSamples' = 'prototypes/godot-contract/Train.Godot.ContractSamples.csproj'
}

$direct = @{
    'Train.Core.Contracts' = @()
    'Train.Content.Contracts' = @('Train.Core.Contracts')
    'Train.Scenes.Contracts' = @('Train.Core.Contracts', 'Train.Content.Contracts')
    'Train.Core' = @('Train.Core.Contracts')
    'Train.Content' = @('Train.Core.Contracts', 'Train.Content.Contracts')
    'Train.Scenes' = @('Train.Core.Contracts', 'Train.Content.Contracts', 'Train.Scenes.Contracts')
    'Train' = @('Train.Core.Contracts', 'Train.Content.Contracts', 'Train.Scenes.Contracts', 'Train.Core', 'Train.Content', 'Train.Scenes')
    'Train.Core.ContractSupport' = @('Train.Core.Contracts')
    'Train.Content.ContractSupport' = @('Train.Core.Contracts', 'Train.Content.Contracts')
    'Train.Scenes.ContractSupport' = @('Train.Core.Contracts', 'Train.Content.Contracts', 'Train.Scenes.Contracts')
    'Train.ContractSupport.Tests' = @('Train.Core.ContractSupport', 'Train.Content.ContractSupport', 'Train.Scenes.ContractSupport')
    'Train.Core.Tests' = @('Train.Core', 'Train.Core.Contracts', 'Train.Core.ContractSupport')
    'Train.Content.Tests' = @('Train.Content', 'Train.Core.Contracts', 'Train.Content.Contracts', 'Train.Content.ContractSupport')
    'Train.Godot.Tests' = @('Train')
    'Train.Core.ContractSamples' = @('Train.Core.Contracts')
    'Train.Content.ContractSamples' = @('Train.Content.Contracts')
    'Train.Scenes.ContractSamples' = @('Train.Scenes.Contracts')
    'Train.Godot.ContractSamples' = @('Train.Core.Contracts', 'Train.Content.Contracts', 'Train.Scenes.Contracts')
}

function Get-AllowedClosure([string] $name) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($item in $direct[$name]) { $pending.Enqueue($item) }
    while ($pending.Count -gt 0) {
        $item = $pending.Dequeue()
        if (-not $seen.Add($item)) { continue }
        foreach ($child in $direct[$item]) { $pending.Enqueue($child) }
    }
    return @($seen)
}

function Test-ChildPath([string] $child, [string] $parent) {
    $resolved = [IO.Path]::GetFullPath($child)
    $prefix = [IO.Path]::GetFullPath($parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Add-Failure([string] $message) {
    $failures.Add($message)
    [Console]::Error.WriteLine($message)
}

Push-Location $root
try {
    $builds = @('Train.sln') + @($projects.Values | Where-Object { $_ -like 'prototypes/*' })
    foreach ($build in $builds) {
        $log = Join-Path $OutputDirectory ((Split-Path -Leaf $build) + '.build.log')
        & dotnet build $build -c $Configuration --nologo *> $log
        if ($LASTEXITCODE -ne 0) {
            [Console]::Error.WriteLine("blocked build: $build; log=$log")
            exit 2
        }
    }
    foreach ($entry in $projects.GetEnumerator()) {
        $name = [string] $entry.Key
        $project = [IO.Path]::GetFullPath((Join-Path $root $entry.Value))
        $errorLog = Join-Path $OutputDirectory "$name.msbuild-errors.log"
        $raw = & dotnet msbuild $project -property:Configuration=$Configuration -target:ResolveReferences '-getItem:ProjectReference,ReferencePath,Compile,InternalsVisibleTo' -getProperty:TargetPath 2> $errorLog
        if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine("blocked evaluation: $name; log=$errorLog"); exit 2 }
        $data = $raw | ConvertFrom-Json
        $closure = @(Get-AllowedClosure $name)
        $record = [ordered]@{ project = $entry.Value; assembly = [string] $data.Properties.TargetPath; projectReferences = @(); resolvedAssemblies = @(); assemblyReferences = @(); compile = @(); friends = @() }
        foreach ($item in @($data.Items.ProjectReference)) {
            $target = [string] $item.Filename
            $path = [string] $item.FullPath
            $record.projectReferences += @{ name = $target; path = $path }
            if ([string] $item.DefiningProjectFullPath -ne $project) { continue }
            if (-not $projects.Contains($target) -or $target -notin $direct[$name] -or
                -not [IO.Path]::GetFullPath($path).Equals([IO.Path]::GetFullPath((Join-Path $root $projects[$target])), [StringComparison]::OrdinalIgnoreCase)) {
                Add-Failure "forbidden project reference: $name -> $target ($path)"
            }
        }
        foreach ($item in @($data.Items.ReferencePath)) {
            $target = [string] $item.Filename
            if ($name -notin @('Train', 'Train.Godot.Tests', 'Train.Godot.ContractSamples') -and $target -like 'GodotSharp*') {
                Add-Failure "forbidden Godot assembly: $name -> $target ($($item.FullPath))"
            }
            if (-not ($target -eq 'Train' -or $target.StartsWith('Train.', [StringComparison]::Ordinal))) { continue }
            $path = [string] $item.FullPath
            $record.resolvedAssemblies += @{ name = $target; path = $path }
            if (-not $projects.Contains($target) -or $target -notin $closure) {
                Add-Failure "forbidden resolved assembly: $name -> $target ($path)"
            }
            else {
                $targetDirectory = Split-Path -Parent ([IO.Path]::GetFullPath((Join-Path $root $projects[$target])))
                if ($target -eq 'Train') { $targetDirectory = $root }
                if (-not (Test-ChildPath $path $targetDirectory)) {
                    Add-Failure "assembly resolved outside owner: $name -> $target ($path)"
                }
            }
        }
        foreach ($item in @($data.Items.Compile)) {
            $path = [string] $item.FullPath
            $record.compile += $path
            $owner = Split-Path -Parent $project
            if ($path -match '[\\/]microsoft.net.test.sdk[\\/][^\\/]+[\\/]build[\\/]netcoreapp3.1[\\/]Microsoft.NET.Test.Sdk.Program.cs$') { continue }
            if (-not (Test-ChildPath $path $owner)) { Add-Failure "linked source outside owner: $name ($path)" }
            if ($name -eq 'Train' -and -not ($path -match '[\\/](App|Ui|Rendering|obj)[\\/]')) {
                Add-Failure "host compiles foreign source: $path"
            }
        }
        foreach ($item in @($data.Items.InternalsVisibleTo)) {
            $friend = [string] $item.Identity
            $record.friends += $friend
            $expected = switch ($name) {
                'Train.Core' { 'Train.Core.Tests' }
                'Train.Content' { 'Train.Content.Tests' }
                'Train' { 'Train.Godot.Tests' }
                default { '' }
            }
            if ($friend -ne $expected) { Add-Failure "forbidden friend assembly: $name -> $friend" }
        }
        $assembly = [string] $data.Properties.TargetPath
        if (-not (Test-Path -LiteralPath $assembly)) { [Console]::Error.WriteLine("blocked output missing: $assembly"); exit 2 }
        foreach ($reference in [Reflection.Assembly]::LoadFile($assembly).GetReferencedAssemblies()) {
            $target = [string] $reference.Name
            if ($name -notin @('Train', 'Train.Godot.Tests', 'Train.Godot.ContractSamples') -and $target -like 'GodotSharp*') {
                Add-Failure "forbidden output Godot reference: $name -> $target"
            }
            if (-not ($target -eq 'Train' -or $target.StartsWith('Train.', [StringComparison]::Ordinal))) { continue }
            $record.assemblyReferences += $target
            if ($target -notin $closure) { Add-Failure "forbidden output assembly reference: $name -> $target" }
        }
        $report.Add($record)
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'references.json') -Encoding utf8
    & dotnet build Tests/ContractChecks/Train.ContractChecks.csproj -c $Configuration --nologo *> (Join-Path $OutputDirectory 'contract-surface.build.log')
    if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine('blocked contract surface tool build'); exit 2 }
    $tool = Join-Path $root "Tests/ContractChecks/bin/$Configuration/net10.0/Train.ContractChecks.dll"
    $baseline = Join-Path $root 'docs/contracts/public-api.txt'
    & dotnet $tool verify $root $Configuration $baseline
    if ($LASTEXITCODE -ne 0) { Add-Failure 'contract public surface or type ownership changed' }
    "checked projects=$($projects.Count), failures=$($failures.Count); evidence=$OutputDirectory"
    if ($failures.Count -gt 0) { exit 1 }
}
finally { Pop-Location }
