# 为 App 检查脚本的编排测试创建假的本机进程，并清理本次拥有的临时文件。
# 假进程只提供输出和退出状态，不证明 Godot 或游戏行为。

function Get-TestPowerShellPath {
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw "These harnesses require PowerShell 7 or later."
    }

    return (Get-Process -Id $PID).Path
}

# 清理本次创建的临时目录前，确认绝对目标是固定名称与 GUID 组成的直系临时子目录。
function Remove-ProcessFixtureDirectory {
    param([string] $Path, [string] $ExpectedPrefix)
    $target = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $temp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $parentPath = [System.IO.Path]::GetDirectoryName($target)
    $parent = if ($parentPath) { $parentPath.TrimEnd('\', '/') } else { "" }
    $leaf = [System.IO.Path]::GetFileName($target)
    $suffix = if ($leaf.StartsWith($ExpectedPrefix, [System.StringComparison]::Ordinal)) { $leaf.Substring($ExpectedPrefix.Length) } else { "" }
    if ($parent -ne $temp -or $suffix -notmatch '^[0-9a-f]{32}$') {
        throw "Refusing to remove an unexpected process fixture directory: $target"
    }
    if (-not (Test-Path -LiteralPath $target)) { return }
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop
    if (Test-Path -LiteralPath $target) {
        throw "Process fixture directory remains after cleanup: $target"
    }
}

function New-FakeNativeProgram {
    param(
        [string] $Name,
        [int] $ExitCode,
        [string[]] $Lines,
        [string] $Extension = ""
    )

    $id = [guid]::NewGuid().ToString("N")
    if ($IsWindows) {
        $suffix = if ($Extension) { $Extension } else { ".cmd" }
        $path = Join-Path ([System.IO.Path]::GetTempPath()) ("$Name-$id$suffix")
        $body = @("@echo off") + ($Lines | ForEach-Object { "echo $_" }) + @("exit /b $ExitCode")
        Set-Content -LiteralPath $path -Value $body -Encoding Ascii
        return $path
    }

    $suffix = if ($Extension) { $Extension } else { ".sh" }
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("$Name-$id$suffix")
    $body = @("#!/bin/sh") + ($Lines | ForEach-Object {
        if ($_ -match "'") {
            throw "Fake native program output cannot contain a single quote."
        }
        "printf '%s\n' '$_'"
    }) + @("exit $ExitCode")
    Set-Content -LiteralPath $path -Value $body -Encoding utf8NoBOM
    & chmod +x $path
    if ($LASTEXITCODE -ne 0) {
        throw "Could not make fake native program executable: $path"
    }
    return $path
}

function New-FakeWindowsGuiExe {
    param(
        [string] $Name,
        [int] $ExitCode,
        [string[]] $Lines
    )

    if (-not $IsWindows) {
        throw "Windows GUI fixtures can only be built on Windows."
    }

    $id = [guid]::NewGuid().ToString("N")
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("$Name-$id")
    $publish = Join-Path $root "publish"
    New-Item -ItemType Directory -Path $root | Out-Null

    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>TrainFakeGui</AssemblyName>
    <PublishSingleFile>true</PublishSingleFile>
    <DebugType>none</DebugType>
  </PropertyGroup>
</Project>
"@
    Set-Content -LiteralPath (Join-Path $root "TrainFakeGui.csproj") -Value $project -Encoding utf8NoBOM

    $prints = @($Lines | ForEach-Object {
        $literal = [System.Text.Json.JsonSerializer]::Serialize([string] $_, [System.Text.Json.JsonSerializerOptions]::new())
        "        System.Console.WriteLine($literal);"
    }) -join [Environment]::NewLine
    $code = @"
internal static class Program
{
    public static int Main()
    {
$prints
        return $ExitCode;
    }
}
"@
    Set-Content -LiteralPath (Join-Path $root "Program.cs") -Value $code -Encoding utf8NoBOM

    $runtime = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
    $buildOutput = & dotnet publish (Join-Path $root "TrainFakeGui.csproj") --configuration Release --runtime $runtime --self-contained false --output $publish 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Remove-ProcessFixtureDirectory -Path $root -ExpectedPrefix "$Name-"
        throw "Could not build Windows GUI fixture.`n$buildOutput"
    }

    $filePath = Join-Path $publish "TrainFakeGui.exe"
    if (-not (Test-Path -LiteralPath $filePath)) {
        Remove-ProcessFixtureDirectory -Path $root -ExpectedPrefix "$Name-"
        throw "Windows GUI fixture build did not produce $filePath."
    }

    [pscustomobject]@{
        FilePath = $filePath
        Root     = $root
    }
}
