function Get-BuildFailureKind([string] $Output) {
    # MSBuild 复制遇到文件占用时先报 MSB3027，再报 MSB3021，两者同属环境阻塞。
    $diagnostics = ($Output -split '\r?\n' | Where-Object { $_ -notmatch '\bwarning(?:\s+[A-Z]+\d+)?\s*:' }) -join "`n"
    $errors = [regex]::Matches($diagnostics, '(?im)\berror\s+([A-Z]+\d+)\b')
    foreach ($diagnosticError in $errors) {
        if ($diagnosticError.Groups[1].Value -notmatch '^(NU1301|NETSDK1141|MSB4236|CS2012|MSB3027|MSB3021)$') { return 'failed' }
    }
    if ($errors.Count -gt 0 -or $diagnostics -match 'compatible .NET SDK was not found|No .NET SDKs were found') {
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
