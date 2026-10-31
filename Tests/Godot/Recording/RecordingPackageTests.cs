using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Recording;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Recording;

[TestClass]
[TestCategory("integration")]
public sealed class RecordingPackageTests
{
    [TestMethod]
    public void Version_one_recording_with_numeric_plot_references_is_rejected()
    {
        WithFile(path =>
        {
            Package().Write(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                Replace(zip, "manifest.json", text =>
                {
                    var manifest = JsonNode.Parse(text)!;
                    manifest["FormatVersion"] = 1;
                    return manifest.ToJsonString();
                });
            var exception = Assert.ThrowsExactly<InvalidDataException>(() => RecordingPackage.Read(path));
            StringAssert.Contains(exception.Message, "不支持的录制格式");
        });
    }

    [TestMethod]
    public void Zip_preserves_opaque_payloads_window_and_complete_command_receipts()
    {
        WithFile(path =>
        {
            var package = Package();
            package.Write(path);
            package.Validate(path);
            using var zip = ZipFile.OpenRead(path);
            Assert.AreEqual(" 内容\r\n🚂\0 opaque ", Read(zip, "content.payload"));
            using var manifest = JsonDocument.Parse(Read(zip, "manifest.json"));
            var header = manifest.RootElement;
            Assert.AreEqual(2, header.GetProperty("FormatVersion").GetInt32());
            Assert.AreEqual("actual-runtime", header.GetProperty("Runtime").GetProperty("DotNetRuntime").GetString());
            Assert.AreEqual(900ul, header.GetProperty("EndPosition").GetProperty("CompletedSteps").GetUInt64());
            Assert.AreEqual(2, header.GetProperty("Starts").GetArrayLength());
            var start = header.GetProperty("Starts")[1];
            Assert.AreEqual(" core second\r\n不是JSON ", start.GetProperty("Payload").GetString());
            Assert.AreEqual(17ul, start.GetProperty("Revision").GetProperty("Value").GetUInt64());
            Assert.AreEqual("hash-unmodified", start.GetProperty("Checkpoint").GetProperty("Sha256").GetString());
            Assert.AreEqual("gap-code", header.GetProperty("Gaps")[0].GetProperty("Code").GetString());
            Assert.AreEqual(JsonValueKind.Null, header.GetProperty("Gaps")[0].GetProperty("Until").ValueKind);
            Assert.AreEqual("field-reference", header.GetProperty("RequiredReferences").GetProperty("Definitions")[0].GetProperty("Field").GetString());
            Assert.AreEqual("fault-only", header.GetProperty("FaultSegments")[0].GetProperty("RequiredReferences").GetProperty("Inputs")[0].GetProperty("Value").GetString());
            var lines = Read(zip, "commands.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.AreEqual(3, lines.Length);
            using var accepted = JsonDocument.Parse(lines[0]);
            var command = accepted.RootElement;
            Assert.AreEqual((int)CommandSource.Console, command.GetProperty("Source").GetInt32());
            Assert.AreEqual(" opaque\ncommand ", command.GetProperty("CanonicalPayload").GetString());
            Assert.AreEqual("before", command.GetProperty("InputBefore").GetProperty("Value").GetString());
            Assert.AreEqual("after", command.GetProperty("InputAfter").GetProperty("Value").GetString());
            var receipt = command.GetProperty("Result");
            Assert.AreEqual(41ul, receipt.GetProperty("Number").GetUInt64());
            Assert.AreEqual(5ul, receipt.GetProperty("Before").GetProperty("Sequence").GetUInt64());
            Assert.AreEqual(6ul, receipt.GetProperty("After").GetProperty("Sequence").GetUInt64());
            var decision = receipt.GetProperty("Decision");
            Assert.AreEqual("accepted", decision.GetProperty("$kind").GetString());
            Assert.AreEqual(81ul, decision.GetProperty("Created")[0].GetProperty("Id").GetProperty("Id").GetProperty("Value").GetUInt64());
            Assert.AreEqual(7, decision.GetProperty("SettlementCopies")[0].GetProperty("Plots")[0].GetProperty("Number").GetInt32());
            using var rejected = JsonDocument.Parse(lines[1]);
            var problem = rejected.RootElement.GetProperty("Result").GetProperty("Decision").GetProperty("Problems")[0];
            Assert.AreEqual("related", problem.GetProperty("RelatedIdentities")[0].GetString());
            Assert.AreEqual("creature", problem.GetProperty("Subject").GetProperty("$kind").GetString());
            using var faulted = JsonDocument.Parse(lines[2]);
            Assert.AreEqual("fault-field", faulted.RootElement.GetProperty("Result").GetProperty("Decision").GetProperty("Fault").GetProperty("Field").GetString());
            Assert.AreEqual(lines[2] + "\n", Read(zip, "faults/0/commands.jsonl"));
        });
    }

    [TestMethod]
    public void Validation_rejects_missing_duplicate_corrupt_version_and_changed_deep_values()
    {
        foreach (var mutation in new Action<ZipArchive>[]
        {
            zip => zip.GetEntry("content.payload")!.Delete(),
            zip => zip.CreateEntry("commands.jsonl"),
            zip => Replace(zip, "manifest.json", text => text.Replace("\"FormatVersion\":2", "\"FormatVersion\":3")),
            zip => Replace(zip, "manifest.json", text => text.Replace("hash-unmodified", "different-hash")),
            zip => Replace(zip, "manifest.json", text => text.Replace("fault-only", "lost-reference")),
            zip => Replace(zip, "manifest.json", text =>
            {
                var manifest = JsonNode.Parse(text)!;
                manifest["FaultSegments"]![0] = null;
                return manifest.ToJsonString();
            }),
            zip => Replace(zip, "commands.jsonl", text => text.Replace("related", "lost-identity")),
            zip => Replace(zip, "commands.jsonl", text => text.Replace("\"$kind\":\"accepted\"", "\"$kind\":\"unknown\"")),
            zip => Replace(zip, "commands.jsonl", text => text.TrimEnd('\n')),
            zip => Replace(zip, "faults/0/commands.jsonl", text => text.Replace("fault-field", "changed-field")),
            zip => Replace(zip, "content.payload", _ => "different opaque content"),
            zip => Replace(zip, "manifest.json", _ => "not JSON")
        })
        {
            WithFile(path =>
            {
                var package = Package();
                package.Write(path);
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) mutation(zip);
                Assert.ThrowsExactly<InvalidDataException>(() => package.Validate(path));
            });
        }
        WithFile(path =>
        {
            File.WriteAllText(path, "not ZIP");
            Assert.ThrowsExactly<InvalidDataException>(() => Package().Validate(path));
        });
    }

    [TestMethod]
    public void Read_returns_package_values_without_reinterpreting_opaque_payloads()
    {
        WithFile(path =>
        {
            var package = Package();
            package.Write(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                Replace(zip, "content.payload", _ => "changed opaque content, not JSON");
            Assert.AreEqual("changed opaque content, not JSON", RecordingPackage.Read(path).ContentPayload);
            Assert.ThrowsExactly<InvalidDataException>(() => package.Validate(path));
        });
    }

    [TestMethod]
    [DataRow(CompressionLevel.NoCompression, false, false)]
    [DataRow(CompressionLevel.Optimal, false, false)]
    [DataRow(CompressionLevel.NoCompression, true, false)]
    [DataRow(CompressionLevel.Optimal, true, false)]
    [DataRow(CompressionLevel.NoCompression, false, true)]
    [DataRow(CompressionLevel.Optimal, false, true)]
    [DataRow(CompressionLevel.NoCompression, true, true)]
    [DataRow(CompressionLevel.Optimal, true, true)]
    public void Read_and_validation_reject_manifest_length_exceeding_available_bytes(
        CompressionLevel compression, bool oversized, bool validate)
    {
        WithFile(path =>
        {
            var package = Package();
            package.Write(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var manifest = Read(zip, "manifest.json");
                zip.GetEntry("manifest.json")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json", compression).Open(),
                    new UTF8Encoding(false));
                writer.Write(manifest);
            }
            package.Validate(path);
            InflateManifestLength(path, oversized);
            Assert.ThrowsExactly<InvalidDataException>(() =>
            {
                if (validate) package.Validate(path);
                else RecordingPackage.Read(path);
            });
        });
    }

    private static void InflateManifestLength(string path, bool oversized)
    {
        var bytes = File.ReadAllBytes(path);
        var end = bytes.Length - 22;
        Assert.AreEqual(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end)));
        var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16)));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10));
        for (var i = 0; i < count; i++)
        {
            Assert.AreEqual(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 32));
            if (Encoding.UTF8.GetString(bytes, offset + 46, nameLength) == "manifest.json")
            {
                var size = bytes.AsSpan(offset + 24, 4);
                BinaryPrimitives.WriteUInt32LittleEndian(size,
                    oversized ? uint.MaxValue : checked(BinaryPrimitives.ReadUInt32LittleEndian(size) + 1));
                File.WriteAllBytes(path, bytes);
                return;
            }
            offset += 46 + nameLength + extraLength + commentLength;
        }
        Assert.Fail("录制 ZIP 缺少 manifest.json 中央目录记录。");
    }

    [TestMethod]
    public void Read_rejects_invalid_container_and_missing_required_structures()
    {
        foreach (var mutation in new Action<ZipArchive>[]
        {
            zip => zip.GetEntry("content.payload")!.Delete(),
            zip => zip.CreateEntry("commands.jsonl"),
            zip => Replace(zip, "manifest.json", _ => "{}"),
            zip => Replace(zip, "manifest.json", _ => "null"),
            zip => Replace(zip, "manifest.json", text => text.Replace("\"FormatVersion\":2", "\"FormatVersion\":3")),
            zip => Replace(zip, "manifest.json", text =>
            {
                var manifest = JsonNode.Parse(text)!;
                manifest["Starts"] = null;
                return manifest.ToJsonString();
            }),
            zip => Replace(zip, "manifest.json", text =>
            {
                var manifest = JsonNode.Parse(text)!;
                manifest["RequiredReferences"]!["Inputs"] = null;
                return manifest.ToJsonString();
            }),
            zip => Replace(zip, "manifest.json", text =>
            {
                var manifest = JsonNode.Parse(text)!;
                manifest["FaultSegments"]![0]!["Checkpoints"] = null;
                return manifest.ToJsonString();
            }),
            zip => Replace(zip, "commands.jsonl", text => text.TrimEnd('\n')),
            zip => Replace(zip, "commands.jsonl", text => text.Replace("\"$kind\":\"accepted\"", "\"$kind\":\"unknown\"")),
            zip => Replace(zip, "manifest.json", _ => "not JSON")
        })
        {
            WithFile(path =>
            {
                Package().Write(path);
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) mutation(zip);
                Assert.ThrowsExactly<InvalidDataException>(() => RecordingPackage.Read(path));
            });
        }
        WithFile(path =>
        {
            File.WriteAllText(path, "not ZIP");
            Assert.ThrowsExactly<InvalidDataException>(() => RecordingPackage.Read(path));
        });
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4095)]
    [DataRow(4096)]
    [DataRow(4097)]
    [DataRow(1024 * 1024)]
    public void Opaque_payloads_and_fault_commands_round_trip_without_changing_values(int length)
    {
        WithFile(path =>
        {
            var source = Package();
            var payload = length == 0 ? string.Empty : new string('x', length) + "🚂𝄞\\\"\r\n\t\0 内容";
            var commands = source.Commands.Select(c => c with { CanonicalPayload = payload }).ToImmutableArray();
            var start = source.Header.Starts[0] with { Payload = payload };
            var fault = source.Header.FaultSegments[0] with { Start = start, Commands = commands };
            var package = source with
            {
                Header = source.Header with { Starts = [start], FaultSegments = [fault] },
                Commands = commands,
                ContentPayload = payload
            };
            package.Write(path);
            package.Validate(path);
            var decoded = RecordingPackage.Read(path);
            Assert.AreEqual(payload, decoded.ContentPayload);
            Assert.AreEqual(payload, decoded.Header.Starts[0].Payload);
            Assert.AreEqual(payload, decoded.Header.FaultSegments[0].Start.Payload);
            CollectionAssert.AreEqual(commands.Select(c => c.CanonicalPayload).ToArray(),
                decoded.Commands.Select(c => c.CanonicalPayload).ToArray());
            CollectionAssert.AreEqual(commands.Select(c => c.CanonicalPayload).ToArray(),
                decoded.Header.FaultSegments[0].Commands.Select(c => c.CanonicalPayload).ToArray());
        });
    }

    [TestMethod]
    public void Validation_rejects_changed_fault_start_payload_even_when_main_start_is_unchanged()
    {
        WithFile(path =>
        {
            var package = Package();
            package.Write(path);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
                Replace(zip, "manifest.json", text =>
                {
                    var manifest = JsonNode.Parse(text)!;
                    manifest["FaultSegments"]![0]!["Start"]!["Payload"] = "changed opaque fault start";
                    return manifest.ToJsonString();
                });
            Assert.ThrowsExactly<InvalidDataException>(() => package.Validate(path));
        });
    }

    internal static RecordingPackage Package()
    {
        CoreReferenceManifest references = new([new("design")],
            [new(new(4), new("template"), [new(7, PlotSize.SmallStation)])],
            [new(new CoreDefinitionReference.Process(new ProcessRef.Recovery(new("recovery"))),
                new("frozen-revision"), "field-reference", true)], [new("input")]);
        var fault = new CoreFault("fault-code", new(8, 9), "operation", "fault-field",
            new WorldObjectId.Instance(new InstanceId.Building(new(91))));
        RecordedCommand Command(ulong number, CommandDecision decision) => new(CommandSource.Console,
            " opaque\ncommand ", new(number, new(20, 5), new(20, 6), decision), new("before"), new("after"));
        RecordedCommand[] commands =
        [
            Command(41, new CommandDecision.Accepted([new WorldObjectId.Instance(new InstanceId.Vehicle(new(81)))], references.Templates)),
            Command(42, new CommandDecision.Rejected([new("reason", "problem-field", new WorldObjectId.Creature(new(72)), ["related"])])),
            Command(43, new CommandDecision.Faulted(fault))
        ];
        var first = new RecordingStart(new(1), new Capture(references, " core first\n "));
        var second = new RecordingStart(new(2), new Capture(references, " core second\r\n不是JSON "));
        var checkpoint = first.State.Checkpoint;
        var window = new CoreRecordingWindow(new(900, 10), [first, second], [.. commands], [checkpoint],
            [new(second, [commands[2]], [checkpoint], fault, references with { Inputs = [new("fault-only")] })],
            [new(new(30, 1), null, "gap-code")], references);
        return RecordingPackage.Capture(window, new Content(),
            new("build", "simulation", "actual-os", "actual-architecture", "actual-runtime"));
    }

    private sealed class Capture(CoreReferenceManifest references, string payload) : ICoreCapture
    {
        public SimulationPosition Position => new(40, 3);
        public CoreRevision Revision => new(17);
        public CoreInputId InputIdentity => new("capture-input");
        public CoreReferenceManifest References => references;
        public CoreCheckpoint Checkpoint => new(Position, " opaque checkpoint ", "hash-unmodified");
        public string Encode() => payload;
    }

    private sealed class Content : IContentReplayCapture
    {
        public ImmutableArray<IContentView> History => throw new AssertFailedException("容器不得读取内容历史。");
        public string Encode() => " 内容\r\n🚂\0 opaque ";
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Replace(ZipArchive zip, string name, Func<string, string> change)
    {
        var text = Read(zip, name);
        zip.GetEntry(name)!.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(change(text));
    }

    private static void WithFile(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"train-recording-{Guid.NewGuid():N}.zip");
        try { action(path); }
        finally { File.Delete(path); }
    }
}
