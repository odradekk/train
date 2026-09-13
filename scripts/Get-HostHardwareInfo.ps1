function Get-HostCpuName {
    if ($env:OS -eq "Windows_NT") {
        return Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name
    }

    $model = Get-Content -LiteralPath "/proc/cpuinfo" -ErrorAction SilentlyContinue |
        Where-Object { $_ -match "^model name\s*:\s*(.+)$" } |
        Select-Object -First 1
    if ($model -match "^model name\s*:\s*(.+)$") {
        return $Matches[1]
    }

    return "$([System.Environment]::ProcessorCount) logical processors"
}

function Get-HostPhysicalMemoryBytes {
    if ($env:OS -eq "Windows_NT") {
        return (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    }

    $line = Get-Content -LiteralPath "/proc/meminfo" -ErrorAction SilentlyContinue |
        Where-Object { $_ -match "^MemTotal:\s+(\d+)\s+kB$" } |
        Select-Object -First 1
    if ($line -match "^MemTotal:\s+(\d+)\s+kB$") {
        return [int64] $Matches[1] * 1KB
    }

    return "unknown"
}
