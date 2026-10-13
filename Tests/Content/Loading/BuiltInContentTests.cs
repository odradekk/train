using System.Buffers.Binary;
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Train.Content.Tests.Loading.WorldContentFixtures;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
[TestCategory("integration")]
public sealed class BuiltInContentTests
{
    [TestMethod]
    public void Builtin_core_loads_valid_running_vehicles_and_template_station_into_world_input()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository != null && !File.Exists(Path.Combine(repository.FullName, "global.json"))) repository = repository.Parent;
        Assert.IsNotNull(repository);
        var builtIn = new C.ContentRoot(Path.Combine(repository.FullName, "builtin", "core"));
        var mods = new C.ContentRoot(Path.Combine(repository.FullName, "TestResults", "builtin-test-mods-" + Guid.NewGuid().ToString("N")));
        var factory = ContentFactory.Create();
        var result = factory.PrepareLoad(new(new(builtIn, mods, null, "0.0.56", []), new C.ContentLoadPurpose.Initial()),
            new DiskFiles(), new PngHeaderInspector());
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Success>(result, result.ToString());
        using var candidate = ((C.ContentResult<C.IContentCandidate>.Success)result).Value;
        Assert.AreEqual(0, candidate.Report.Diagnostics.Length);
        var view = candidate.View;
        var vehicles = new List<K.DesignHash>();
        foreach (var (id, size) in new[] { ("small-locomotive", K.SizeClass.Small),
                     ("medium-locomotive", K.SizeClass.Medium), ("extra-large-locomotive", K.SizeClass.ExtraLarge) })
        {
            var design = Complete(view, id);
            Assert.IsNotNull(design.Core);
            Assert.IsInstanceOfType<K.DesignKind.Vehicle>(design.Core.Design.Kind);
            Assert.AreEqual(size, ((K.DesignKind.Vehicle)design.Core.Design.Kind).Size);
            var capabilities = design.Core.Design.Parts.SelectMany(part => part.Capabilities).ToArray();
            Assert.IsTrue(capabilities.OfType<K.PartCapability.Control>().Any(), id);
            Assert.IsTrue(capabilities.OfType<K.PartCapability.Propulsion>().Any(), id);
            Assert.IsTrue(capabilities.OfType<K.PartCapability.Storage>().Any(), id);
            vehicles.Add(design.Design);
        }
        var station = Complete(view, "small-station");
        Assert.IsNotNull(station.Core);
        Assert.IsTrue(station.Core.Design.Parts.Any(part => part.Definition is K.CoreDefinitionReference.Voxel &&
            part.Capabilities.OfType<K.PartCapability.Platform>().Any()));
        var template = view.Core.SettlementTemplates.Single(item => item.Id.Value == "core:agricultural-town");
        Assert.IsTrue(template.Plots.Any(plot => plot.PrebuiltDesign == station.Design));
        Assert.IsTrue(view.Core.Designs.Single(design => design.Design == station.Design).Parts
            .SelectMany(part => part.Capabilities).OfType<K.PartCapability.Platform>().Any());
        var worldResult = factory.CreateWorldInput(view, new("core:basic-through-world"), 1, "stage1-world-v1");
        Assert.IsInstanceOfType<C.ContentResult<K.WorldGenerationInput>.Success>(worldResult, worldResult.ToString());
        var world = ((C.ContentResult<K.WorldGenerationInput>.Success)worldResult).Value;
        foreach (var vehicle in vehicles) Assert.IsTrue(world.Start.Trains.Any(train => train.Vehicles.Contains(vehicle)));
    }

    private static C.DesignEvaluation.Complete Complete(C.IContentView view, string id)
    {
        var evaluation = Found(view.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:" + id)))).Evaluation;
        Assert.IsInstanceOfType<C.DesignEvaluation.Complete>(evaluation, evaluation.ToString());
        var complete = (C.DesignEvaluation.Complete)evaluation;
        Assert.IsTrue(complete.IsValid, id);
        Assert.AreEqual(0, complete.Diagnostics.Length);
        return complete;
    }

    private sealed class DiskFiles : C.IContentFiles
    {
        public C.ContentIoResult<ImmutableArray<C.ContentFileEntry>> Enumerate(C.ContentPath directory)
        {
            var path = Path.Combine(directory.Root.Key, directory.RelativePath);
            if (!Directory.Exists(path))
                return new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Failure(C.ContentIoFailure.Missing, path);
            return new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success(Directory.EnumerateFileSystemEntries(path)
                .Select(entry => new C.ContentFileEntry(Path.GetFileName(entry),
                    (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ? C.ContentEntryKind.Link :
                    Directory.Exists(entry) ? C.ContentEntryKind.Directory : C.ContentEntryKind.File)).ToImmutableArray());
        }

        public C.ContentIoResult<ImmutableArray<byte>> Read(C.ContentPath file, long maximumBytes)
        {
            var path = Path.Combine(file.Root.Key, file.RelativePath);
            if (!File.Exists(path))
                return new C.ContentIoResult<ImmutableArray<byte>>.Failure(C.ContentIoFailure.Missing, path);
            if (new FileInfo(path).Length > maximumBytes)
                return new C.ContentIoResult<ImmutableArray<byte>>.Failure(C.ContentIoFailure.LimitExceeded, path);
            return new C.ContentIoResult<ImmutableArray<byte>>.Success(File.ReadAllBytes(path).ToImmutableArray());
        }
    }

    // 只提供 PNG 头中的尺寸事实；本测试不证明 Godot 能解码这些资产。
    private sealed class PngHeaderInspector : C.IAssetInspector
    {
        public C.AssetInspection Inspect(C.AssetKind kind, C.AssetRevision revision, ImmutableArray<byte> bytes)
        {
            Assert.AreEqual(C.AssetKind.Png, kind);
            Assert.IsTrue(bytes.Length >= 24);
            Assert.IsTrue(bytes.AsSpan()[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            return new C.AssetInspection.Success(new C.AssetMetadata.Png(
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan().Slice(16, 4)),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan().Slice(20, 4))));
        }
    }
}
