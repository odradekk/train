using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Train.App.Measurement;

internal static class ProcessMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        internal uint Size, PageFaultCount;
        internal nuint PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool,
            QuotaPeakNonPagedPool, QuotaNonPagedPool, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);

    // 设计 00「私有字节」：Windows 为 PrivateUsage，Linux 为 RssAnon + VmSwap；单位均为字节。
    // Windows 私有峰值为 PeakPagefileUsage；Linux 私有峰值为采样中的最大值。
    internal static (long Private, long Working, long PeakPrivate, long PeakWorking) Read(
        Process process, long previousPeakPrivate)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetProcessMemoryInfo(process.Handle, out var memory, (uint)Marshal.SizeOf<ProcessMemoryCounters>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return ((long)memory.PrivateUsage, (long)memory.WorkingSet,
                (long)memory.PeakPagefileUsage, (long)memory.PeakWorkingSet);
        }
        return ReadLinux(File.ReadLines($"/proc/{process.Id}/status"), previousPeakPrivate);
    }

    internal static (long Private, long Working, long PeakPrivate, long PeakWorking) ReadLinux(
        IEnumerable<string> lines, long previousPeakPrivate)
    {
        long? working = null, peakWorking = null, anonymous = null, swapped = null;
        foreach (var line in lines)
        {
            // 字段名后是制表符和对齐空格，例如 "VmRSS:\t  123456 kB"。
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            long Bytes() => long.Parse(fields[1]) * 1024;
            switch (fields[0])
            {
                case "VmRSS:": working = Bytes(); break;
                case "VmHWM:": peakWorking = Bytes(); break;
                case "RssAnon:": anonymous = Bytes(); break;
                case "VmSwap:": swapped = Bytes(); break;
            }
        }
        if (working is null || peakWorking is null || anonymous is null)
            throw new InvalidDataException("进程状态缺少 VmRSS、VmHWM 或 RssAnon。");
        var privateBytes = anonymous.Value + (swapped ?? 0);
        return (privateBytes, working.Value, Math.Max(previousPeakPrivate, privateBytes), peakWorking.Value);
    }
}
