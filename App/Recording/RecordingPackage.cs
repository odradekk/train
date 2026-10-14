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
        RuntimeIdentity runtime) => new(new(2, runtime, window.EndPosition,
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
        var decoded = Read(path);
        if (JsonSerializer.Serialize(this, Json) != JsonSerializer.Serialize(decoded, Json))
            throw new InvalidDataException("录制 ZIP 读回值与写入快照不一致。");
    }

    internal static RecordingPackage Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry(zip, "manifest.json"), Json)
                ?? throw new InvalidDataException("录制清单为空。");
            if (manifest.FormatVersion != 2) throw new InvalidDataException("不支持的录制格式。");
            if (manifest.Runtime is null || manifest.Starts.IsDefault || manifest.Checkpoints.IsDefault
                || manifest.FaultSegments.IsDefault || manifest.Gaps.IsDefault
                || manifest.Starts.Any(s => s is null) || manifest.Checkpoints.Any(c => c is null)
                || manifest.Gaps.Any(g => g is null))
                throw new InvalidDataException("录制清单缺少必要结构。");
            ValidateReferences(manifest.RequiredReferences);
            foreach (var start in manifest.Starts) ValidateStart(start);
            if (zip.Entries.Count != 3 + manifest.FaultSegments.Length)
                throw new InvalidDataException("录制 ZIP 条目不完整或重复。");
            manifest = manifest with
            {
                FaultSegments = manifest.FaultSegments.Select((f, i) =>
                {
                    if (f is null) throw new InvalidDataException("故障片段为空。");
                    if (f.Commands.IsDefault || !f.Commands.IsEmpty)
                        throw new InvalidDataException("故障命令必须来自 JSONL。");
                    if (f.Checkpoints.IsDefault || f.Checkpoints.Any(c => c is null) || f.Fault is null)
                        throw new InvalidDataException("故障片段缺少必要结构。");
                    ValidateStart(f.Start);
                    ValidateReferences(f.RequiredReferences);
                    return f with { Commands = DecodeCommands(ReadEntry(zip, $"faults/{i}/commands.jsonl")) };
                }).ToImmutableArray()
            };
            return new RecordingPackage(manifest, DecodeCommands(ReadEntry(zip, "commands.jsonl")),
                ReadEntry(zip, "content.payload"));
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or DecoderFallbackException
            or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("录制 ZIP 无法解码。", error);
        }
    }

    private static void ValidateStart(EncodedStart start)
    {
        if (start is null || start.Checkpoint is null || start.Payload is null)
            throw new InvalidDataException("录制起点缺少必要结构。");
        ValidateReferences(start.References);
    }

    private static void ValidateReferences(CoreReferenceManifest references)
    {
        if (references is null || references.Designs.IsDefault || references.Templates.IsDefault
            || references.Definitions.IsDefault || references.Inputs.IsDefault
            || references.Templates.Any(t => t is null || t.Plots.IsDefault || t.Plots.Any(p => p is null))
            || references.Definitions.Any(d => d is null))
            throw new InvalidDataException("录制引用清单缺少必要结构。");
    }

    private static string EncodeCommands(ImmutableArray<RecordedCommand> commands)
        => string.Concat(commands.Select(c => JsonSerializer.Serialize(c, Json) + "\n"));

    private static ImmutableArray<RecordedCommand> DecodeCommands(string text)
    {
        if (text.Length == 0) return [];
        if (!text.EndsWith('\n')) throw new InvalidDataException("录制命令 JSONL 未完整结束。");
        return text[..^1].Split('\n').Select(line =>
        {
            var command = JsonSerializer.Deserialize<RecordedCommand>(line, Json)
                ?? throw new InvalidDataException("录制命令为空。");
            if (command.Result is null || command.Result.Decision is null)
                throw new InvalidDataException("录制命令缺少回执。");
            switch (command.Result.Decision)
            {
                case CommandDecision.Accepted accepted when accepted.Created.IsDefault
                    || accepted.Created.Any(c => c is null) || accepted.SettlementCopies.IsDefault
                    || accepted.SettlementCopies.Any(t => t is null || t.Plots.IsDefault || t.Plots.Any(p => p is null)):
                case CommandDecision.Rejected rejected when rejected.Problems.IsDefault
                    || rejected.Problems.Any(p => p is null || p.RelatedIdentities.IsDefault
                        || p.RelatedIdentities.Any(id => id is null)):
                case CommandDecision.Faulted { Fault: null }:
                    throw new InvalidDataException("录制命令回执缺少必要结构。");
            }
            return command;
        }).ToImmutableArray();
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
