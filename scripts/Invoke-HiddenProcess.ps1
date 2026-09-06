# Wait for a Windows GUI or console process and capture stdout, stderr, and exit code.
# Call-operator capture (`& exe 2>&1`) misses GUI-subsystem stdout and can leave $LASTEXITCODE unset.
# Start-Process -Wait -WindowStyle Hidden races when a GUI exe exits immediately, so this uses
# ProcessStartInfo with the same hidden window and attached redirects.

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
        [string[]] $ArgumentList = @()
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
        $psi.FileName = $env:ComSpec
        if ([string]::IsNullOrEmpty($psi.FileName)) {
            $psi.FileName = "cmd.exe"
        }
        $quoted = '"' + $FilePath + '"'
        if ($args.Length -gt 0) {
            $psi.Arguments = "/c $quoted $args"
        }
        else {
            $psi.Arguments = "/c $quoted"
        }
    }
    else {
        $psi.FileName = $FilePath
        $psi.Arguments = $args
    }

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $psi
    $process.Start() | Out-Null
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($null -eq $stdout) { $stdout = "" }
    if ($null -eq $stderr) { $stderr = "" }

    $exit = -1
    if ($null -ne $process.ExitCode) {
        $exit = [int] $process.ExitCode
    }

    [pscustomobject]@{
        ExitCode = $exit
        Output   = $stdout + $stderr
    }
}
