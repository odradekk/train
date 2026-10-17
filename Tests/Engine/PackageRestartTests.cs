using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.App.Authoring;
using Train.Content.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class PackageRestartTests(Node testScene) : TestClass(testScene)
{
    private const string ModId = "11111111-1111-4111-8111-111111111111";
    private static readonly DraftId SavedDraftId = new(Guid.Parse("33333333-3333-4333-8333-333333333333"));
    private const string Definition = "resources/voxels/block.voxel.json";
    private const string NewIcon = "resources/voxels/block/z.png";

    [Test]
    public async Task Interrupted_real_package_patch_is_diagnosed_by_another_process_without_recovery()
    {
        var report = System.Environment.GetEnvironmentVariable("TRAIN_ENGINE_REPORT");
        var evidenceRoot = string.IsNullOrEmpty(report) ? ProjectSettings.GlobalizePath("res://TestResults") : Path.GetDirectoryName(Path.GetFullPath(report))!;
        var run = Path.Combine(evidenceRoot, "package-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        using (var writer = StartChild("write", run))
        {
            var errors = writer.StandardError.ReadToEndAsync();
            var output = new StringBuilder();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                string? line;
                do
                {
                    line = await writer.StandardOutput.ReadLineAsync(timeout.Token);
                    output.AppendLine(line);
                    Require(line is not null, "写入子进程在确认第一项前退出。");
                } while (line != "package-write-completed:1");
            }
            finally
            {
                if (!writer.HasExited) writer.Kill(entireProcessTree: true);
                await writer.WaitForExitAsync();
                output.Append(await writer.StandardOutput.ReadToEndAsync());
                File.WriteAllText(Path.Combine(run, "write.stdout.log"), output.ToString());
                File.WriteAllText(Path.Combine(run, "write.stderr.log"), await errors);
            }
        }
        var packageDirectory = PackageDirectory(run);
        var interrupted = File.ReadAllBytes(Path.Combine(packageDirectory, Definition));
        var draftBytes = File.ReadAllBytes(DraftPath(run));
        var draft = Success(Train.Content.ContentFactory.Create().DecodeDraft(Encoding.UTF8.GetString(draftBytes)));
        Require(draft.Id == SavedDraftId && draft.Revision == new EditRevision(7)
            && draft.Target is DraftTarget.PackageDefinition { Package.Value: ModId, Kind: DefinitionKind.Voxel, LocalId: "block" }
            && draft.Payload is DraftPayload.Definition { Value: DefinitionDraft.Voxel savedVoxel }
            && savedVoxel.Value.Icon == new AuthoringAssetPath("z.png"),
            "中断前保存的真实草稿未保留包目标、版本或编辑载荷。");
        Require(Encoding.UTF8.GetString(interrupted).Contains("z.png", StringComparison.Ordinal), "确认写入的真实定义没有引用计划中的新资产。");
        Require(!File.Exists(Path.Combine(packageDirectory, NewIcon)), "未确认的新资产被写入。");
        using (var loader = StartChild("load", run))
        {
            var output = loader.StandardOutput.ReadToEndAsync();
            var errors = loader.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await loader.WaitForExitAsync(timeout.Token); }
            finally
            {
                if (!loader.HasExited) { loader.Kill(entireProcessTree: true); await loader.WaitForExitAsync(); }
                File.WriteAllText(Path.Combine(run, "load.stdout.log"), await output);
                File.WriteAllText(Path.Combine(run, "load.stderr.log"), await errors);
            }
            Require(loader.ExitCode == 0, $"普通加载子进程失败；原始日志：{run}");
            Require((await output).Contains("package-load-diagnostic:file_missing", StringComparison.Ordinal), "真实普通加载没有输出缺失资产诊断。");
        }
        Require(interrupted.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(packageDirectory, Definition))), "普通加载自动覆盖了部分写入定义。");
        Require(!File.Exists(Path.Combine(packageDirectory, NewIcon)), "普通加载自动补写了未完成资产。");
        Require(draftBytes.AsSpan().SequenceEqual(File.ReadAllBytes(DraftPath(run))), "普通加载改变了独立保存的真实创作草稿。");
        GD.Print($"Package restart evidence: {run}");
    }

    private static Process StartChild(string mode, string run)
    {
        var start = new ProcessStartInfo(System.Environment.GetEnvironmentVariable("GODOT") ?? OS.GetExecutablePath())
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "--headless", "--path", ProjectSettings.GlobalizePath("res://"),
            "--log-file", Path.Combine(run, mode + ".godot.log"), "res://Tests/Engine/Bootstrap/Runner.tscn",
            "--", "--package-restart-child", mode, run }) start.ArgumentList.Add(argument);
        start.Environment["TRAIN_ENGINE_TEST_ASSEMBLY"] = typeof(PackageRestartTests).Assembly.Location;
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "APPDATA", "LOCALAPPDATA" }
            : new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME" })
        {
            var directory = Path.Combine(run, "user", name);
            Directory.CreateDirectory(directory);
            start.Environment[name] = directory;
        }
        return Process.Start(start)!;
    }

    private static string PackageDirectory(string run) => Path.Combine(run, "user", "mods", "restart-mod");

    private static string DraftPath(string run) => Path.Combine(run, "user", "authoring-drafts", "packages", ModId, SavedDraftId.Value.ToString("D") + ".json");

    public static Task<int> Run(Node runner)
    {
        var args = OS.GetCmdlineUserArgs();
        var mode = args[1];
        var run = args[2];
        var packageDirectory = PackageDirectory(run);
        var builtin = new ContentRoot("builtin");
        var mods = new ContentRoot("mods");
        var files = new RestartFiles(builtin, mods, Path.Combine(run, "empty-builtin"), Path.Combine(run, "user", "mods"));
        var installation = new ContentInstallation(builtin, mods, null, "0.2.0", [new(ModId)]);
        var factory = Train.Content.ContentFactory.Create();
        using var dispatcher = new GodotMainThreadDispatcher();
        var inspector = new GodotAssetInspector(dispatcher);
        if (mode == "write")
        {
            Directory.CreateDirectory(Path.Combine(run, "empty-builtin", "resources"));
            Directory.CreateDirectory(Path.Combine(packageDirectory, "resources/voxels/block"));
            File.WriteAllText(Path.Combine(packageDirectory, "mod.json"), $$"""{"schema_version":2,"id":"{{ModId}}","name":"Restart test","version":"1.0.0","authors":["test"],"dependencies":[]}""");
            File.WriteAllText(Path.Combine(packageDirectory, Definition), """
                {"schema_version":3,"id":"block","simulation":{"value":20,"mass":2,"max_durability":100,"tags":["raw_good"]},"appearance":{"allowed_orientations":"four_way","presentation":{"kind":"cube","faces":{"top":"a.png","side":"a.png","bottom":"a.png"}}},"display":{"name":"Block","description":"Restart test","icon":"a.png"}}
                """);
            using var image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
            image.Fill(Colors.White);
            var png = image.SavePngToBuffer().ToImmutableArray();
            File.WriteAllBytes(Path.Combine(packageDirectory, "resources/voxels/block/a.png"), png.ToArray());
            using var initial = Success(factory.PrepareLoad(new(installation, new ContentLoadPurpose.Initial()), files, inspector));
            Require(initial.Report.Packages.Single(p => p.Metadata?.Id.Value == ModId).State == PackageState.Available &&
                !initial.Report.Diagnostics.Any(d => d.Severity == ContentDiagnosticSeverity.Error), "中断前的真实 Mod 不是有效内容。");
            var snapshot = PackageSnapshot.Capture(packageDirectory);
            var package = Success(factory.OpenAuthoringPackage(new("package"), new(ModId), snapshot.Version, snapshot));
            var voxel = (DefinitionDraft.Voxel)((ContentLookup<DefinitionDraft>.Found)package.Read(new(DefinitionKind.Voxel, "block"))).Value;
            var changed = voxel with { Value = voxel.Value with { Icon = new("z.png") } };
            var savedDraft = new SavedContentDraft(SavedDraftId, new(7),
                new DraftTarget.PackageDefinition(new(ModId), DefinitionKind.Voxel, "block", package.SourceVersion),
                new DraftPayload.Definition(changed));
            Directory.CreateDirectory(Path.GetDirectoryName(DraftPath(run))!);
            File.WriteAllText(DraftPath(run), Success(factory.EncodeDraft(savedDraft)));
            var plan = Success(factory.PreparePackageWrite(package, [new PackageEdit.WriteDefinition(changed), new PackageEdit.WriteAsset(NewIcon, png)]));
            Require(plan.Changes.Length == 2 && plan.Changes[0].RelativePath == Definition && plan.Changes[1].RelativePath == NewIcon,
                "真实补丁顺序不符合中断场景。");
            var count = 0;
            var result = PackageWriter.Execute(snapshot, plan, false, _ =>
            {
                if (++count != 1) return;
                System.Console.WriteLine("package-write-completed:1");
                System.Console.Out.Flush();
                System.Console.ReadLine();
                throw new InvalidOperationException("父进程应终止阻塞的写入子进程。");
            });
            return Task.FromResult(result.Success ? 0 : 1);
        }
        Require(mode == "load", "未知中断测试子进程模式。");
        using var loaded = Success(factory.PrepareLoad(new(installation, new ContentLoadPurpose.Initial()), files, inspector));
        var diagnostics = loaded.Report.Diagnostics;
        Require(diagnostics.Any(d => d.Code == "file_missing" && d.Source?.Package.Value == ModId
            && d.Source.Path.EndsWith(NewIcon, StringComparison.Ordinal) && d.Field == "/display/icon"), "真实 Content 普通加载未诊断缺失的新资产。");
        foreach (var diagnostic in diagnostics)
            System.Console.WriteLine($"package-load-diagnostic:{diagnostic.Code}|{diagnostic.Source?.Path}|{diagnostic.Message}");
        System.Console.Out.Flush();
        return Task.FromResult(0);
    }

    private static T Success<T>(ContentResult<T> result) where T : notnull => result is ContentResult<T>.Success success
        ? success.Value : throw new InvalidOperationException($"真实 Content 调用失败：{result}");

    private sealed class RestartFiles(ContentRoot builtin, ContentRoot mods, string builtinDirectory, string modsDirectory) : IContentFiles
    {
        private IContentFiles Files(ContentRoot root) => new DiskContentFiles(root == builtin ? builtinDirectory
            : root == mods ? modsDirectory : throw new InvalidOperationException("未知内容根。"));
        public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory) => Files(directory.Root).Enumerate(directory);
        public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes) => Files(file.Root).Read(file, maximumBytes);
    }
}

