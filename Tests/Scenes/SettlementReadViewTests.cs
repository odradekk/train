using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class SettlementReadViewTests
{
    [TestMethod]
    public async Task Node_frames_share_building_queries_with_train_platform_reconciliation()
    {
        var basis = NodeFixtures.Snapshot();
        var view = new CountingCoreReadView(basis.Core);
        await using var session = new WorldSceneSession(new(Guid.NewGuid()), basis with { Core = view },
            WorldSceneSession.Generate, new(), new(), RailwayFixtures.EmptyNode);
        session.RequestView(new WorldSceneTarget.Location(NodeFixtures.Location));
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        var frame = await RailwayFixtures.Settled(session, request);
        for (var pass = 0; pass < 3; pass++)
            Assert.IsInstanceOfType<SceneResult<ISceneFrame>.Success>(session.Present(request, frame));
        Assert.AreEqual(1, view.SettlementQueries[new(1)]);
        Assert.AreEqual(1, view.InstanceQueries[new InstanceId.Building(new(1))],
            "Platform reconciliation must share the building read with Pump and Present.");
    }

    [TestMethod]
    public async Task Repeated_frames_query_each_settlement_and_building_or_parked_instance_once_per_read_view()
    {
        var basis = NodeFixtures.Snapshot(trains: []);
        var station = NodeFixtures.Station(2);
        var vehicle = RailwayFixtures.Instance(7) with { Location = NodeFixtures.Location };
        var view = View(basis, NodeFixtures.Settlement() with { ParkedVehicles = [new(7)] },
            new Lookup<InstanceView>.Found(station), new Lookup<InstanceView>.Found(vehicle));
        var snapshot = basis with { Core = view };
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        await using var objects = new SettlementObjects(_ => new(1));
        for (var pass = 0; pass < 4; pass++)
        {
            objects.Pump(snapshot, layout);
            objects.Present(snapshot, layout, default, ImmutableArray.CreateBuilder<SceneFog>());
            await Task.WhenAll(objects.PendingWork);
        }
        Assert.AreEqual(1, view.SettlementQueries[new(1)], "A fixed read view must materialize its settlement only once.");
        Assert.AreEqual(1, view.InstanceQueries[station.Id], "Building reads must be reused across Pump and Present.");
        Assert.AreEqual(1, view.InstanceQueries[vehicle.Id], "Parked vehicle reads must be reused across Pump and Present.");
    }

    [TestMethod]
    public async Task Missing_instances_are_cached_until_a_new_read_view_replaces_them()
    {
        var basis = NodeFixtures.Snapshot(trains: []);
        var missing = View(basis, NodeFixtures.Settlement(), new Lookup<InstanceView>.Missing());
        var snapshot = basis with { Core = missing };
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        await using var objects = new SettlementObjects(_ => new(1));
        for (var pass = 0; pass < 3; pass++)
            Assert.IsEmpty(objects.Present(snapshot, layout, default, ImmutableArray.CreateBuilder<SceneFog>()).Objects);
        var instance = NodeFixtures.Station(2);
        var next = View(basis, NodeFixtures.Settlement(), new Lookup<InstanceView>.Found(instance));
        var rendered = objects.Present(snapshot with { Core = next }, layout, default, ImmutableArray.CreateBuilder<SceneFog>());
        Assert.IsNotEmpty(rendered.Objects, "A new view at the same position must replace a cached Missing result.");
        Assert.AreEqual(1, missing.InstanceQueries[instance.Id]);
        Assert.AreEqual(1, next.InstanceQueries[instance.Id]);
    }

    [TestMethod]
    public async Task Missing_settlements_are_cached_per_read_view()
    {
        var basis = NodeFixtures.Snapshot(trains: []);
        var view = View(basis, null, new Lookup<InstanceView>.Missing());
        var snapshot = basis with { Core = view };
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        await using var objects = new SettlementObjects(_ => new(1));
        for (var pass = 0; pass < 3; pass++)
            Assert.IsEmpty(objects.Present(snapshot, layout, default, ImmutableArray.CreateBuilder<SceneFog>()).Objects);
        Assert.AreEqual(1, view.SettlementQueries[new(1)], "Missing settlements must not repeat the full query every frame.");
        Assert.IsEmpty(view.InstanceQueries);
    }

    private static CountingCoreReadView View(SceneWorldSnapshot basis, SettlementView? settlement,
        Lookup<InstanceView> station, Lookup<InstanceView>? vehicle = null) => new(new FixedCoreReadView
    {
        Position = basis.Core.Position, InputIdentity = basis.Core.InputIdentity, World = basis.Core.World,
        Settlements = new Dictionary<SettlementId, Lookup<SettlementView>>
        { [new(1)] = settlement is null ? new Lookup<SettlementView>.Missing() : new Lookup<SettlementView>.Found(settlement) },
        Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
        { [new InstanceId.Building(new(1))] = station, [new InstanceId.Vehicle(new(7))] = vehicle ?? new Lookup<InstanceView>.Missing() }
    });
}
