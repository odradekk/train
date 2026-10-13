using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
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
            Assert.AreEqual(1, header.GetProperty("FormatVersion").GetInt32());
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
            zip => Replace(zip, "manifest.json", text => text.Replace("\"FormatVersion\":1", "\"FormatVersion\":2")),
            zip => Replace(zip, "manifest.json", text => text.Replace("hash-unmodified", "different-hash")),
            zip => Replace(zip, "manifest.json", text => text.Replace("fault-only", "lost-reference")),
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

    private static RecordingPackage Package()
    {
        CoreReferenceManifest references = new([new("design")],
            [new(new(4), new("template"), [new(7, PlotSize.Station)])],
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
