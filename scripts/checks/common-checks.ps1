# 统一入口与结构检查共用构建失败分类、允许依赖闭包与元数据读取。
# 仓库错误不能被同时出现的环境错误遮蔽。
function Get-BuildFailureKind([string] $Output) {
    if ($Output -match '(?im)\berror\s+(?:CS(?!2012)\d+|NU(?!1301)\d+|MSB4019|NETSDK1045|NETSDK1141)\b') { return 'failed' }
    if ($Output -match '(?im)\berror\s+(?:NU1301|MSB4236|CS2012|MSB3027)\b' -or
        $Output -match 'compatible .NET SDK was not found|No .NET SDKs were found|Unable to load the service index') {
        return 'blocked'
    }
    return 'failed'
}

function Get-AllowedProjectClosure([System.Collections.IDictionary] $Projects, [string] $Name, [switch] $IncludeSelf) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending = [Collections.Generic.Queue[string]]::new()
    if (-not $Projects.Contains($Name)) { throw [IO.InvalidDataException]::new("unknown project in boundary matrix: $Name") }
    if ($IncludeSelf) { $pending.Enqueue($Name) }
    else { foreach ($item in @($Projects[$Name].references)) { $pending.Enqueue([string] $item) } }
    while ($pending.Count -gt 0) {
        $item = $pending.Dequeue()
        if (-not $Projects.Contains($item)) { throw [IO.InvalidDataException]::new("unknown project in boundary matrix: $item") }
        if (-not $seen.Add($item)) { continue }
        foreach ($child in @($Projects[$item].references)) { $pending.Enqueue([string] $child) }
    }
    return @($seen)
}

function Get-AssemblyReferenceNames($Metadata) {
    foreach ($handle in $Metadata.AssemblyReferences) {
        $Metadata.GetString($Metadata.GetAssemblyReference($handle).Name)
    }
}

# Microsoft.NET.Test.Sdk 生成的入口来自 NuGet 缓存，允许编译进测试项目。
function Test-TestSdkCompileSource([string] $Path) {
    return $Path -match '[\\/]microsoft\.net\.test\.sdk[\\/][^\\/]+[\\/]build[\\/]netcoreapp3\.1[\\/]Microsoft\.NET\.Test\.Sdk\.Program\.cs$'
}
