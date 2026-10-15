using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class SettlementPrebuildTests
{
    [TestMethod]
    public async Task CandidatePumpBuildsBuildingsAndParkedVehiclesBeforePresentAndReusesBodies()
    {
        var snapshot = Snapshot();
        var buildingCache = new SceneVehicles.Cache();
        var parkedCache = new SceneVehicles.Cache();
        await using var vehicles = new SceneVehicles();
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        SceneFrame first;
        await using (var candidate = new NodeScene(snapshot, NodeFixtures.NodeId, new(), Ids(), vehicles,
            RailwayFixtures.EmptyNode, buildingCache, parkedCache))
        {
            await Prepare(candidate);
            first = candidate.Present(snapshot, request, stamp, null, default);
            Assert.AreEqual(SceneReadiness.Ready, first.Readiness,
                "Both bodies must be ready before the candidate's first displayed frame.");
            Assert.IsTrue(first.Objects.Any(item => item.Subject == new WorldObjectId.Instance(new InstanceId.Building(new(1))) && !item.Visuals.IsEmpty));
            Assert.IsTrue(first.Objects.Any(item => item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(new(2))) && !item.Visuals.IsEmpty));
        }
        await using var reopened = new NodeScene(snapshot, NodeFixtures.NodeId, new(), Ids(), vehicles,
            RailwayFixtures.EmptyNode, buildingCache, parkedCache);
        await Prepare(reopened);
        var second = reopened.Present(snapshot, request, stamp with { Revision = 2 }, null, default);
        Assert.AreEqual(SceneReadiness.Ready, second.Readiness);
        CollectionAssert.AreEqual(first.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray(),
            second.Objects.SelectMany(item => item.Visuals).OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray());

        static async Task Prepare(NodeScene scene)
        {
            for (var pass = 0; pass < 8; pass++)
            {
                await Task.WhenAll(scene.PendingWork);
                scene.Pump();
                if (scene.PendingWork.Length == 0) return;
            }
            Assert.Fail("Candidate work did not finish.");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ParkedVehicleWithoutUsableLengthReportsItsDesignSource(bool missing)
    {
        var instance = RailwayFixtures.Instance(2) with { Location = NodeFixtures.Location };
        var diagnostic = new ContentDiagnostic("design_source", ContentDiagnosticSeverity.Warning, "evaluate",
            new ContentReference.Design(instance.Design), new(new PackageId("vehicles"), "vehicles.json"), "vehicle", null, "Source.");
        ContentLookup<DesignEvaluation> design = missing
            ? new ContentLookup<DesignEvaluation>.Missing([diagnostic])
            : new ContentLookup<DesignEvaluation>.Found(RailwayFixtures.Vehicle() with { Core = null, Diagnostics = [diagnostic] });
        var content = NodeFixtures.Content(designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
        {
            [NodeFixtures.StationDesign] = new ContentLookup<DesignEvaluation>.Found(NodeFixtures.StationEvaluation()),
            [instance.Design] = design
        });
        var snapshot = Snapshot(content);
        await using var objects = new SettlementObjects(Ids());
        var rendered = objects.Present(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId), default, ImmutableArray.CreateBuilder<SceneFog>());
        var problem = rendered.Diagnostics.Single(item => item.Code == (missing ? "content_missing" : "vehicle_length_unknown"));
        Assert.AreEqual(new WorldObjectId.Instance(instance.Id), problem.Subject);
        Assert.AreEqual(new PackageId("vehicles"), problem.Package);
        Assert.AreEqual(new ContentReference.Design(instance.Design), problem.Content);
    }

    [TestMethod]
    public async Task ResourceCandidatesDoNotReadGroundAgainOnLaterFrames()
    {
        var content = NodeFixtures.Content();
        var snapshot = Snapshot(content);
        var map = snapshot.Core.World.Nodes[0] with
        { Resources = [new(new GoodId.Voxel(SceneFixtures.Voxel), 1, new ResourceRenewal.NonRenewable())] };
        snapshot = RailwayFixtures.ReplaceCore(snapshot, node: map);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        await using var objects = new SettlementObjects(Ids());
        var first = objects.Present(snapshot, layout, default, ImmutableArray.CreateBuilder<SceneFog>());
        Assert.IsTrue(first.Objects.Any(item => item.Subject is null), "The fixture must produce visible resource candidates.");
        var noGroundQueries = new FixedContentView
        {
            Generation = content.Generation, Core = content.Core,
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>
            { [new(1)] = content.FindSettlementLayout(new(1)) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [NodeFixtures.StationDesign] = content.FindDesign(NodeFixtures.StationDesign), [SceneFixtures.Design] = content.FindDesign(SceneFixtures.Design) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [SceneFixtures.Voxel] = content.FindVoxelAppearance(SceneFixtures.Voxel) }
        };
        // An unconfigured FindTerrain throws: repeated ground calculation would fail this frame.
        var second = objects.Present(snapshot with { Content = noGroundQueries }, layout, default, ImmutableArray.CreateBuilder<SceneFog>());
        CollectionAssert.AreEqual(first.Objects.Where(item => item.Subject is null).Select(item => item.Pose).ToArray(),
            second.Objects.Where(item => item.Subject is null).Select(item => item.Pose).ToArray());
    }

    private static SceneWorldSnapshot Snapshot(IContentView? content = null)
    {
        var snapshot = NodeFixtures.Snapshot(trains: [], content: content);
        var basis = (FixedCoreReadView)snapshot.Core;
        var node = snapshot.Core.World.Nodes[0];
        var settlement = NodeFixtures.Settlement() with { ParkedVehicles = [new(2)] };
        var instances = new[] { NodeFixtures.Station(2), RailwayFixtures.Instance(2) with { Location = NodeFixtures.Location } };
        return snapshot with { Core = new FixedCoreReadView
        {
            InputIdentity = basis.InputIdentity, Position = basis.Position,
            World = basis.World with { Nodes = [node], Edges = [] },
            Nodes = new Dictionary<NodeId, Lookup<NodeView>> { [node.Id] = new Lookup<NodeView>.Found(new(node, [], 80, 0, 80)) },
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [settlement.Id] = new Lookup<SettlementView>.Found(settlement) },
            Instances = instances.ToDictionary(instance => instance.Id, instance => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(instance))
        } };
    }

    private static Func<string, SceneObjectId> Ids()
    {
        var ids = new Dictionary<string, SceneObjectId>();
        return key => ids.TryGetValue(key, out var id) ? id : ids[key] = new((ulong)ids.Count + 1);
    }
}
