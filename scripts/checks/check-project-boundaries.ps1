# 检查项目引用、源码归属、友元程序集、公开面与调用样例。
# 允许依赖读取 project-boundaries.json；检查结果保存在本次运行目录。
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Debug',
    [string] $OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common-checks.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'TestResults/contract-checks' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()

# 结构检查与隔离构建共享同一份允许依赖，避免两个入口各自放宽模块边界。
# Train 是当前组合入口，可直接引用三个实现；真实工厂的调用位置仍待建立后检查。
$matrix = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'project-boundaries.json') -Raw | ConvertFrom-Json -AsHashtable
$projects = [ordered]@{}
$expectedFriends = @{ 'Train.Core' = 'Train.Core.Tests'; 'Train.Content' = 'Train.Content.Tests'; 'Train' = 'Train.Godot.Tests' }
$godotProjects = @('Train', 'Train.Godot.Tests', 'Train.Godot.ContractSamples')
foreach ($name in $matrix.projects.Keys) {
    $projects[$name] = $matrix.projects[$name].path
}

# 使用目录分隔符限定前缀，避免同名前缀目录被当成项目所有者。
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
    # 从版本控制清单检查未登记项目，防止新项目绕过固定矩阵；归档不属于当前产品。
    $trackedProjects = @(& git ls-files --cached --others --exclude-standard -- '*.csproj' | Where-Object { $_ -notlike 'archive/*' })
    if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine('blocked project inventory'); exit 2 }
    foreach ($path in $trackedProjects) {
        # 公开面检查工具不属于模块图；它读取源码和允许依赖矩阵，没有产品项目引用。
        if ($path -ne 'Tests/ContractChecks/Train.ContractChecks.csproj' -and $path -notin $projects.Values) {
            Add-Failure "unregistered project: $path"
        }
    }
    $builds = @('Train.sln') + @($projects.Values | Where-Object { $_ -like 'prototypes/*' })
    foreach ($build in $builds) {
        $log = Join-Path $OutputDirectory ((Split-Path -Leaf $build) + '.build.log')
        & dotnet build $build -c $Configuration --nologo *> $log
        if ($LASTEXITCODE -ne 0) {
            # 只把可辨认的工具/环境缺失记为阻塞，其余构建错误属于仓库失败。
            $buildOutput = Get-Content -LiteralPath $log -Raw
            $kind = Get-BuildFailureKind $buildOutput
            if ($kind -eq 'blocked') {
                [Console]::Error.WriteLine("blocked build: $build; log=$log")
                if ($failures.Count -eq 0) { exit 2 }
                exit 1
            }
            Add-Failure "failed build: $build; log=$log"
            exit 1
        }
    }
    foreach ($entry in $projects.GetEnumerator()) {
        $name = [string] $entry.Key
        $project = [IO.Path]::GetFullPath((Join-Path $root $entry.Value))
        $errorLog = Join-Path $OutputDirectory "$name.msbuild-errors.log"
        # Train.sln 的 Release|Any CPU 将 Godot 主项目映射到 ExportRelease。
        $projectConfiguration = if ($name -eq 'Train' -and $Configuration -eq 'Release') { 'ExportRelease' } else { $Configuration }
        $raw = & dotnet msbuild $project -property:Configuration=$projectConfiguration -target:ResolveReferences '-getItem:ProjectReference,ReferencePath,Compile,InternalsVisibleTo' -getProperty:TargetPath 2> $errorLog
        if ($LASTEXITCODE -ne 0) {
            $evaluationOutput = (@($raw) -join "`n") + (Get-Content -LiteralPath $errorLog -Raw)
            $kind = Get-BuildFailureKind $evaluationOutput
            [Console]::Error.WriteLine("$kind evaluation: $name; log=$errorLog")
            if ($kind -eq 'failed' -or $failures.Count -gt 0) { exit 1 }
            exit 2
        }
        $data = $raw | ConvertFrom-Json
        $closure = @(Get-AllowedProjectClosure -Projects $matrix.projects -Name $name)
        $record = [ordered]@{ project = $entry.Value; assembly = [string] $data.Properties.TargetPath; projectReferences = @(); resolvedAssemblies = @(); assemblyReferences = @(); compile = @(); friends = @() }
        foreach ($item in @($data.Items.ProjectReference)) {
            $target = [string] $item.Filename
            $path = [string] $item.FullPath
            $record.projectReferences += @{ name = $target; path = $path }
            # 导入的项目引用不按本项目直接边判断，解析后的程序集仍受闭包检查。
            if ([string] $item.DefiningProjectFullPath -ne $project) { continue }
            if (-not $projects.Contains($target) -or $target -notin @($matrix.projects[$name].references) -or
                -not [IO.Path]::GetFullPath($path).Equals([IO.Path]::GetFullPath((Join-Path $root $projects[$target])), [StringComparison]::OrdinalIgnoreCase)) {
                Add-Failure "forbidden project reference: $name -> $target ($path)"
            }
        }
        foreach ($item in @($data.Items.ReferencePath)) {
            $target = [string] $item.Filename
            if ($name -notin $godotProjects -and $target -like 'GodotSharp*') {
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
            # Microsoft.NET.Test.Sdk 注入的 Program.cs 在 NuGet 缓存内，不属于仓库的生产源码。
            if (Test-TestSdkCompileSource $path) { continue }
            if (-not (Test-ChildPath $path $owner)) { Add-Failure "linked source outside owner: $name ($path)" }
            if ($name -eq 'Train' -and -not ([IO.Path]::GetRelativePath($root, $path) -match '^(App|Ui|Rendering)[\\/]|^\.godot[\\/]mono[\\/]temp[\\/]obj[\\/]')) {
                Add-Failure "host compiles foreign source: $path"
            }
        }
        foreach ($item in @($data.Items.InternalsVisibleTo)) {
            $friend = [string] $item.Identity
            $record.friends += $friend
            if ($friend -ne $expectedFriends[$name]) { Add-Failure "forbidden friend assembly: $name -> $friend" }
        }
        $assembly = [string] $data.Properties.TargetPath
        if (-not (Test-Path -LiteralPath $assembly)) { [Console]::Error.WriteLine("failed output missing: $assembly"); exit 1 }
        # 直接读取 PE 元数据，避免加载 Godot 或其他实现依赖；覆盖手写友元属性。
        $stream = [IO.File]::OpenRead($assembly)
        $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
                $attribute = $metadata.GetCustomAttribute($handle)
                if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
                $constructor = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle] $attribute.Constructor)
                if ($constructor.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
                $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle] $constructor.Parent)
                if ($metadata.GetString($type.Namespace) -ne 'System.Runtime.CompilerServices' -or
                    $metadata.GetString($type.Name) -ne 'InternalsVisibleToAttribute') { continue }
                $blob = $metadata.GetBlobReader($attribute.Value)
                $null = $blob.ReadUInt16()
                $friend = $blob.ReadSerializedString()
                if ($friend -notin $record.friends) {
                    $record.friends += $friend
                    if ($friend -ne $expectedFriends[$name]) { Add-Failure "forbidden output friend assembly: $name -> $friend" }
                }
            }
            foreach ($target in @(Get-AssemblyReferenceNames $metadata)) {
                if ($name -notin $godotProjects -and $target -like 'GodotSharp*') {
                    Add-Failure "forbidden output Godot reference: $name -> $target"
                }
                if (-not ($target -eq 'Train' -or $target.StartsWith('Train.', [StringComparison]::Ordinal))) { continue }
                $record.assemblyReferences += $target
                if ($target -notin $closure) { Add-Failure "forbidden output assembly reference: $name -> $target" }
            }
        }
        finally { $pe.Dispose(); $stream.Dispose() }
        $report.Add($record)
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'references.json') -Encoding utf8
    & dotnet build Tests/ContractChecks/Train.ContractChecks.csproj -c $Configuration --nologo *> (Join-Path $OutputDirectory 'contract-surface.build.log')
    if ($LASTEXITCODE -ne 0) {
        $kind = Get-BuildFailureKind (Get-Content -LiteralPath (Join-Path $OutputDirectory 'contract-surface.build.log') -Raw)
        if ($kind -eq 'blocked') {
            [Console]::Error.WriteLine("blocked contract surface tool build; log=$(Join-Path $OutputDirectory 'contract-surface.build.log')")
            if ($failures.Count -eq 0) { exit 2 }
            exit 1
        }
        Add-Failure "failed contract surface tool build; log=$(Join-Path $OutputDirectory 'contract-surface.build.log')"
        exit 1
    }
    $tool = Join-Path $root "Tests/ContractChecks/bin/$Configuration/net10.0/Train.ContractChecks.dll"
    $baseline = Join-Path $root 'docs/contracts/public-api.txt'
    & dotnet $tool verify $root $Configuration $baseline
    if ($LASTEXITCODE -ne 0) { Add-Failure 'contract public surface or type ownership changed' }
    "checked projects=$($projects.Count), failures=$($failures.Count); evidence=$OutputDirectory"
    if ($failures.Count -gt 0) { exit 1 }
}
finally { Pop-Location }
