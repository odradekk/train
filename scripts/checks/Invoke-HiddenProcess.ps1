# 以隐藏窗口运行 App 检查进程，捕获标准输出、错误、退出码和超时清理结果。
# PowerShell 调用运算符不能可靠捕获 GUI 程序输出；Start-Process 的等待也会与快速退出竞争。
# 因此通过 ProcessStartInfo 连接输出流，并等待本次启动的进程。

function ConvertTo-HiddenProcessArgumentString {
    param([string[]] $ArgumentList)
    if ($null -eq $ArgumentList -or $ArgumentList.Count -eq 0) {
        return ""
    }

    ($ArgumentList | ForEach-Object {
        if ($_ -match '[\s"]') {
            '"' + ($_ -replace '"', '\"') + '"'
        }
        else {
            $_
        }
    }) -join " "
}

function Invoke-HiddenProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,
        [string[]] $ArgumentList = @(),
        [ValidateRange(0, 2147478)]
        [int] $TimeoutSeconds = 0
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden

    $args = ConvertTo-HiddenProcessArgumentString -ArgumentList $ArgumentList
    $extension = [System.IO.Path]::GetExtension($FilePath)
    if ($extension -eq ".cmd" -or $extension -eq ".bat") {
        if ($env:OS -ne "Windows_NT") {
            throw "Cannot run Windows batch files on this host: $FilePath"
        }
        $psi.FileName = $env:ComSpec
        if ([string]::IsNullOrEmpty($psi.FileName)) {
            $psi.FileName = "cmd.exe"
        }
        # cmd.exe 会移除 /c 后的第一层引号；外层再包一层，才能同时保留脚本路径和带空格参数。
        $command = '"' + $FilePath + '"'
        if ($args.Length -gt 0) { $command += " $args" }
        $psi.Arguments = '/d /s /c "' + $command + '"'
    }
    else {
        $psi.FileName = $FilePath
        $psi.Arguments = $args
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    $started = $false
    $timedOut = $false
    $cleanupSucceeded = $true
    $stdout = ""
    $stderr = ""
    $exit = -1
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $cleanupDeadlineMs = if ($TimeoutSeconds -gt 0) { ($TimeoutSeconds + 5) * 1000 } else { 0 }
    try {
        $process.Start() | Out-Null
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if ($TimeoutSeconds -gt 0) {
            $finished = $process.WaitForExit($TimeoutSeconds * 1000)
            if (-not $finished) {
                $timedOut = $true
                try { $process.Kill($true) } catch { $cleanupSucceeded = $false }
                $cleanupWaitMs = [Math]::Max(1, $cleanupDeadlineMs - [int] $watch.ElapsedMilliseconds)
                if (-not $process.WaitForExit($cleanupWaitMs)) { $cleanupSucceeded = $false }
            }
        }
        else {
            $process.WaitForExit()
        }

        if ($process.HasExited) { $exit = [int] $process.ExitCode }
        $streamWaitMs = if ($TimeoutSeconds -gt 0) { [Math]::Max(1, $cleanupDeadlineMs - [int] $watch.ElapsedMilliseconds) } else { 5000 }
        if (-not [System.Threading.Tasks.Task]::WaitAll(@($stdoutTask, $stderrTask), $streamWaitMs)) {
            $cleanupSucceeded = $false
        }
        else {
            $stdout = $stdoutTask.GetAwaiter().GetResult()
            $stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            try { $process.Kill($true) } catch { $cleanupSucceeded = $false }
            $finalWaitMs = if ($TimeoutSeconds -gt 0) { [Math]::Max(1, $cleanupDeadlineMs - [int] $watch.ElapsedMilliseconds) } else { 5000 }
            try { if (-not $process.WaitForExit($finalWaitMs)) { $cleanupSucceeded = $false } } catch { $cleanupSucceeded = $false }
        }
        $watch.Stop()
        $process.Dispose()
    }
    if ($null -eq $stdout) { $stdout = "" }
    if ($null -eq $stderr) { $stderr = "" }

    [pscustomobject]@{
        ExitCode = $exit
        Output = $stdout + $stderr
        TimedOut = $timedOut
        CleanupSucceeded = $cleanupSucceeded
        DurationMs = [int] $watch.ElapsedMilliseconds
    }
}
