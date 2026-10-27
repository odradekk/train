using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;
using Train.App.Measurement;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class ProcessMemoryTests
{
    [TestMethod]
    public void LinuxPrivateBytesIncludeSwapAndKeepTheLargestSample()
    {
        string[] status = ["Name:\ttrain", "VmRSS:\t  100 kB", "VmHWM:   160 kB",
            "VmData: 900 kB", "RssAnon:\t40 kB", "VmSwap:  8 kB"];
        var first = ProcessMemory.ReadLinux(status, 0);
        Assert.AreEqual((49152L, 102400L, 49152L, 163840L), first);
        var later = ProcessMemory.ReadLinux(["VmRSS: 90 kB", "VmHWM: 160 kB", "RssAnon: 30 kB"], first.PeakPrivate);
        Assert.AreEqual((30720L, 92160L, 49152L, 163840L), later);
    }

    [TestMethod]
    [DataRow("VmRSS:")]
    [DataRow("VmHWM:")]
    [DataRow("RssAnon:")]
    public void LinuxMemoryCannotBeReportedWithoutRequiredCounters(string missing)
    {
        string[] status = ["VmRSS: 100 kB", "VmHWM: 160 kB", "RssAnon: 40 kB", "VmSwap: 8 kB"];
        Assert.Throws<InvalidDataException>(() => ProcessMemory.ReadLinux(
            status.Where(line => !line.StartsWith(missing, StringComparison.Ordinal)), 0));
    }

    [TestMethod]
    [TestCategory("integration")]
    [OSCondition(OperatingSystems.Windows)]
    public void WindowsMemoryMatchesThePreviousProcessCounters()
    {
        const uint createSuspended = 0x00000004, createNoWindow = 0x08000000;
        var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcess(Path.Combine(Environment.SystemDirectory, "cmd.exe"), IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, false, createSuspended | createNoWindow, IntPtr.Zero, null,
                ref startup, out var created))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var processHandle = new SafeProcessHandle(created.Process, ownsHandle: true);
        using var threadHandle = new SafeWaitHandle(created.Thread, ownsHandle: true);
        try
        {
            using var process = Process.GetProcessById(created.ProcessId);
            var actual = ProcessMemory.Read(process, long.MaxValue);
            Assert.AreEqual(process.PrivateMemorySize64, actual.Private);
            Assert.AreEqual(process.WorkingSet64, actual.Working);
            Assert.AreEqual(process.PeakPagedMemorySize64, actual.PeakPrivate);
            Assert.AreEqual(process.PeakWorkingSet64, actual.PeakWorking);
        }
        finally
        {
            if (!TerminateProcess(processHandle, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Assert.AreEqual(0u, WaitForSingleObject(processHandle, 5000), "测试子进程未退出。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Size;
        internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, Reserved2Size;
        internal IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process, Thread;
        internal int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, IntPtr commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
}
