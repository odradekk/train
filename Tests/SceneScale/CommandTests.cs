using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Train.SceneScale.Tests;
[TestClass]
[TestCategory("integration")]
public sealed class CommandTests
{
    [TestMethod]
    [DataRow("local", "", "FileNotFoundException")]
    [DataRow("stats", "", "FileNotFoundException")]
    [DataRow("local", "--verify-stable", "FileNotFoundException")]
    [DataRow("ready", "--verify-stable", "ArgumentException")]
    [DataRow("stats", "--verify-stable", "ArgumentException")]
    [DataRow("bounds", "--verify-stable", "ArgumentException")]
    [DataRow("local", "focus.json", "ArgumentException")]
    [DataRow("local", "--verify-stable extra", "ArgumentException")]
    public async Task Command_validates_options_without_exposing_paths(string mode, string options, string error)
    {
        var directory = Path.Combine(Path.GetTempPath(), "scene-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { typeof(Probe).Assembly.Location, directory, mode, "private-save.json" }) start.ArgumentList.Add(arg);
            foreach (var arg in options.Split(' ', StringSplitOptions.RemoveEmptyEntries)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(true); }
            Assert.AreNotEqual(0, process.ExitCode);
            Assert.AreEqual("", await stdout);
            var diagnostic = await stderr;
            StringAssert.Contains(diagnostic, "scene-scale failed: " + error);
            Assert.IsFalse(diagnostic.Contains(directory, StringComparison.Ordinal));
            Assert.IsFalse(diagnostic.Contains("private-save", StringComparison.Ordinal));
        }
        finally { Directory.Delete(directory, true); }
    }
}
