using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Recording;

internal sealed record RecordingPackage(RecordingPackage.Manifest Header,
    ImmutableArray<RecordedCommand> Commands, string ContentPayload)
{
    internal sealed record EncodedStart(RecordingPointId Id, SimulationPosition Position,
        CoreRevision Revision, CoreInputId InputIdentity, CoreReferenceManifest References,
        CoreCheckpoint Checkpoint, string Payload);
    internal sealed record EncodedFault(EncodedStart Start, ImmutableArray<RecordedCommand> Commands,
        ImmutableArray<CoreCheckpoint> Checkpoints, CoreFault Fault, CoreReferenceManifest RequiredReferences);
    internal sealed record Manifest(int FormatVersion, RuntimeIdentity Runtime, SimulationPosition EndPosition,
        ImmutableArray<EncodedStart> Starts, ImmutableArray<CoreCheckpoint> Checkpoints,
        ImmutableArray<EncodedFault> FaultSegments, ImmutableArray<RecordingGap> Gaps,
        CoreReferenceManifest RequiredReferences);

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = CreateOptions();

    internal static RecordingPackage Capture(CoreRecordingWindow window, IContentReplayCapture content,
        RuntimeIdentity runtime) => new(new(1, runtime, window.EndPosition,
            window.Starts.Select(EncodeStart).ToImmutableArray(), window.Checkpoints,
            window.FaultSegments.Select(f => new EncodedFault(EncodeStart(f.Start), f.Commands,
                f.Checkpoints, f.Fault, f.RequiredReferences)).ToImmutableArray(), window.Gaps,
            window.RequiredReferences), window.Commands, content.Encode());

    private static EncodedStart EncodeStart(RecordingStart start) => new(start.Id, start.State.Position,
        start.State.Revision, start.State.InputIdentity, start.State.References, start.State.Checkpoint,
        start.State.Encode());

    internal void Write(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "manifest.json", JsonSerializer.Serialize(Header with
                {
                    FaultSegments = Header.FaultSegments.Select(f => f with { Commands = [] }).ToImmutableArray()
                }, Json));
                WriteEntry(zip, "commands.jsonl", EncodeCommands(Commands));
                WriteEntry(zip, "content.payload", ContentPayload);
                for (var i = 0; i < Header.FaultSegments.Length; i++)
                    WriteEntry(zip, $"faults/{i}/commands.jsonl", EncodeCommands(Header.FaultSegments[i].Commands));
            }
            file.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException
            or EncoderFallbackException or InvalidOperationException)
        {
            throw new InvalidDataException("录制快照无法编码。", error);
        }
    }

    internal void Validate(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry(zip, "manifest.json"), Json)
                ?? throw new InvalidDataException("录制清单为空。");
            if (manifest.FormatVersion != 1) throw new InvalidDataException("不支持的录制格式。");
            if (manifest.FaultSegments.IsDefault) throw new InvalidDataException("缺少故障片段。");
            if (zip.Entries.Count != 3 + manifest.FaultSegments.Length)
                throw new InvalidDataException("录制 ZIP 条目不完整或重复。");
            manifest = manifest with
            {
                FaultSegments = manifest.FaultSegments.Select((f, i) =>
                {
                    if (f is null) throw new InvalidDataException("故障片段为空。");
                    if (!f.Commands.IsEmpty) throw new InvalidDataException("故障命令必须来自 JSONL。");
                    return f with { Commands = DecodeCommands(ReadEntry(zip, $"faults/{i}/commands.jsonl")) };
                }).ToImmutableArray()
            };
            var decoded = new RecordingPackage(manifest, DecodeCommands(ReadEntry(zip, "commands.jsonl")),
                ReadEntry(zip, "content.payload"));
            if (JsonSerializer.Serialize(this, Json) != JsonSerializer.Serialize(decoded, Json))
                throw new InvalidDataException("录制 ZIP 读回值与写入快照不一致。");
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or DecoderFallbackException
            or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("录制 ZIP 无法解码。", error);
        }
    }

    private static string EncodeCommands(ImmutableArray<RecordedCommand> commands)
        => string.Concat(commands.Select(c => JsonSerializer.Serialize(c, Json) + "\n"));

    private static ImmutableArray<RecordedCommand> DecodeCommands(string text)
    {
        if (text.Length == 0) return [];
        if (!text.EndsWith('\n')) throw new InvalidDataException("录制命令 JSONL 未完整结束。");
        return text[..^1].Split('\n').Select(line => JsonSerializer.Deserialize<RecordedCommand>(line, Json)
            ?? throw new InvalidDataException("录制命令为空。")).ToImmutableArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Utf8);
        writer.Write(text);
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        var entries = zip.Entries.Where(e => e.FullName == name).ToArray();
        if (entries.Length != 1) throw new InvalidDataException($"录制 ZIP 缺少或重复条目：{name}");
        using var reader = new StreamReader(entries[0].Open(), Utf8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type.Assembly != typeof(CommandDecision).Assembly || !info.Type.IsAbstract) return;
            var variants = info.Type.GetNestedTypes()
                .Where(type => type.IsSealed && info.Type.IsAssignableFrom(type)).ToArray();
            if (variants.Length == 0) return;
            info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "$kind" };
            foreach (var variant in variants)
                info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(variant,
                    JsonNamingPolicy.SnakeCaseLower.ConvertName(variant.Name)));
        });
        return new()
        {
            TypeInfoResolver = resolver,
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
    }
}
