#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Directory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$relative = '<directory>'
$identities = @()
try {
    $directoryPath = (Resolve-Path -LiteralPath $Directory).Path
    $runnerRoot = if ($env:RUNNER_WORKSPACE) { Split-Path (Split-Path $env:RUNNER_WORKSPACE -Parent) -Parent }
    $prefixes = @{
        '<workspace>' = $env:GITHUB_WORKSPACE; '<temp>' = $env:RUNNER_TEMP
        '<tool-cache>' = $env:RUNNER_TOOL_CACHE; '<runner>' = $runnerRoot
        '<home>' = $HOME; '<env-home>' = $env:HOME; '<dotnet>' = $env:DOTNET_ROOT
        '<godot>' = $(if ($env:GODOT) { Split-Path $env:GODOT -Parent })
        '<system-temp>' = [IO.Path]::GetTempPath().TrimEnd([char[]]'\/')
        '<runtime>' = $(if ($IsLinux) { "/run/user/$(& id -u)" })
    }
    foreach ($command in @(Get-Command python, python3 -ErrorAction SilentlyContinue)) {
        $prefixes["<$($command.Name)>"] = $command.Source
    }
    $replacements = @(foreach ($entry in $prefixes.GetEnumerator()) {
        if ($entry.Value) {
            foreach ($path in @($entry.Value, $entry.Value.Replace('\', '/'), $entry.Value.Replace('\', '\\')) | Select-Object -Unique) {
                [pscustomobject]@{ path = $path.TrimEnd('/'); replacement = $entry.Key }
            }
        }
    }) | Sort-Object { $_.path.Length } -Descending
    $identities = @([Environment]::MachineName, [Environment]::UserName,
        $env:HOSTNAME, $env:COMPUTERNAME, $env:USER, $env:USERNAME, $HOME, $env:HOME) |
        Where-Object { $_ } | Select-Object -Unique
    $forbidden = '(?i)' + ((@($identities | ForEach-Object { [regex]::Escape($_) }) +
        @('/home/', '/root/', '/Users/', '[A-Z]:[\\/]+Users[\\/]', '/run/user/\d+', '\bsudo\b')) -join '|')
    foreach ($file in Get-ChildItem -LiteralPath $directoryPath -File -Recurse -Force) {
        $relative = [IO.Path]::GetRelativePath($directoryPath, $file.FullName)
        # 只改文本；PNG 等二进制仍扫描身份残留，不重编码。
        $content = [IO.File]::ReadAllText($file.FullName)
        if ($file.Extension -in '.json', '.jsonl', '.log', '.txt', '.csv', '.md', '.xml', '.trx') {
            foreach ($item in $replacements) { $content = $content.Replace($item.path, $item.replacement) }
            [IO.File]::WriteAllText($file.FullName, $content)
        }
        if ($content -match $forbidden -or $relative -match $forbidden) { throw 'Private evidence.' }
    }
}
catch {
    foreach ($identity in @($identities)) {
        if ($identity) { $relative = $relative.Replace($identity, '<redacted>') }
    }
    Write-Host "公开证据检查失败：$relative"
    exit 1
}
Write-Host '公开证据脱敏与残留检查通过。'
exit 0
