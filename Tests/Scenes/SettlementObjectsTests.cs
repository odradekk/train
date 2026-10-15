using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class SettlementObjectsTests
{
    [TestMethod]
    public void PlatformNumbersUseCoreCapacityAndReportMismatch()
    {
        var evaluation = NodeFixtures.StationEvaluation();
        evaluation = evaluation with { Scene = evaluation.Scene with
        { Elements = evaluation.Scene.Elements.Reverse().ToImmutableArray(), Voxels = evaluation.Scene.Voxels.Reverse().ToImmutableArray() } };
        var snapshot = Snapshot(content: NodeFixtures.Content(designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
        { [NodeFixtures.StationDesign] = new ContentLookup<DesignEvaluation>.Found(evaluation) }));
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var platforms = SettlementObjects.Platforms(snapshot, layout, problems);
        CollectionAssert.AreEqual(new[] { -40.0, 40.0 }, platforms.Select(stop => stop.LocalZ).ToArray());
        Assert.AreEqual(0, problems.Count);
        var mismatch = Snapshot(node: NodeFixtures.Node() with { Platforms = 1 });
        Assert.IsTrue(SettlementObjects.Platforms(mismatch, layout, problems).IsEmpty);
        Assert.AreEqual("station_platform_mismatch", problems.Single().Code);
        Assert.AreEqual(new WorldObjectId.Node(NodeFixtures.NodeId), problems.Single().Subject);
    }

    [TestMethod]
    public async Task BuildingReconciliationKeepsIdentityAndRejectsLateWork()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builds = 0;
        await using var objects = new SettlementObjects(StableIds(), (content, instance) =>
        {
            if (Interlocked.Increment(ref builds) == 2)
            {
                entered.SetResult();
                release.Wait();
            }
            return WorldSceneSession.BuildObject(content, instance, SceneFixtures.Identity);
        });
        try
        {
            var snapshot = Snapshot();
            var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
            var loading = Present(objects, snapshot, layout).Objects.Single();
            var first = await Settle(objects, snapshot, layout);
            Assert.AreEqual(loading.Id, first.Objects.Single(item => item.Parent is null).Id);
            var repeated = Present(objects, snapshot, layout);
            Assert.AreEqual(1, builds);
            CollectionAssert.AreEqual(first.Objects.Select(item => item.Id).ToArray(), repeated.Objects.Select(item => item.Id).ToArray());
            Assert.AreSame(first.Picking.Single().Grid, repeated.Picking.Single().Grid);

            var changedHash = new DesignHash("changed-station");
            var changedDesign = NodeFixtures.StationEvaluation();
            changedDesign = changedDesign with { Design = changedHash, Scene = changedDesign.Scene with { Design = changedHash } };
            var content = NodeFixtures.Content(designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [changedHash] = new ContentLookup<DesignEvaluation>.Found(changedDesign) });
            var changed = Snapshot(content: content, station: NodeFixtures.Station(2) with { Design = changedHash });
            var rebuilding = Present(objects, changed, layout);
            Assert.AreSame(first.Picking.Single().Grid, rebuilding.Picking.Single().Grid);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = Present(objects, snapshot, layout);
            Assert.AreSame(first.Picking.Single().Grid, current.Picking.Single().Grid);
            release.Set();
            await Task.WhenAll(objects.PendingWork).WaitAsync(TimeSpan.FromSeconds(5));
            var afterLate = Present(objects, snapshot, layout);
            Assert.AreEqual(2, builds);
            Assert.AreSame(first.Picking.Single().Grid, afterLate.Picking.Single().Grid);
            CollectionAssert.AreEqual(first.Objects.Select(item => item.Id).ToArray(), afterLate.Objects.Select(item => item.Id).ToArray());
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task ResourceQuantityPreservesCandidateIdentityAndMeshesWithoutChangingLayoutInputs()
    {
        var good = new GoodId.Voxel(SceneFixtures.Voxel);
        var template = NodeFixtures.Layout with { SceneModel = NodeFixtures.Layout.SceneModel with
        { Scene = SceneFixtures.Projection([]) with { OccupiedBounds = new(new(-500, 0, -500), new(499, 0, 499)) } } };
        var content = Content(template);
        var node = NodeFixtures.Node() with { Resources = [new(good, 100, new ResourceRenewal.Renewable(100, 1))] };
        var full = Snapshot(content: content, node: node);
        var half = Snapshot(content: content, node: node with { Resources = [new(good, 50, new ResourceRenewal.Renewable(100, 1))] });
        var empty = Snapshot(content: content, node: node with { Resources = [new(good, 0, new ResourceRenewal.Renewable(100, 1))] });
        var ids = new Dictionary<string, SceneObjectId>();
        SceneObjectId Id(string key)
        {
            if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = new((ulong)ids.Count + 1));
            return id;
        }
        await using var objects = new SettlementObjects(Id);
        var layout = NodeLayout.Build(full, NodeFixtures.NodeId);
        var before = Present(objects, full, layout).Objects.Where(item => item.Subject is null).ToDictionary(item => item.Id);
        Assert.IsTrue(before.Count > 0);
        var after = Present(objects, half, layout).Objects.Where(item => item.Subject is null).ToArray();
        Assert.IsTrue(after.Length > 0 && after.Length < before.Count);
        foreach (var item in after)
        {
            Assert.AreEqual(before[item.Id].Pose, item.Pose);
            Assert.AreSame(((SceneVisual.Geometry)before[item.Id].Visuals[0]).Mesh, ((SceneVisual.Geometry)item.Visuals[0]).Mesh);
            var point = new ScenePoint(item.Pose.Position.X, item.Pose.Position.Y, item.Pose.Position.Z);
            Assert.IsFalse(layout.ContainsPlot(point));
            Assert.IsTrue(layout.ClearOfTracks(point, 17));
        }
        Assert.IsFalse(Present(objects, empty, layout).Objects.Any(item => item.Subject is null));
        Assert.AreEqual(NodeLayout.ReadInputs(full, NodeFixtures.NodeId), NodeLayout.ReadInputs(half, NodeFixtures.NodeId));
        var fixedOutcrop = Snapshot(content: content, node: node with { Resources = [new(good, 1, new ResourceRenewal.NonRenewable())] });
        CollectionAssert.AreEqual(before.Keys.OrderBy(id => id.Value).ToArray(), Present(objects, fixedOutcrop, layout).Objects
            .Where(item => item.Subject is null).Select(item => item.Id).OrderBy(id => id.Value).ToArray());
    }

    [TestMethod]
    public async Task MissingResourceAppearanceKeepsItsSourceAndAddsNoSubstitute()
    {
        var reference = new ContentReference.Core(new CoreDefinitionReference.Voxel(SceneFixtures.Voxel));
        var diagnostic = new ContentDiagnostic("resource_source_missing", ContentDiagnosticSeverity.Error, "lookup", reference,
            new(new("resource-package"), "resource.json"), "appearance", null, "Resource appearance is unavailable.");
        var snapshot = Snapshot(content: Content(NodeFixtures.Layout, new ContentLookup<VoxelAppearanceProjection>.Missing([diagnostic])),
            node: NodeFixtures.Node() with { Resources = [new(new GoodId.Voxel(SceneFixtures.Voxel), 1, new ResourceRenewal.NonRenewable())] });
        await using var objects = new SettlementObjects(StableIds());
        var rendered = Present(objects, snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));
        Assert.IsFalse(rendered.Objects.Any(item => item.Subject is null));
        var missing = rendered.Diagnostics.Single(problem => problem.Code == "content_missing");
        Assert.AreEqual(reference, missing.Content);
        Assert.AreEqual(diagnostic.Source!.Package, missing.Package);
        Assert.IsTrue(rendered.Diagnostics.Any(problem => problem.Code == diagnostic.Code));
    }

    [TestMethod]
    [DataRow(8, 8)]
    [DataRow(100, 2)]
    public async Task ParkedVehiclesUseSortedIdsTwoLinesAndTheDisplayLimit(int length, int expected)
    {
        var vehicles = Enumerable.Range(1, 10).Reverse().Select(number => new VehicleId((ulong)number)).ToImmutableArray();
        var settlement = NodeFixtures.Settlement() with { ParkedVehicles = vehicles };
        var content = NodeFixtures.Content(design: RailwayFixtures.Vehicle(lengthCells: length),
            voxel: SceneFixtures.Cube with { Behaviors = new(true, null) });
        var snapshot = Snapshot(content: content, settlement: settlement,
            vehicles: vehicles.Select(id => RailwayFixtures.Instance(id.Value) with { Location = NodeFixtures.Location }).ToImmutableArray());
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        Assert.AreEqual(2, layout.Parking.Length);
        await using var objects = new SettlementObjects(StableIds());
        var rendered = await Settle(objects, snapshot, layout);
        var roots = rendered.Objects.Where(item => item.Parent is null && item.Subject is WorldObjectId.Instance
            { Id: InstanceId.Vehicle }).OrderBy(item => ((InstanceId.Vehicle)((WorldObjectId.Instance)item.Subject!).Id).Id.Value).ToArray();
        Assert.AreEqual(expected, roots.Length);
        CollectionAssert.AreEqual(Enumerable.Range(1, expected).Select(number => (ulong)number).ToArray(), roots.Select(item =>
            ((InstanceId.Vehicle)((WorldObjectId.Instance)item.Subject!).Id).Id.Value).ToArray());
        var used = new double[2];
        foreach (var root in roots)
        {
            var index = used[0] + length <= layout.Parking[0].Length ? 0 : 1;
            var line = layout.Parking[index];
            var center = RailwayCoordinates.Add(line.Start, line.Forward * (float)(used[index] + length / 2.0));
            Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(center, default), root.Pose.Position) < .001f);
            used[index] += length + 4;
        }
        Assert.IsTrue(rendered.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Wheelset>().Any());
        if (expected == 2) Assert.IsTrue(used.All(value => value > 0));
    }

    private static RenderedDesign Present(SettlementObjects objects, SceneWorldSnapshot snapshot, NodeLayout layout) =>
        objects.Present(snapshot, layout, default, ImmutableArray.CreateBuilder<SceneFog>());

    private static Func<string, SceneObjectId> StableIds()
    {
        var ids = new Dictionary<string, SceneObjectId>();
        return key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = new((ulong)ids.Count + 1));
            return id;
        };
    }

    private static async Task<RenderedDesign> Settle(SettlementObjects objects, SceneWorldSnapshot snapshot, NodeLayout layout)
    {
        for (var pass = 0; pass < 12; pass++)
        {
            var result = Present(objects, snapshot, layout);
            if (objects.PendingWork.Length == 0) return result;
            await Task.WhenAll(objects.PendingWork).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Fail("Settlement object builds did not settle.");
        throw new InvalidOperationException();
    }

    private static SceneWorldSnapshot Snapshot(IContentView? content = null, NodeMapItem? node = null,
        SettlementView? settlement = null, InstanceView? station = null, ImmutableArray<InstanceView> vehicles = default)
    {
        var basis = NodeFixtures.Snapshot(trains: [], content: content);
        node ??= NodeFixtures.Node();
        settlement ??= NodeFixtures.Settlement();
        station ??= NodeFixtures.Station(2);
        var instances = (vehicles.IsDefault ? ImmutableArray<InstanceView>.Empty : vehicles).Add(station);
        return basis with { Core = new FixedCoreReadView
        {
            Position = basis.Core.Position, InputIdentity = basis.Core.InputIdentity,
            World = basis.Core.World with { Nodes = [node], Edges = [], Trains = [] },
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [node.Id] = new Lookup<NodeView>.Found(new(node, [], 80, 0, 80)) },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [settlement.Id] = new Lookup<SettlementView>.Found(settlement) },
            Instances = instances.ToDictionary(instance => instance.Id, instance => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(instance))
        } };
    }

    private static IContentView Content(SettlementLayoutProjection template,
        ContentLookup<VoxelAppearanceProjection>? resource = null) => new FixedContentView
    {
        Generation = new(1), Core = SceneFixtures.Content().Core,
        SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
        { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(template) },
        Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
        { [NodeFixtures.StationDesign] = new ContentLookup<DesignEvaluation>.Found(NodeFixtures.StationEvaluation()) },
        VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
        { [SceneFixtures.Voxel] = resource ?? new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube) },
        Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>>
        { [RailwayFixtures.Terrain] = new ContentLookup<TerrainAppearanceProjection>.Found(RailwayFixtures.TerrainAppearance) },
        RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>>
        { [RailwayFixtures.Rule] = new ContentLookup<RailwayAppearanceProjection>.Found(RailwayFixtures.Rules) }
    };
}
