using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Cli.CliRunner;

namespace Train.Cli;

/// <summary>Measures one group of content operations in the current process; records values only, no budgets.</summary>
internal sealed class ContentMeasurement(ICoreFactory core, IContentFactory content, CliRunner runner)
{
    internal static readonly string[] Groups = ["load", "evaluate", "history", "capture", "restore"];
    private const int WarmSamples = 5;
    private const int HistoryEdits = 4;
    private static readonly string[] LargeDesigns = ["titan-armed-vehicle", "core-city-hub", "bastion-fortress"];
    private readonly JsonArray _operations = [];
    private readonly JsonArray _retained = [];
    private MeasuredFiles _files = null!;
    private readonly MeasuredInspector _inspector = new(new AssetInspector());
    private ContentInstallation _installation = null!;
    private string _repository = "";

    internal JsonObject Measure(string repository, string savePath, string group)
    {
        _repository = Path.GetFullPath(repository);
        _installation = Installation(_repository);
        _files = new(new CliContentFiles(_repository));
        var inputs = Inputs(savePath);
        switch (group)
        {
            case "load": MeasureLoad(); break;
            case "evaluate": MeasureEvaluate(); break;
            case "history": MeasureHistory(savePath); break;
            case "capture": MeasureCapture(savePath); break;
            case "restore": MeasureRestore(savePath); break;
        }
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new()
        {
            ["tool"] = "measure-content",
            ["group"] = group,
            ["environment"] = Platform(),
            ["inputs"] = inputs,
            ["operations"] = _operations,
            ["retained"] = _retained,
            ["process"] = new JsonObject
            {
                ["peakWorkingSetBytes"] = process.PeakWorkingSet64,
                // Windows 上对应 PeakPagefileUsage，即进程私有提交量的峰值。
                ["peakPagedMemoryBytes"] = process.PeakPagedMemorySize64,
                ["workingSetBytes"] = process.WorkingSet64,
                ["privateMemoryBytes"] = process.PrivateMemorySize64,
                ["managedHeapBytes"] = GC.GetTotalMemory(false),
                ["totalAllocatedBytes"] = GC.GetTotalAllocatedBytes(true),
                ["gcCollections"] = new JsonArray(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)),
                ["gcPauseMs"] = Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 3)
            }
        };
    }

    private void MeasureLoad()
    {
        Repeat("Discover", () => Require(content.Discover(_installation, _files)));
        Repeat("PrepareLoad(Initial)", () =>
        {
            using var loaded = runner.Load(_installation, new ContentLoadPurpose.Initial(), _files, _inspector);
            return loaded.View;
        }, view => new() { ["coreInput"] = view.Core.Identity.Value, ["blueprints"] = view.Blueprints.Length });
        Retain("Initial view", LoadInitial);
    }

    private void MeasureEvaluate()
    {
        var view = LoadInitial();
        foreach (var id in LargeDesigns)
        {
            var draft = Found(view.FindBlueprint(new(BlueprintLibrary.Packages, new("core:" + id))), id).Definition;
            Repeat($"EvaluateDesign({id})", () => Evaluate(view, draft, id), evaluation => new()
            {
                ["design"] = evaluation.Design.Value,
                ["composition"] = evaluation.Composition.Value,
                ["ownCells"] = draft.Layout.Cells.Length,
                ["nestedItems"] = draft.Layout.NestedItems.Length,
                ["sceneVoxels"] = evaluation.Scene.Voxels.Length,
                ["elements"] = evaluation.Scene.Elements.Length,
                ["nestedObjects"] = evaluation.Scene.NestedObjects.Length
            });
            Retain($"DesignEvaluation({id})", () => Evaluate(view, draft, id));
        }
    }

    private void MeasureHistory(string savePath)
    {
        Retain("Initial view", LoadInitial);
        var (document, state) = ReadSave(savePath);
        var restored = Retain("Restored view", () => Restore(document, state));
        Repeat("PrepareLoad(Reload, unchanged)", () =>
        {
            using var loaded = runner.Load(_installation, new ContentLoadPurpose.Reload(restored, state.References), _files, _inspector);
            if (loaded.Changes != ContentChange.None || !ReferenceEquals(loaded.View, restored))
                throw new InvalidDataException("无变化重载没有复用基准视图。");
            return loaded.View;
        });
        Reloads(restored, state.References);
        Drafts(restored);
    }

    private void MeasureCapture(string savePath)
    {
        var (document, state) = ReadSave(savePath);
        var restored = Restore(document, state);
        var capture = Repeat("Capture", () => Require(content.Capture(restored, state.References)));
        var payload = Repeat("Encode(capture)", capture.Encode, Payload);
        Repeat("DecodeCapture", () => Require(content.DecodeCapture(payload)));
        var manifest = Manifest(state, restored);
        foreach (var (name, history) in Histories(restored, state.References))
        {
            var replay = Repeat($"CaptureReplay({name})", () => Require(content.CaptureReplay(history, manifest)));
            Repeat($"Encode(replay, {name})", replay.Encode, Payload);
        }
    }

    private void MeasureRestore(string savePath)
    {
        var document = Store(savePath).Read(savePath);
        var state = Repeat("DecodeState (core, not content cost)", () => Require(core.DecodeState(document.CorePayload)));
        var capture = Repeat("DecodeCapture", () => Require(content.DecodeCapture(document.ContentPayload)));
        Repeat("PrepareLoad(Restore)", () =>
        {
            using var loaded = runner.Load(_installation, new ContentLoadPurpose.Restore(capture, state.References), _files, _inspector);
            return loaded.View;
        });
        var restored = Retain("Restored view", () => Restore(document, state));
        foreach (var (name, history) in Histories(restored, state.References))
        {
            var payload = Require(content.CaptureReplay(history, Manifest(state, restored))).Encode();
            Repeat($"DecodeReplay({name})", () => Require(content.DecodeReplay(payload)),
                decoded => new() { ["payloadUtf8Bytes"] = Encoding.UTF8.GetByteCount(payload), ["history"] = decoded.History.Length });
            Retain($"DecodeReplay result ({name})", () => Require(content.DecodeReplay(payload)));
        }
    }

    private IEnumerable<(string Name, ImmutableArray<IContentView> History)> Histories(IContentView restored, CoreReferenceManifest references)
    {
        yield return ("1 view", [restored]);
        yield return ($"restored + {HistoryEdits} draft saves", Drafts(restored));
        yield return ($"restored + {HistoryEdits} effective reloads", Reloads(restored, references));
    }

    // 每次在内存中改同一份体素的显示名，产生只影响表现的有效重载；不改仓库文件。
    private ImmutableArray<IContentView> Reloads(IContentView restored, CoreReferenceManifest references)
    {
        var file = Directory.EnumerateFiles(Path.Combine(_repository, "builtin", "core", "resources", "voxels"), "*.voxel.json")
            .Order(StringComparer.Ordinal).First();
        var path = new ContentPath(CliContentFiles.BuiltIn, "resources/voxels/" + Path.GetFileName(file));
        var original = JsonNode.Parse(File.ReadAllBytes(file))!;
        var name = original["display"]!["name"]!.GetValue<string>();
        var history = ImmutableArray.CreateBuilder<IContentView>();
        history.Add(restored);
        var samples = new JsonArray();
        var retained = new JsonArray();
        var baseline = GC.GetTotalMemory(true);
        for (var edit = 1; edit <= HistoryEdits; edit++)
        {
            var changed = original.DeepClone();
            changed["display"]!["name"] = $"{name} {edit}";
            _files.Overrides[path] = Encoding.UTF8.GetBytes(changed.ToJsonString()).ToImmutableArray();
            var basis = history[^1];
            var (view, sample) = Sample(() =>
            {
                using var loaded = runner.Load(_installation, new ContentLoadPurpose.Reload(basis, references), _files, _inspector);
                if (loaded.Changes != ContentChange.Presentation)
                    throw new InvalidDataException($"有效重载的变化不符：{loaded.Changes}。");
                return loaded.Activate();
            });
            history.Add(view);
            samples.Add(sample);
            retained.Add(GC.GetTotalMemory(true) - baseline);
        }
        _files.Overrides.Clear();
        _operations.Add(new JsonObject { ["name"] = "PrepareLoad(Reload, effective)+Activate", ["modifiedFile"] = path.RelativePath, ["samples"] = samples });
        _retained.Add(new JsonObject { ["name"] = "History growth after each effective reload", ["bytes"] = retained });
        return history.ToImmutable();
    }

    private ImmutableArray<IContentView> Drafts(IContentView restored)
    {
        var design = Found(restored.FindBlueprint(new(BlueprintLibrary.Packages, new("core:" + LargeDesigns[0]))), LargeDesigns[0]).Definition;
        var history = ImmutableArray.CreateBuilder<IContentView>();
        history.Add(restored);
        var samples = new JsonArray();
        var retained = new JsonArray();
        var baseline = GC.GetTotalMemory(true);
        for (var edit = 1; edit <= HistoryEdits; edit++)
        {
            var draft = new SavedContentDraft(new(new Guid(edit, 0, 0, new byte[8])), new((ulong)edit),
                new DraftTarget.LocalBlueprint(null), new DraftPayload.Design(design));
            var basis = history[^1];
            var (view, sample) = Sample(() =>
            {
                using var prepared = Require(content.PrepareEdit(basis, new ContentEdit.SaveLocalDraft(draft)));
                if (prepared.Changes != ContentChange.Persistent)
                    throw new InvalidDataException($"草稿保存的变化不符：{prepared.Changes}。");
                return prepared.Activate();
            });
            history.Add(view);
            samples.Add(sample);
            retained.Add(GC.GetTotalMemory(true) - baseline);
        }
        _operations.Add(new JsonObject { ["name"] = "PrepareEdit(SaveLocalDraft)+Activate", ["draftDesign"] = "core:" + LargeDesigns[0], ["samples"] = samples });
        _retained.Add(new JsonObject { ["name"] = "History growth after each draft save", ["bytes"] = retained });
        return history.ToImmutable();
    }

    private IContentView LoadInitial()
    {
        using var loaded = runner.Load(_installation, new ContentLoadPurpose.Initial(), _files, _inspector);
        return loaded.Activate();
    }

    private IContentView Restore(SaveDocument document, ICoreCapture state)
    {
        var capture = Require(content.DecodeCapture(document.ContentPayload));
        using var loaded = runner.Load(_installation, new ContentLoadPurpose.Restore(capture, state.References), _files, _inspector);
        return loaded.Activate();
    }

    private (SaveDocument Document, ICoreCapture State) ReadSave(string savePath)
    {
        var document = Store(savePath).Read(savePath);
        return (document, Require(core.DecodeState(document.CorePayload)));
    }

    private static CoreReferenceManifest Manifest(ICoreCapture state, IContentView restored) =>
        state.References with { Inputs = [restored.Core.Identity] };

    private DesignEvaluation.Complete Evaluate(IContentView view, DesignDraft draft, string id) =>
        Require(content.EvaluateDesign(view, draft)) is DesignEvaluation.Complete { IsValid: true } complete
            ? complete : throw new InvalidDataException($"{id} 没有得到完整合法的求值结果。");

    private static T Found<T>(ContentLookup<T> lookup, string id) where T : notnull =>
        lookup is ContentLookup<T>.Found found ? found.Value : throw new InvalidDataException($"找不到 {id}。");

    private T Repeat<T>(string name, Func<T> action, Func<T, JsonObject>? describe = null)
    {
        var samples = new JsonArray();
        T result = default!;
        for (var index = 0; index <= WarmSamples; index++)
        {
            result = default!;
            (result, var sample) = Sample(action);
            samples.Add(sample);
        }
        var warm = samples.Skip(1).Select(s => s!["elapsedMs"]!.GetValue<double>()).Order().ToArray();
        var operation = new JsonObject
        {
            ["name"] = name,
            ["samples"] = samples,
            ["warm"] = new JsonObject { ["minMs"] = warm[0], ["medianMs"] = warm[warm.Length / 2], ["maxMs"] = warm[^1] }
        };
        if (describe is not null) operation["details"] = describe(result);
        _operations.Add(operation);
        return result;
    }

    private (T Result, JsonObject Sample) Sample<T>(Func<T> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _files.Reset();
        _inspector.Reset();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var watch = Stopwatch.StartNew();
        var result = action();
        watch.Stop();
        var sample = new JsonObject
        {
            ["elapsedMs"] = Math.Round(watch.Elapsed.TotalMilliseconds, 3),
            ["allocatedBytes"] = GC.GetTotalAllocatedBytes(true) - allocated,
            ["gcCollections"] = new JsonArray(GC.CollectionCount(0) - collections[0],
                GC.CollectionCount(1) - collections[1], GC.CollectionCount(2) - collections[2])
        };
        if (_files.Reads + _files.Enumerations + _inspector.Inspections > 0)
            sample["ports"] = new JsonObject
            {
                ["enumerations"] = _files.Enumerations,
                ["enumerateMs"] = Math.Round(_files.EnumerateTime.Elapsed.TotalMilliseconds, 3),
                ["reads"] = _files.Reads,
                ["readBytes"] = _files.ReadBytes,
                ["readMs"] = Math.Round(_files.ReadTime.Elapsed.TotalMilliseconds, 3),
                ["inspections"] = _inspector.Inspections,
                ["inspectMs"] = Math.Round(_inspector.Time.Elapsed.TotalMilliseconds, 3)
            };
        return (result, sample);
    }

    private T Retain<T>(string name, Func<T> create)
    {
        var before = GC.GetTotalMemory(true);
        var value = create();
        _retained.Add(new JsonObject { ["name"] = name, ["bytes"] = GC.GetTotalMemory(true) - before });
        return value;
    }

    private static JsonObject Payload(string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var document = JsonDocument.Parse(bytes);
        var details = new JsonObject
        {
            ["utf8Bytes"] = bytes.Length,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)),
            ["sections"] = Sections(document.RootElement)
        };
        if (document.RootElement.TryGetProperty("history", out var history))
            details["historyEntries"] = new JsonArray(history.EnumerateArray().Select(entry => (JsonNode)Sections(entry)).ToArray());
        return details;
    }

    private static JsonObject Sections(JsonElement value)
    {
        var sections = new JsonObject();
        foreach (var property in value.EnumerateObject())
            sections[property.Name] = property.Value.ValueKind == JsonValueKind.Array
                ? new JsonObject { ["utf8Bytes"] = Encoding.UTF8.GetByteCount(property.Value.GetRawText()), ["items"] = property.Value.GetArrayLength() }
                : Encoding.UTF8.GetByteCount(property.Value.GetRawText());
        return sections;
    }

    private JsonObject Inputs(string savePath)
    {
        var root = Path.Combine(_repository, "builtin", "core");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => (Path: Path.GetRelativePath(root, file).Replace('\\', '/'), Bytes: File.ReadAllBytes(file)))
            .OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
        var manifest = string.Concat(files.Select(file => $"{file.Path}\t{Convert.ToHexString(SHA256.HashData(file.Bytes))}\n"));
        var kinds = new JsonObject();
        foreach (var kind in files.GroupBy(file => Path.GetExtension(file.Path).ToLowerInvariant()).OrderBy(g => g.Key, StringComparer.Ordinal))
            kinds[kind.Key] = new JsonObject { ["files"] = kind.Count(), ["bytes"] = kind.Sum(file => (long)file.Bytes.Length) };
        var save = File.ReadAllBytes(savePath);
        return new()
        {
            ["gameVersion"] = _installation.GameVersion,
            ["builtIn"] = new JsonObject
            {
                ["files"] = files.Length,
                ["bytes"] = files.Sum(file => (long)file.Bytes.Length),
                ["manifestSha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))),
                ["byExtension"] = kinds
            },
            ["save"] = new JsonObject
            {
                ["path"] = Path.GetFullPath(savePath),
                ["bytes"] = save.Length,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(save))
            },
            ["largeDesigns"] = new JsonArray(LargeDesigns.Select(id => (JsonNode)("core:" + id)).ToArray()),
            ["warmSamples"] = WarmSamples,
            ["historyEdits"] = HistoryEdits
        };
    }

    private JsonObject Platform() => new()
    {
        ["measuredAtUtc"] = DateTimeOffset.UtcNow,
        ["contentBuild"] = content.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
#if DEBUG
        ["configuration"] = "Debug",
#else
        ["configuration"] = "Release",
#endif
        ["os"] = RuntimeInformation.OSDescription,
        ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
        ["processorCount"] = System.Environment.ProcessorCount,
        ["totalAvailableMemoryBytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        ["dotnet"] = RuntimeInformation.FrameworkDescription,
        ["serverGc"] = GCSettings.IsServerGC,
        ["concurrentGc"] = AppContext.GetData("System.GC.Concurrent") as bool? ?? true,
        ["gcLatencyMode"] = GCSettings.LatencyMode.ToString()
    };

    private sealed class MeasuredFiles(IContentFiles inner) : IContentFiles
    {
        internal Dictionary<ContentPath, ImmutableArray<byte>> Overrides { get; } = [];
        internal Stopwatch EnumerateTime { get; } = new();
        internal Stopwatch ReadTime { get; } = new();
        internal int Enumerations { get; private set; }
        internal int Reads { get; private set; }
        internal long ReadBytes { get; private set; }

        internal void Reset()
        {
            EnumerateTime.Reset();
            ReadTime.Reset();
            Enumerations = Reads = 0;
            ReadBytes = 0;
        }

        public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory)
        {
            Enumerations++;
            EnumerateTime.Start();
            try { return inner.Enumerate(directory); }
            finally { EnumerateTime.Stop(); }
        }

        public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
        {
            Reads++;
            ReadTime.Start();
            try
            {
                var result = Overrides.TryGetValue(file, out var bytes)
                    ? new ContentIoResult<ImmutableArray<byte>>.Success(bytes) : inner.Read(file, maximumBytes);
                if (result is ContentIoResult<ImmutableArray<byte>>.Success success) ReadBytes += success.Value.Length;
                return result;
            }
            finally { ReadTime.Stop(); }
        }
    }

    private sealed class MeasuredInspector(IAssetInspector inner) : IAssetInspector
    {
        internal Stopwatch Time { get; } = new();
        internal int Inspections { get; private set; }

        internal void Reset()
        {
            Time.Reset();
            Inspections = 0;
        }

        public AssetInspection Inspect(AssetKind kind, AssetRevision revision, ImmutableArray<byte> bytes)
        {
            Inspections++;
            Time.Start();
            try { return inner.Inspect(kind, revision, bytes); }
            finally { Time.Stop(); }
        }
    }
}
