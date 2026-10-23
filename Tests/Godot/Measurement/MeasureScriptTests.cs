using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Godot.Tests.Measurement;

[TestClass]
[TestCategory("integration")]
[OSCondition(OperatingSystems.Windows)]
public sealed class MeasureScriptTests
{
    [TestMethod]
    public async Task Route_without_streaming_keeps_original_arguments_and_directory()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.Run("-Scenario", "scene-route-1x", "-Repeat", "1");
        Assert.AreEqual(0, result.ExitCode, result.Output);
        var arguments = fixture.Arguments("scene-route-1x-1");
        CollectionAssert.DoesNotContain(arguments, "--measure-streaming");
        CollectionAssert.DoesNotContain(arguments, "--measure-route-camera");
    }

    [TestMethod]
    public async Task Streaming_selection_forwards_camera_and_preserves_identity()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.Run("-Scenario", "scene-route-8x", "-Repeat", "1", "-StreamingDiagnostics", "-RouteCamera", "farthest");
        Assert.AreEqual(0, result.ExitCode, result.Output);
        var arguments = fixture.Arguments("scene-route-8x-farthest-1");
        CollectionAssert.Contains(arguments, "--measure-streaming");
        Assert.AreEqual("farthest", arguments[Array.IndexOf(arguments, "--measure-route-camera") + 1]);
        var identity = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Output, "scene-route-8x-farthest-1", "identity.json")))!;
        Assert.IsFalse(string.IsNullOrEmpty(identity["input"]!["sha256"]!.GetValue<string>()));
    }

    [TestMethod]
    public async Task Matrix_defaults_to_three_independent_runs_per_combination()
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.Run("-StreamingMatrix", "-DurationSeconds", "1");
        Assert.AreEqual(0, result.ExitCode, result.Output);
        var summary = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Output, "summary.json")))!;
        Assert.AreEqual(12, summary["runs"]!.AsArray().Count);
        foreach (var scenario in new[] { "scene-route-1x", "scene-route-8x" })
        foreach (var camera in new[] { "default", "farthest" })
        for (var repeat = 1; repeat <= 3; repeat++)
        {
            var arguments = fixture.Arguments($"{scenario}-{camera}-{repeat}");
            CollectionAssert.Contains(arguments, $"--measure={scenario}");
            CollectionAssert.Contains(arguments, "--measure-streaming");
            Assert.AreEqual(camera, arguments[Array.IndexOf(arguments, "--measure-route-camera") + 1]);
            Assert.AreEqual("1", arguments[Array.IndexOf(arguments, "--measure-duration") + 1]);
        }
    }

    [TestMethod]
    public async Task Matrix_preview_accepts_identical_save_copy_and_repeat_one()
    {
        using var fixture = new ScriptFixture();
        var copy = Path.Combine(fixture.Directory, "copy.json");
        File.Copy(fixture.Input, copy);
        var result = await fixture.Run("-StreamingMatrix", "-Repeat", "1", "-InputPath", copy);
        Assert.AreEqual(0, result.ExitCode, result.Output);
        Assert.AreEqual(4, JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Output, "summary.json")))!["runs"]!.AsArray().Count);
    }

    [TestMethod]
    [DataRow("-RouteCamera", "farthest")]
    [DataRow("-StreamingDiagnostics", "-Scenario", "scene-follow")]
    [DataRow("-StreamingMatrix", "-Scenario", "scene-route-1x")]
    [DataRow("-StreamingMatrix", "-RouteCamera", "default")]
    public async Task Invalid_selection_fails_before_launch(params string[] arguments)
    {
        using var fixture = new ScriptFixture();
        var result = await fixture.Run(arguments);
        Assert.AreEqual(1, result.ExitCode, result.Output);
        Assert.IsFalse(System.IO.Directory.Exists(fixture.Output));
    }

    [TestMethod]
    public async Task Matrix_rejects_different_input()
    {
        using var fixture = new ScriptFixture();
        var input = Path.Combine(fixture.Directory, "different.json");
        File.WriteAllText(input, "{}");
        var result = await fixture.Run("-StreamingMatrix", "-InputPath", input);
        Assert.AreEqual(1, result.ExitCode, result.Output);
        Assert.IsFalse(System.IO.Directory.Exists(fixture.Output));
    }

    private sealed class ScriptFixture : IDisposable
    {
        public string Directory { get; }
        public string Input { get; }
        public string Output => Path.Combine(Directory, "output");

        public ScriptFixture()
        {
            // 此编排测试替换进程启动端口；不启动 Godot，也不证明渲染行为。
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "global.json"))) repository = repository.Parent;
            Assert.IsNotNull(repository);
            Directory = Path.Combine(repository.FullName, "TestResults", "measure-script", Guid.NewGuid().ToString("N"));
            var checks = Path.Combine(Directory, "scripts", "checks");
            System.IO.Directory.CreateDirectory(checks);
            File.Copy(Path.Combine(repository.FullName, "scripts", "measure.ps1"), Path.Combine(Directory, "scripts", "measure.ps1"));
            System.IO.Directory.CreateDirectory(Path.Combine(Directory, "examples", "starting-trade"));
            Input = Path.Combine(Directory, "examples", "starting-trade", "save.json");
            File.Copy(Path.Combine(repository.FullName, "examples", "starting-trade", "save.json"), Input);
            File.WriteAllText(Path.Combine(Directory, "train.exe"), "fake executable");
            File.WriteAllText(Path.Combine(Directory, "train.pck"), "fake pck");
            File.Copy(typeof(Train.App.Measurement.WorldCounts).Assembly.Location, Path.Combine(Directory, "Train.dll"));
            // Godot 生成的程序集特性包含脚本类型；读取版本时也需要解析这些类型的依赖。
            foreach (var dependency in new[] { "GodotSharp.dll", "Train.Core.dll", "Train.Content.dll", "Train.Scenes.dll",
                         "Train.Core.Contracts.dll", "Train.Content.Contracts.dll", "Train.Scenes.Contracts.dll" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, dependency), Path.Combine(Directory, dependency));
            File.WriteAllText(Path.Combine(checks, "Invoke-HiddenProcess.ps1"), """
                function Invoke-HiddenProcess {
                    param($FilePath, $TimeoutSeconds, $ArgumentList)
                    $index = [Array]::IndexOf($ArgumentList, '--measure-output')
                    $report = $ArgumentList[$index + 1]
                    $ArgumentList | ConvertTo-Json -AsArray | Set-Content -LiteralPath (Join-Path (Split-Path $report -Parent) 'arguments.json')
                    @{ outcome = 'completed'; identity = @{ build_configuration = 'ExportRelease' }; timing = @{ frames = @(1) }; scope = @{ final_performance = $false } } |
                        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $report
                    @{ ExitCode = 0; TimedOut = $false; CleanupSucceeded = $true; DurationMs = 1; Output = '' }
                }
                """);
        }

        public async Task<(int ExitCode, string Output)> Run(params string[] arguments)
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(Directory, "scripts", "measure.ps1"), "-ExecutablePath", Path.Combine(Directory, "train.exe"), "-OutputDirectory", Output }) start.ArgumentList.Add(argument);
            if (!arguments.Contains("-InputPath")) { start.ArgumentList.Add("-InputPath"); start.ArgumentList.Add(Input); }
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            return (process.ExitCode, await stdout + await stderr);
        }

        public string[] Arguments(string run) => JsonNode.Parse(File.ReadAllText(Path.Combine(Output, run, "arguments.json")))!.AsArray().Select(value => value!.GetValue<string>()).ToArray();

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
