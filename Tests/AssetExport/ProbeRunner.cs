using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Engine.Tests.Assets;
using FileAccess = System.IO.FileAccess;

namespace Train.AssetExport;

public partial class ProbeRunner : Node
{
    public override void _Ready()
    {
        try
        {
            var arguments = OS.GetCmdlineUserArgs();
            string Option(string name)
            {
                var index = Array.IndexOf(arguments, name);
                Require(index >= 0 && index + 1 < arguments.Length, $"Missing argument {name}.");
                return arguments[index + 1];
            }
            var data = Option("--data-root");
            Require(Path.IsPathFullyQualified(data), "Probe data directory must be absolute.");
            if (arguments.Contains("--prepare")) Prepare(data);
            else Run(data, Option("--game-pck"));
            RecordIsolation(data, arguments.Contains("--prepare") ? "prepare" : "runtime");
            GD.Print("asset_export_probe_passed");
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PrintErr(error);
            GetTree().Quit(1);
        }
    }

    private static void Prepare(string data)
    {
        var assets = ProjectSettings.GlobalizePath("res://assets");
        var mods = Path.Combine(data, "mods");
        Directory.CreateDirectory(assets);
        Directory.CreateDirectory(mods);
        using var image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
        image.Fill(Colors.Red);
        var png = image.SavePngToBuffer();
        var glb = GlbFixture.Create(transformed: true, png: png).ToArray();
        foreach (var (name, bytes) in new[] { ("face.png", png), ("model.glb", glb) })
        {
            System.IO.File.WriteAllBytes(Path.Combine(assets, name), bytes);
            System.IO.File.WriteAllText(Path.Combine(assets, name + ".import"), "[remap]\nimporter=\"keep\"\n");
            System.IO.File.WriteAllBytes(Path.Combine(mods, name), bytes);
        }
        System.IO.File.WriteAllBytes(Path.Combine(mods, "broken.png"), [137, 80, 78, 71, 13, 10, 26, 10]);
        System.IO.File.WriteAllBytes(Path.Combine(mods, "broken.glb"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(assets, "nested"));
        System.IO.File.WriteAllBytes(Path.Combine(assets, "nested", "child.png"), png);
        System.IO.File.WriteAllText(Path.Combine(assets, "nested", "child.png.import"), "[remap]\nimporter=\"keep\"\n");
    }

    private static void Run(string data, string gamePck)
    {
        Require(Path.IsPathFullyQualified(gamePck), "Game PCK path must be absolute.");
        Require(ProjectSettings.LoadResourcePack(gamePck, replaceFiles: false), "Cannot load actual game PCK.");
        using var dispatcher = new GodotMainThreadDispatcher();
        var packed = new ContentRoot("packed");
        var mod = new ContentRoot("mod");
        var production = new ContentRoot("production");
        var modDirectory = Path.Combine(data, "mods");
        IContentFiles files = new ContentFiles(dispatcher, new Dictionary<ContentRoot, ContentRootLocation>
        {
            [packed] = new ContentRootLocation.Resource("res://assets"),
            [mod] = new ContentRootLocation.Disk(modDirectory),
            [production] = new ContentRootLocation.Resource("res://builtin/legacy-host/resources/voxels/grain")
        });
        var inspector = new GodotAssetInspector(dispatcher);
        using var cache = new AssetResourceCache(dispatcher);
        foreach (var root in new[] { packed, mod })
        {
            var entries = Success(files.Enumerate(new(root, "")));
            if (root == packed)
                Require(entries.Any(entry => entry.Name == "nested" && entry.Kind == ContentEntryKind.Directory)
                    && entries.All(entry => entry.Name != "child.png"), "PCK enumeration must contain only direct children.");
            foreach (var (name, kind) in new[] { ("face.png", AssetKind.Png), ("model.glb", AssetKind.Glb) })
            {
                Require(entries.Any(entry => entry.Name == name && entry.Kind == ContentEntryKind.File),
                    $"{root.Key} does not enumerate original name {name}.");
                var bytes = Success(files.Read(new(root, name), 1024 * 1024));
                Require(bytes.AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(Path.Combine(modDirectory, name))),
                    $"{root.Key}/{name} raw bytes differ from source fixture.");
                var retained = bytes;
                IoFailure(files.Read(new(root, name), bytes.Length - 1), ContentIoFailure.LimitExceeded);
                Require(retained.AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(Path.Combine(modDirectory, name))),
                    "Returned bytes changed after another request.");
                var revision = new AssetRevision(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan())).ToLowerInvariant());
                var inspection = inspector.Inspect(kind, revision, bytes);
                Require(inspection is AssetInspection.Success, $"Actual exported decode failed: {inspection}.");
                var asset = new ContentAsset(revision, bytes, ((AssetInspection.Success)inspection).Metadata, []);
                if (kind == AssetKind.Png)
                {
                    Require(asset.Metadata is AssetMetadata.Png { Width: 4, Height: 4 }, "PNG dimensions differ.");
                    using var texture = cache.AcquireFaceTexture(asset);
                    using var decoded = texture.Value.GetImage();
                    Require(decoded.HasMipmaps(), "Exported face texture has no mipmaps.");
                }
                else
                {
                    using var model = cache.AcquireModel(asset);
                    Require(model.Value.Mesh.GetSurfaceCount() == 3 && model.Value.Surfaces[1].BaseColorTexture is not null,
                        "Exported GLB mesh/material texture was not created.");
                }
            }
            IoFailure(files.Read(new(root, "missing.png"), 100), ContentIoFailure.Missing);
        }
        var original = Success(files.Read(new(production, "placeholder.png"), 1024 * 1024));
        Require(original.AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(Path.Combine(data, "production-source.png"))),
            "Actual game PCK placeholder differs from current source PNG.");
        Require(Success(files.Enumerate(new(production, ""))).Any(entry => entry.Name == "placeholder.png"
            && entry.Kind == ContentEntryKind.File), "Actual game PCK did not retain original PNG name.");
        CheckPackedSourceContents("res://");
        foreach (var weight in new[] { "Regular", "Bold" })
        {
            using var font = GD.Load<Font>($"res://Ui/Fonts/NotoSansSC-{weight}.otf");
            Require(font is not null && font.GetFontName() == "Noto Sans SC",
                $"Actual game PCK is missing the bundled {weight} font.");
        }
        Require(Godot.FileAccess.FileExists("res://Ui/Fonts/OFL.txt"), "Actual game PCK is missing the font license.");
        foreach (var name in new[] { "lair", "train-running", "train-servicing", "train-waiting", "warning",
            "train-combat", "train-retreating", "notification-info", "notification-complete", "notification-critical" })
        {
            using var texture = GD.Load<Texture2D>($"res://Ui/Symbols/{name}.png");
            Require(texture is not null, $"Actual game PCK is missing the host symbol {name}.");
        }
        foreach (var (name, kind) in new[] { ("broken.png", AssetKind.Png), ("broken.glb", AssetKind.Glb) })
        {
            var result = inspector.Inspect(kind, new(name), Success(files.Read(new(mod, name), 100)));
            Require(result is AssetInspection.Failure { Kind: AssetInspectionFailure.CannotDecode },
                $"Corrupt exported {kind} returned wrong failure: {result}.");
        }
        using (var locked = new FileStream(Path.Combine(modDirectory, "face.png"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            IoFailure(files.Read(new(mod, "face.png"), 1024 * 1024), ContentIoFailure.Inaccessible);
        CheckJunction(files, mod, modDirectory);
    }

    private static void CheckJunction(IContentFiles files, ContentRoot root, string directory)
    {
        var link = Path.Combine(directory, "linked");
        var target = Directory.CreateDirectory(Path.Combine(directory, "target")).FullName;
        System.IO.File.WriteAllBytes(Path.Combine(target, "face.png"), [1]);
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Require(process.WaitForExit(10000) && process.ExitCode == 0, $"Cannot create actual junction: {output} {error}");
        try
        {
            Require(Success(files.Enumerate(new(root, ""))).Any(entry => entry.Name == "linked"
                && entry.Kind == ContentEntryKind.Link), "Junction was not enumerated as Link.");
            IoFailure(files.Read(new(root, "linked/face.png"), 100), ContentIoFailure.LinkedPath);
        }
        finally { Directory.Delete(link); }
    }

    private static void CheckPackedSourceContents(string path)
    {
        using var directory = DirAccess.Open(path);
        Require(directory is not null, $"Cannot enumerate packed directory {path}.");
        directory!.IncludeHidden = true;
        foreach (var file in directory!.GetFiles())
        {
            Require(file is not ".mcp.json" and not "global.json", $"Current PCK contains a development file: {path}{file}");
            Require(!file.EndsWith(".blend", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".blend1", StringComparison.OrdinalIgnoreCase), $"Current PCK contains a source file (no source-file fixture is supplied): {path}{file}");
        }
        foreach (var child in directory.GetDirectories())
        {
            Require(path != "res://examples/" || child == "basic-through",
                $"Current PCK contains a development example: {path}{child}");
            Require(!(path == "res://" && child == "addons") && !(path == "res://App/" && child == "ArtCapture"),
                $"Current PCK contains a development directory: {path}{child}");
            Require(!child.Equals("art-source", StringComparison.OrdinalIgnoreCase), $"Current PCK contains a source directory (no source-file fixture is supplied): {path}{child}");
            CheckPackedSourceContents(path == "res://" ? path + child + "/" : path.TrimEnd('/') + "/" + child + "/");
        }
    }

    private static void RecordIsolation(string data, string phase)
    {
        var allowed = new[] { "Train.AssetExport", "Train.Content.Contracts", "Train.Core.Contracts" };
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => new
        {
            name = assembly.GetName().Name!, location = assembly.IsDynamic ? "dynamic" : assembly.Location
        }).OrderBy(assembly => assembly.name).ToArray();
        System.IO.File.WriteAllText(Path.Combine(data, phase + "-assemblies.json"), JsonSerializer.Serialize(assemblies,
            new JsonSerializerOptions { WriteIndented = true }));
        foreach (var assembly in assemblies.Where(assembly => assembly.name.StartsWith("Train", StringComparison.Ordinal)))
            Require(allowed.Contains(assembly.name), $"Implementation assembly was loaded: {assembly.name}.");
        var executableDirectory = Path.GetDirectoryName(OS.GetExecutablePath())!;
        if (phase == "runtime")
            foreach (var dll in Directory.EnumerateFiles(executableDirectory, "Train*.dll", SearchOption.AllDirectories))
                Require(allowed.Contains(AssemblyName.GetAssemblyName(dll).Name!), $"Implementation DLL was exported: {dll}.");
        var evidence = Path.Combine(data, "build-inputs");
        var original = System.IO.File.ReadAllText(Path.Combine(data, "source-root.txt")).Trim();
        var staging = Path.GetFullPath(Path.Combine(data, "..", "staging"));
        foreach (var project in allowed)
        {
            var inputs = System.IO.File.ReadAllLines(Path.Combine(evidence, project + ".compile.txt"));
            Require(inputs.Length > 0, $"No actual compilation inputs recorded for {project}.");
            var sourceDirectory = Path.GetFullPath(Path.Combine(original, project == "Train.AssetExport" ? "App/Assets"
                : project == "Train.Core.Contracts" ? "Contracts/Core" : "Contracts/Content"));
            Require(inputs.Any(input => input.StartsWith(sourceDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)), $"Actual compilation did not link original sources for {project}.");
            foreach (var input in inputs.Where(input => input.StartsWith(original + Path.DirectorySeparatorChar,
                         StringComparison.OrdinalIgnoreCase) && !input.StartsWith(staging + Path.DirectorySeparatorChar,
                         StringComparison.OrdinalIgnoreCase)))
                Require(input.StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || project == "Train.AssetExport" && input.Equals(Path.GetFullPath(Path.Combine(original, "Tests/Engine/Assets/GlbFixture.cs")),
                        StringComparison.OrdinalIgnoreCase), $"Unexpected original implementation source compiled: {input}.");
            foreach (var reference in System.IO.File.ReadAllLines(Path.Combine(evidence, project + ".references.txt")))
            {
                var name = Path.GetFileNameWithoutExtension(reference);
                if (name.StartsWith("Train", StringComparison.Ordinal))
                    Require(allowed.Contains(name), $"Implementation reference reached isolated compilation: {reference}.");
            }
        }
    }

    private static T Success<T>(ContentIoResult<T> result) where T : notnull
    {
        Require(result is ContentIoResult<T>.Success, $"IO failed: {result}.");
        return ((ContentIoResult<T>.Success)result).Value;
    }
    private static void IoFailure<T>(ContentIoResult<T> result, ContentIoFailure kind) where T : notnull =>
        Require(result is ContentIoResult<T>.Failure failure && failure.Kind == kind, $"Expected {kind}, got {result}.");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
