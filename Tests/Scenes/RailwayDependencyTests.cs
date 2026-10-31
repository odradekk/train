using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.Tracing;
using System.Numerics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class RailwayDependencyTests
{
    [TestMethod]
    public void LargeDependencyValuesDoNotBorrowLargeSharedPoolBuffers()
    {
        var value = Enumerable.Range(0, 262_144).ToArray();
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(value).Length > 1024 * 1024);
        using var events = new CurrentThreadPoolEvents();
        var control = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        ArrayPool<byte>.Shared.Return(control);
        Assert.IsTrue(events.Rented > 0, "The listener must observe a real large rental.");
        events.Rented = events.Allocated = 0;

        Assert.IsNotEmpty(RailwayDependencies.Values(value));

        Assert.AreEqual(0, events.Rented, "Dependency hashing must not rent a large shared buffer.");
        Assert.AreEqual(0, events.Allocated, "Dependency hashing must not allocate a large shared buffer.");
    }

    [TestMethod]
    public void Dependency_fingerprint_storage_does_not_grow_with_design_size()
    {
        var small = RailwayDependencies.Values(SizedModel(1).Scene);
        var large = RailwayDependencies.Values(SizedModel(1_024).Scene);
        Assert.AreEqual(small.Length, large.Length,
            "Dependency cache keys must not grow with the serialized design.");
    }

    [TestMethod]
    public void LargeDependencyValuesIncludeChangesBeyondTheFirstSerializationBuffer()
    {
        var original = SizedModel(1_024).Scene;
        var equivalent = SizedModel(1_024).Scene;
        var changed = original with
        {
            Voxels = original.Voxels.SetItem(original.Voxels.Length - 1, original.Voxels[^1] with { Missing = true })
        };
        var key = RailwayDependencies.Values(original);
        Assert.AreEqual(key, RailwayDependencies.Values(equivalent));
        Assert.AreNotEqual(key, RailwayDependencies.Values(changed),
            "A difference after the first streamed buffer must invalidate the dependency key.");
    }

    [TestMethod]
    public void Captured_railway_dependency_storage_does_not_grow_with_design_size()
    {
        var small = Capture(Snapshot(model: SizedModel(1)), models: [SceneFixtures.Design]);
        var large = Capture(Snapshot(model: SizedModel(1_024)), models: [SceneFixtures.Design]);
        Assert.AreEqual(small.Key.Length, large.Key.Length,
            "Captured railway keys must not retain the full model and terrain JSON.");
    }

    [TestMethod]
    public void Dependency_values_distinguish_two_nonidentity_pose_rotations()
    {
        var first = new ScenePose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        var second = first with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI) };
        Assert.IsFalse(first.Rotation.IsIdentity || second.Rotation.IsIdentity);
        Assert.AreNotEqual(RailwayDependencies.Values(first), RailwayDependencies.Values(second),
            "The IsIdentity property alone does not identify the rotation of a flatten footprint.");
    }

    [TestMethod]
    public void CollisionVariantsWithNoPropertiesDoNotReuseDependencies()
    {
        var original = Snapshot(voxel: SceneFixtures.Cube with { Collision = new VoxelCollision.None() });
        var changed = Snapshot(voxel: SceneFixtures.Cube with { Collision = new VoxelCollision.FullBox() });
        Assert.IsFalse(Capture(original).Matches(changed, new(changed, RailwayFixtures.Edge), 0));
    }

    [TestMethod]
    public void SameShapedCoreReferenceVariantsInDiagnosticsDoNotReuseDependencies()
    {
        var original = Snapshot(model: Model(new CoreDefinitionReference.Voxel(new("same-id"))));
        var changed = Snapshot(model: Model(new CoreDefinitionReference.Terrain(new("same-id"))));
        Assert.IsFalse(Capture(original, models: [SceneFixtures.Design]).Matches(changed,
            new(changed, RailwayFixtures.Edge), 0));
    }

    [TestMethod]
    public void NestedCoreReferenceValuesAreIncludedInDependencies()
    {
        var original = Snapshot(model: Model(new CoreDefinitionReference.Voxel(new("first"))));
        var changed = Snapshot(model: Model(new CoreDefinitionReference.Voxel(new("second"))));
        Assert.IsFalse(Capture(original, models: [SceneFixtures.Design]).Matches(changed,
            new(changed, RailwayFixtures.Edge), 0));
    }

    [TestMethod]
    public void MissingLookupDiagnosticsRetainNestedReferenceTypes()
    {
        var original = Snapshot(lookup: new ContentLookup<DesignEvaluation>.Missing(
            [Diagnostic(new CoreDefinitionReference.Voxel(new("same-id")))]));
        var changed = Snapshot(lookup: new ContentLookup<DesignEvaluation>.Missing(
            [Diagnostic(new CoreDefinitionReference.Terrain(new("same-id")))]));
        Assert.IsFalse(Capture(original, models: [SceneFixtures.Design]).Matches(changed,
            new(changed, RailwayFixtures.Edge), 0));
    }

    [TestMethod]
    public void EquivalentValuesInNewInstancesReuseDependencies()
    {
        var original = Snapshot(voxel: SceneFixtures.TexturedCube(2),
            model: Model(new CoreDefinitionReference.Voxel(new("same-id"))));
        var equivalent = Snapshot(voxel: SceneFixtures.TexturedCube(2),
            model: Model(new CoreDefinitionReference.Voxel(new("same-id"))));
        Assert.AreNotSame(original.Content, equivalent.Content);
        Assert.AreNotSame(((ContentLookup<DesignEvaluation>.Found)original.Content.FindDesign(SceneFixtures.Design)).Value,
            ((ContentLookup<DesignEvaluation>.Found)equivalent.Content.FindDesign(SceneFixtures.Design)).Value);
        Assert.IsTrue(Capture(original, models: [SceneFixtures.Design]).Matches(equivalent,
            new(equivalent, RailwayFixtures.Edge), 0));
    }

    private static RailwayDependencies Capture(SceneWorldSnapshot snapshot, ImmutableArray<DesignHash> models = default) =>
        RailwayDependencies.Capture(snapshot, new(snapshot, RailwayFixtures.Edge), 0,
            [], [], models.IsDefault ? [] : models, [SceneFixtures.Voxel], false, false);

    private static SceneWorldSnapshot Snapshot(VoxelAppearanceProjection? voxel = null,
        DesignEvaluation.Complete? model = null, ContentLookup<DesignEvaluation>? lookup = null) =>
        RailwayFixtures.Snapshot(4, 4, relief: 0, content: RailwayFixtures.Content(design: model,
            voxels: voxel is null ? null : new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = new ContentLookup<VoxelAppearanceProjection>.Found(voxel) },
            designs: lookup is null ? null : new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [SceneFixtures.Design] = lookup }));

    private static DesignEvaluation.Complete Model(CoreDefinitionReference reference) =>
        SceneFixtures.Evaluation(SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))]), [Diagnostic(reference)]);

    private static DesignEvaluation.Complete SizedModel(int cells) => SceneFixtures.Evaluation(
        SceneFixtures.Projection([.. Enumerable.Range(0, cells).Select(x => SceneFixtures.Cell(new(x, 0, 0)))]));

    private static ContentDiagnostic Diagnostic(CoreDefinitionReference reference) =>
        new("test_diagnostic", ContentDiagnosticSeverity.Warning, "test", new ContentReference.Core(reference),
            new(new("test-package"), "test.json"), "field", null, "test diagnostic");

    private sealed class CurrentThreadPoolEvents : EventListener
    {
        private readonly int thread = Environment.CurrentManagedThreadId;
        private readonly int bytePool = ArrayPool<byte>.Shared.GetHashCode();
        private readonly int charPool = ArrayPool<char>.Shared.GetHashCode();
        internal int Rented;
        internal int Allocated;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource") EnableEvents(source, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (Environment.CurrentManagedThreadId != thread
                || data.EventName is not ("BufferRented" or "BufferAllocated")
                || data.Payload is not { Count: >= 3 }) return;
            var pool = (int)data.Payload[2]!;
            if (pool != bytePool && pool != charPool) return;
            var bytes = (long)(int)data.Payload[1]! * (pool == charPool ? 2 : 1);
            if (bytes < 1024 * 1024) return;
            if (data.EventName == "BufferRented") Rented++;
            else Allocated++;
        }
    }
}
