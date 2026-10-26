#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Directory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$relative = '<directory>'
$lineNumber = 0
$category = 'io'
$forbidden = [ordered]@{}
$removed = 0
try {
    $directoryPath = (Resolve-Path -LiteralPath $Directory).Path
    $runnerRoot = if ($env:RUNNER_WORKSPACE) { Split-Path (Split-Path $env:RUNNER_WORKSPACE -Parent) -Parent }
    $prefixes = @{
        '<workspace>' = $env:GITHUB_WORKSPACE; '<temp>' = $env:RUNNER_TEMP
        '<tool-cache>' = $env:RUNNER_TOOL_CACHE; '<runner>' = $runnerRoot
        '<home>' = $HOME; '<pwsh>' = $PSHOME
        '<godot>' = $(if ($env:GODOT) { Split-Path $env:GODOT -Parent })
        '<runtime>' = $(if ($IsLinux) { "/run/user/$(& id -u)" })
    }
    $replacements = $prefixes.GetEnumerator() | Where-Object Value | Sort-Object { $_.Value.Length } -Descending
    $identities = [ordered]@{
        hostname = @([Environment]::MachineName, $env:HOSTNAME, $env:COMPUTERNAME)
        username = @([Environment]::UserName, $env:USER, $env:USERNAME)
        home = @($HOME, $env:HOME)
    }
    foreach ($name in $identities.Keys) {
        $values = @($identities[$name] | Where-Object { $_ } | Select-Object -Unique | ForEach-Object { [regex]::Escape($_) })
        if ($values.Count) { $forbidden[$name] = $values -join '|' }
    }
    $forbidden['run-user'] = '/run/user/\d+'
    $forbidden['sudo'] = '\bsudo\b'
    foreach ($file in Get-ChildItem -LiteralPath $directoryPath -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($directoryPath, $file.FullName)
        foreach ($check in $forbidden.GetEnumerator()) {
            if ([regex]::IsMatch($relative, $check.Value, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                $category = $check.Key
                throw 'Private evidence.'
            }
        }
        if ($file.Extension -eq '.png') { continue }
        if ($file.Extension -notin '.json', '.jsonl', '.log', '.txt', '.csv', '.md', '.xml', '.trx') {
            Remove-Item -LiteralPath $file.FullName
            $removed++
            continue
        }
        $content = [IO.File]::ReadAllText($file.FullName)
        foreach ($item in $replacements) { $content = $content.Replace($item.Value, $item.Key) }
        [IO.File]::WriteAllText($file.FullName, $content)
        foreach ($check in $forbidden.GetEnumerator()) {
            $match = [regex]::Match($content, $check.Value, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($match.Success) {
                $lineNumber = 1 + [regex]::Matches($content.Substring(0, $match.Index), '\n').Count
                $category = $check.Key
                throw 'Private evidence.'
            }
        }
    }
}
catch {
    foreach ($pattern in $forbidden.Values) { $relative = [regex]::Replace($relative, $pattern, '<redacted>', [Text.RegularExpressions.RegexOptions]::IgnoreCase) }
    Write-Host "公开证据检查失败：${relative}:$lineNumber [$category]"
    exit 1
}
Write-Host "已删除其他类型文件：$removed"
Write-Host '公开证据脱敏与残留检查通过。'
exit 0
