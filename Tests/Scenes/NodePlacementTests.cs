using System.Collections.Immutable;
using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodePlacementTests
{
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);
    private static readonly SceneFrameStamp Stamp = new(new(Guid.NewGuid()), new(1), new(1), 1);

    [TestMethod]
    [DataRow(SettlementRelation.NonHostile)]
    [DataRow(SettlementRelation.PlayerOwned)]
    public async Task FirstServicingTrainIdsOccupyPlatformsAndOverflowAndStoppedTrainsQueue(SettlementRelation relation)
    {
        ImmutableArray<TrainView> trains =
        [
            NodeFixtures.Train(9, enteredFrom: new(2), activity: TrainActivity.Servicing),
            NodeFixtures.Train(7, enteredFrom: new(2), activity: TrainActivity.Servicing),
            NodeFixtures.Train(2, enteredFrom: new(2)),
            NodeFixtures.Train(3, enteredFrom: new(2), activity: TrainActivity.Servicing)
        ];
        var snapshot = Snapshot(trains, occupant: new NodeOccupant.Settlement(new(1), relation));
        await using var scene = Controlled(snapshot);
        var before = await Settled(scene, snapshot);
        AssertPosition(before, 3, new(-20, 0, 0));
        AssertPosition(before, 7, new(20, 0, 0));
        Assert.IsTrue(Pose(before, 2).Position.Z < -64);
        Assert.IsTrue(Pose(before, 9).Position.Z < Pose(before, 2).Position.Z,
            "Overflow joins the incoming queue behind the lower TrainId stopped train.");

        var reordered = Snapshot(trains.Reverse().ToImmutableArray(),
            occupant: new NodeOccupant.Settlement(new(1), relation));
        var after = await Settled(scene, reordered);
        foreach (var train in trains) Assert.AreEqual(Pose(before, train.Id.Value), Pose(after, train.Id.Value));
    }

    public enum NonStationOccupant { HostileSettlement, Empty, Lair }

    [TestMethod]
    [DataRow(NonStationOccupant.HostileSettlement)]
    [DataRow(NonStationOccupant.Empty)]
    [DataRow(NonStationOccupant.Lair)]
    public async Task ServicingOutsideFriendlySettlementsUsesEnteredFromAndIgnoresThePlan(NonStationOccupant kind)
    {
        NodeOccupant occupant = kind switch
        {
            NonStationOccupant.HostileSettlement => new NodeOccupant.Settlement(new(1), SettlementRelation.Hostile),
            NonStationOccupant.Empty => new NodeOccupant.Empty(),
            _ => new NodeOccupant.Lair(new(1))
        };
        ImmutableArray<TrainView> trains =
        [
            NodeFixtures.Train(2, enteredFrom: new(2), nextEdge: new(1), activity: TrainActivity.Servicing),
            NodeFixtures.Train(1, enteredFrom: new(1), nextEdge: new(2), activity: TrainActivity.Servicing)
        ];
        var snapshot = Snapshot(trains, occupant: occupant);
        await using var scene = Controlled(snapshot);
        var before = await Settled(scene, snapshot);
        Assert.IsTrue(Pose(before, 1).Position.Z > 64);
        Assert.IsTrue(Pose(before, 2).Position.Z < -64);

        var changedPlans = trains.Select(train => train with
        {
            NodeTransit = train.NodeTransit! with { NextEdge = train.NodeTransit!.EnteredFrom },
            Plan = new(false, [new PlanStop.Waypoint(new(99))]), NextStopIndex = 0
        }).ToImmutableArray();
        var after = await Settled(scene, Snapshot(changedPlans, occupant: occupant));
        foreach (var train in trains) Assert.AreEqual(Pose(before, train.Id.Value), Pose(after, train.Id.Value));
    }

    [TestMethod]
    public async Task MissingEnteredFromUsesTheSmallestBuiltEdgeRegardlessOfWorldEdgeOrder()
    {
        var snapshot = Snapshot([NodeFixtures.Train(nextEdge: new(2))], platforms: 0);
        var world = snapshot.Core.World;
        snapshot = snapshot with
        {
            Core = new FixedCoreReadView
            {
                Position = snapshot.Core.Position, InputIdentity = snapshot.Core.InputIdentity,
                World = world with { Edges = world.Edges.Reverse().ToImmutableArray() },
                Nodes = world.Nodes.ToDictionary(item => item.Id, item => snapshot.Core.FindNode(item.Id)),
                Edges = world.Edges.ToDictionary(item => item.Id, item => snapshot.Core.FindEdge(item.Id)),
                Trains = world.Trains.ToDictionary(item => item.Id, item => snapshot.Core.FindTrain(item.Id)),
                Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
                { [new InstanceId.Vehicle(new(1))] = snapshot.Core.FindInstance(new InstanceId.Vehicle(new(1))) }
            }
        };
        await using var scene = Controlled(snapshot);
        var automatic = await Settled(scene, snapshot);
        var explicitEntry = Snapshot([NodeFixtures.Train(enteredFrom: new(1))], platforms: 0);
        var explicitFrame = await Settled(scene, explicitEntry);
        Assert.AreEqual(Pose(explicitFrame, 1), Pose(automatic, 1));

        var unbuiltFirst = RailwayFixtures.ReplaceCore(snapshot, edge: world.Edges[0] with { Tracks = 0 });
        var secondEdge = await Settled(scene, unbuiltFirst);
        Assert.IsTrue(Pose(secondEdge, 1).Position.Z < 0,
            "The lower edge ID is ignored when it has no built track.");
    }

    [TestMethod]
    [DataRow(false, TrainOrderFacing.FirstEndLeading)]
    [DataRow(false, TrainOrderFacing.LastEndLeading)]
    [DataRow(true, TrainOrderFacing.FirstEndLeading)]
    [DataRow(true, TrainOrderFacing.LastEndLeading)]
    public async Task MemberOrderAndFacingArePreservedOnPlatformsAndThePositiveZFallback(bool servicing, TrainOrderFacing facing)
    {
        var train = NodeFixtures.Train(vehicles: [new(3), new(1), new(2)],
            enteredFrom: servicing ? new EdgeId(2) : null, nextEdge: new(2),
            activity: servicing ? TrainActivity.Servicing : TrainActivity.Stopped, facing: facing);
        var snapshot = Snapshot([train], platforms: servicing ? 1 : 0, tracks: servicing ? 1 : 0);
        await using var scene = Controlled(snapshot);
        var frame = await Settled(scene, snapshot);
        var positive = facing == TrainOrderFacing.FirstEndLeading;
        var expected = servicing ? new[] { 8f, 0f, -8f } : new[] { -4f, -12f, -20f };
        if (!positive) Array.Reverse(expected);
        var members = new ulong[] { 3, 1, 2 };
        for (var i = 0; i < members.Length; i++)
        {
            AssertPosition(frame, members[i], new(0, 0, expected[i]));
            var forward = Vector3.Transform(Vector3.UnitZ, Pose(frame, members[i]).Rotation);
            Assert.IsTrue(Vector3.Distance(positive ? Vector3.UnitZ : -Vector3.UnitZ, forward) < 1e-4);
        }
    }

    [TestMethod]
    public async Task LongServicingConsistExtendsPastBothPlatformTrackEndsWithoutClamping()
    {
        var train = NodeFixtures.Train(vehicles: [new(1), new(2), new(3)], enteredFrom: new(2), activity: TrainActivity.Servicing);
        var snapshot = Snapshot([train], length: 104);
        await using var scene = Controlled(snapshot);
        var frame = await Settled(scene, snapshot);
        AssertPosition(frame, 1, new(-20, 0, 104));
        AssertPosition(frame, 2, new(-20, 0, 0));
        AssertPosition(frame, 3, new(-20, 0, -104));
        Assert.IsTrue(Pose(frame, 1).Position.Z > 80 && Pose(frame, 3).Position.Z < -80);
    }

    [TestMethod]
    public async Task LongIncomingQueueExtendsAlongTheEntryTangentAndRetainsMemberLengthsAndGaps()
    {
        var trains = Enumerable.Range(1, 6).Reverse()
            .Select(id => NodeFixtures.Train((ulong)id, enteredFrom: new(2))).ToImmutableArray();
        var snapshot = Snapshot(trains, platforms: 0, length: 64);
        await using var scene = Controlled(snapshot);
        var frame = await Settled(scene, snapshot);
        for (ulong id = 1; id <= 6; id++)
        {
            AssertPosition(frame, id, new(0, 0, -16 - 32 - 4 - ((float)id - 1) * 68));
            Assert.IsTrue(Vector3.Distance(Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, Pose(frame, id).Rotation)) < 1e-4);
        }
        var entry = scene.Layout!.EntryFor(new(2));
        Assert.IsTrue(Pose(frame, 6).Position.Z < entry.Point.Z + 16,
            "The queue extends past the generated connection instead of stacking at its endpoint.");
    }

    [TestMethod]
    public async Task EvenTrackCountQueuesUseABuiltApproachTrackAndExtendAlongItsTangent()
    {
        var trains = Enumerable.Range(1, 6).Select(id => NodeFixtures.Train((ulong)id, enteredFrom: new(2))).ToImmutableArray();
        var snapshot = Snapshot(trains, platforms: 0, tracks: 2, length: 64);
        await using var scene = Controlled(snapshot);
        var frame = await Settled(scene, snapshot);
        var fifth = Pose(frame, 5).Position;
        var sixth = Pose(frame, 6).Position;
        Assert.AreEqual(-17f, fifth.X, .001f);
        Assert.AreEqual(-17f, sixth.X, .001f);
        Assert.IsTrue(fifth.Z < scene.Layout!.EntryFor(new(2)).Point.Z);
        Assert.AreEqual(new Vector3(0, 0, -68), sixth - fifth);
    }

    [TestMethod]
    public async Task MissingMemberDesignReportsItsSourceAndDoesNotGuessConsistMemberLengths()
    {
        var hash = new DesignHash("missing-node-vehicle");
        var reference = new ContentReference.Design(hash);
        var diagnostic = new ContentDiagnostic("missing_node_design", ContentDiagnosticSeverity.Error, "lookup", reference,
            new(new PackageId("vehicle-package"), "vehicles.json"), "design", null, "The vehicle design is unavailable.");
        var content = RailwayFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 },
            design: RailwayFixtures.Vehicle(), designs: new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [hash] = new ContentLookup<DesignEvaluation>.Missing([diagnostic]) });
        var snapshot = NodeFixtures.Snapshot(platforms: 1, content: content,
            trains: [NodeFixtures.Train(vehicles: [new(1), new(2)], enteredFrom: new(2), activity: TrainActivity.Servicing)],
            instances:
            [
                RailwayFixtures.Instance() with { Location = NodeFixtures.Location },
                RailwayFixtures.Instance(2, design: hash) with { Location = NodeFixtures.Location }
            ]);
        await using var scene = Controlled(snapshot);
        var frame = await Settled(scene, snapshot);
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == diagnostic.Code &&
            problem.Content == reference && problem.Package == diagnostic.Source!.Package));
        Assert.IsTrue(frame.Diagnostics.Any(problem => problem.Code == "content_missing" && problem.Content == reference));
        Assert.AreEqual(Pose(frame, 1), Pose(frame, 2), "Unknown length prevents placing every member of the consist.");
        Assert.IsTrue(frame.Objects.Where(item => item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(new(1))))
            .All(item => item.Visuals.IsEmpty));
        Assert.IsTrue(frame.Objects.Where(item => item.Subject == new WorldObjectId.Instance(new InstanceId.Vehicle(new(2))))
            .SelectMany(item => item.Visuals).OfType<SceneVisual.Missing>().Any(item => item.Reference == reference));
    }

    private static SceneWorldSnapshot Snapshot(ImmutableArray<TrainView> trains, int platforms = 2,
        NodeOccupant? occupant = null, int tracks = 1, int length = 8)
    {
        var snapshot = NodeFixtures.Snapshot(platforms: platforms, trains: trains, tracks: tracks,
            content: NodeFixtures.Content(design: RailwayFixtures.Vehicle(lengthCells: length),
                rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = 1 }));
        return occupant is null ? snapshot : RailwayFixtures.ReplaceCore(snapshot,
            node: snapshot.Core.World.Nodes.Single(item => item.Id == NodeFixtures.NodeId) with { Occupant = occupant });
    }

    private static NodeScene Controlled(SceneWorldSnapshot snapshot)
    {
        var ids = new Dictionary<string, SceneObjectId>();
        ulong next = 0;
        return new(snapshot, NodeFixtures.NodeId, new(), key =>
        {
            if (!ids.TryGetValue(key, out var id)) ids.Add(key, id = new(++next));
            return id;
        }, (input, node, _, token, readyLayout) =>
        {
            var layout = NodeLayout.Build(input, node, token);
            readyLayout?.Invoke(layout);
            return new(layout, ImmutableDictionary<string, NodeScene.Part>.Empty);
        });
    }

    private static async Task<SceneFrame> Settled(NodeScene scene, SceneWorldSnapshot snapshot)
    {
        await scene.BuildWork;
        var revision = Stamp.Revision;
        SceneFrame Present() => scene.Present(snapshot, Request, Stamp with { Revision = ++revision }, null, default);
        Present();
        while (scene.PendingWork.Length > 0)
        {
            await Task.WhenAll(scene.PendingWork);
            Present();
        }
        var frame = Present();
        Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
        return frame;
    }

    private static ScenePose Pose(SceneFrame frame, ulong vehicle) =>
        ((Lookup<ScenePose>.Found)frame.FindPose(new WorldObjectId.Instance(new InstanceId.Vehicle(new(vehicle))))).Value;

    private static void AssertPosition(SceneFrame frame, ulong vehicle, Vector3 expected) =>
        Assert.IsTrue(Vector3.Distance(expected, Pose(frame, vehicle).Position) < 1e-3,
            $"Vehicle {vehicle}: expected {expected}, actual {Pose(frame, vehicle).Position}.");
}
