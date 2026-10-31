using System.Collections.Immutable;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private static readonly JsonSerializerOptions Json = CreateOptions(new JsonStrings());
    private static readonly JsonSerializerOptions DigestJson = CreateOptions(new StringDigest());

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
                using (var entry = zip.CreateEntry("manifest.json").Open())
                using (var writer = new Utf8JsonWriter(entry))
                {
                    JsonSerializer.Serialize(writer, Header with
                    {
                        FaultSegments = Header.FaultSegments.Select(f => f with { Commands = [] }).ToImmutableArray()
                    }, Json);
                }
                WriteCommands(zip, "commands.jsonl", Commands);
                WriteEntry(zip, "content.payload", ContentPayload);
                for (var i = 0; i < Header.FaultSegments.Length; i++)
                    WriteCommands(zip, $"faults/{i}/commands.jsonl", Header.FaultSegments[i].Commands);
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
        if (!Digest(this).AsSpan().SequenceEqual(Digest(decoded)))
            throw new InvalidDataException("录制 ZIP 读回值与写入快照不一致。");
    }

    private static byte[] Digest(RecordingPackage package)
    {
        using var sha = SHA256.Create();
        using var sink = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);
        JsonSerializer.Serialize(sink, package, DigestJson);
        sink.FlushFinalBlock();
        return sha.Hash!;
    }

    // 长字符串转义要按长度 6 倍租缓冲区，所以按字符串哈希。
    private sealed class StringDigest : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteBase64StringValue(SHA256.HashData(MemoryMarshal.AsBytes(value.AsSpan())));
    }

    internal static RecordingPackage Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            // 解转义暂存只属于本次读取，不留在长期使用的序列化选项里。
            var json = CreateOptions(new JsonStrings());
            var manifest = JsonSerializer.Deserialize<Manifest>(ReadBytes(zip, "manifest.json").AsSpan(), json)
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
                    return f with { Commands = ReadCommands(zip, $"faults/{i}/commands.jsonl", json) };
                }).ToImmutableArray()
            };
            return new RecordingPackage(manifest, ReadCommands(zip, "commands.jsonl", json),
                ReadEntry(zip, "content.payload"));
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or DecoderFallbackException
            or ArgumentException or InvalidOperationException or EndOfStreamException)
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

    private static void WriteCommands(ZipArchive zip, string name, ImmutableArray<RecordedCommand> commands)
    {
        using var entry = zip.CreateEntry(name).Open();
        using var writer = new Utf8JsonWriter(entry);
        foreach (var command in commands)
        {
            JsonSerializer.Serialize(writer, command, Json);
            writer.Flush();
            entry.WriteByte((byte)'\n');
            writer.Reset();
        }
    }

    private static ImmutableArray<RecordedCommand> ReadCommands(ZipArchive zip, string name, JsonSerializerOptions json)
    {
        var text = ReadBytes(zip, name).AsSpan();
        if (text.Length == 0) return [];
        if (text[^1] != (byte)'\n') throw new InvalidDataException("录制命令 JSONL 未完整结束。");
        var commands = ImmutableArray.CreateBuilder<RecordedCommand>();
        while (text.Length > 0)
        {
            var length = text.IndexOf((byte)'\n');
            var command = JsonSerializer.Deserialize<RecordedCommand>(text[..length], json)
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
            commands.Add(command);
            text = text[(length + 1)..];
        }
        return commands.ToImmutable();
    }

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Utf8);
        writer.Write(text);
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(Entry(zip, name).Open(), Utf8, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static byte[] ReadBytes(ZipArchive zip, string name)
    {
        var entry = Entry(zip, name);
        if (entry.Length > Array.MaxLength) throw new InvalidDataException("录制 ZIP 条目长度过大。");
        var bytes = new byte[entry.Length];
        using var stream = entry.Open();
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static ZipArchiveEntry Entry(ZipArchive zip, string name)
    {
        var entries = zip.Entries.Where(e => e.FullName == name).ToArray();
        if (entries.Length != 1) throw new InvalidDataException($"录制 ZIP 缺少或重复条目：{name}");
        return entries[0];
    }

    private sealed class JsonStrings : JsonConverter<string>
    {
        private byte[] _unescaped = [];

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (!reader.ValueIsEscaped) return reader.GetString()!;
            if (_unescaped.Length < reader.ValueSpan.Length) _unescaped = new byte[reader.ValueSpan.Length];
            var length = reader.CopyString(_unescaped);
            return Utf8.GetString(_unescaped.AsSpan(0, length));
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            var offset = 0;
            do
            {
                var length = Math.Min(4096, value.Length - offset);
                writer.WriteStringValueSegment(value.AsSpan(offset, length), offset + length == value.Length);
                writer.Flush();
                offset += length;
            } while (offset < value.Length);
        }
    }

    private static JsonSerializerOptions CreateOptions(JsonConverter<string> strings)
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
            Converters = { strings },
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
    }
}
