using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldQueryTests
{
    private static ICoreSessionCandidate Prepare(WorldGenerationInput? input = null)
    {
        var result = CoreFactory.Create().PrepareNew(input ?? WorldFixture.Input,
            WorldFixture.Content, WorldGenerationTests.Options);
        Assert.IsInstanceOfType<CoreResult<ICoreSessionCandidate>.Success>(result);
        return ((CoreResult<ICoreSessionCandidate>.Success)result).Value;
    }

    [TestMethod]
    public void MinimalWorldQueriesExposeInitialStationVehicleAndStoppedTrain()
    {
        using var candidate = Prepare();
        using var session = candidate.Activate();
        var view = session.Read();
        var world = view.World;
        Assert.AreEqual(42UL, world.WorldSeed);
        Assert.AreEqual(WorldFixture.Rules.Railway, world.RailwayRule);
        Assert.AreEqual(10d, world.WidthMeters);
        Assert.AreEqual(10d, world.HeightMeters);
        Assert.AreEqual(1, world.Nodes.Length);
        Assert.AreEqual(0, world.Edges.Length);
        Assert.AreEqual(1, world.Trains.Length);
        var node = world.Nodes[0];
        Assert.AreEqual(new NodeId(1), node.Id);
        Assert.IsInstanceOfType<NodeOccupant.Settlement>(node.Occupant);
        Assert.AreEqual(SettlementRelation.NonHostile, ((NodeOccupant.Settlement)node.Occupant).Relation);
        Assert.AreEqual(1, node.Platforms);
        Assert.AreEqual(0, node.OccupiedPlatforms);
        var nodeView = ((Lookup<NodeView>.Found)view.FindNode(new(1))).Value;
        Assert.AreEqual(node, nodeView.Map);
        Assert.AreEqual(0, nodeView.AdjacentEdges.Length);
        var train = world.Trains[0];
        Assert.AreEqual(new TrainId(1), train.Id);
        Assert.AreEqual(TrainActivity.Stopped, train.Activity);
        Assert.AreEqual(new TrainPosition.Node(new(1)), train.Position);

        var location = ((Lookup<LocationView>.Found)view.FindLocation(new WorldLocation.Node(new(1)))).Value;
        CollectionAssert.AreEqual(new[] { new TrainId(1) }, location.Trains.ToArray());
        CollectionAssert.AreEquivalent(new InstanceId[] { new InstanceId.Vehicle(new(1)), new InstanceId.Building(new(1)) },
            location.Instances.ToArray());
        Assert.AreEqual(node.Occupant, location.Occupant);
        var vehicle = ((Lookup<InstanceView>.Found)view.FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        var building = ((Lookup<InstanceView>.Found)view.FindInstance(new InstanceId.Building(new(1)))).Value;
        Assert.AreEqual(WorldFixture.Vehicle, vehicle.Design);
        Assert.AreEqual(WorldFixture.Station, building.Design);
        Assert.AreEqual(SettlementRelation.PlayerOwned, vehicle.Ownership);
        Assert.AreEqual(SettlementRelation.NonHostile, building.Ownership);
        Assert.AreEqual(new InstanceDetails.Vehicle(SizeClass.Small, new TrainId(1)), vehicle.Details);
        Assert.AreEqual(new InstanceDetails.Building(new(1), PlotSize.Station), building.Details);
        foreach (var instance in new[] { vehicle, building })
        {
            Assert.AreEqual(new WorldLocation.Node(new(1)), instance.Location);
            Assert.AreEqual(new DurabilityView(100, 100), instance.Structure);
            Assert.AreEqual(InstanceLock.None, instance.Lock);
            Assert.AreEqual(0, instance.MissingContent.Length);
            Assert.AreEqual(1, instance.Parts.Length);
            Assert.AreEqual(new DurabilityView(100, 100), instance.Parts[0].Durability);
            Assert.IsFalse(instance.Parts[0].Destroyed);
        }
    }

    [TestMethod]
    public void UnknownWorldObjectsReturnMissing()
    {
        using var candidate = Prepare();
        var view = candidate.View;
        Assert.IsInstanceOfType<Lookup<NodeView>.Missing>(view.FindNode(new(99)));
        Assert.IsInstanceOfType<Lookup<EdgeView>.Missing>(view.FindEdge(new(99)));
        Assert.IsInstanceOfType<Lookup<LocationView>.Missing>(view.FindLocation(new WorldLocation.Node(new(99))));
        Assert.IsInstanceOfType<Lookup<LocationView>.Missing>(view.FindLocation(new WorldLocation.Edge(new(99), 0)));
        Assert.IsInstanceOfType<Lookup<InstanceView>.Missing>(view.FindInstance(new InstanceId.Vehicle(new(99))));
        Assert.IsInstanceOfType<Lookup<InstanceView>.Missing>(view.FindInstance(new InstanceId.Building(new(99))));
    }

    [TestMethod]
    public void EdgeQueriesAgreeWithWorldAndNodeAdjacency()
    {
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with { WidthMeters = 100 } };
        using var candidate = Prepare(input);
        var view = candidate.View;
        Assert.IsTrue(view.World.Edges.Length > 0);
        foreach (var edge in view.World.Edges)
        {
            var edgeView = ((Lookup<EdgeView>.Found)view.FindEdge(edge.Id)).Value;
            Assert.AreEqual(edge, edgeView.Map);
            Assert.AreEqual(edge.Tracks, edgeView.Tracks.Length);
            foreach (var track in edgeView.Tracks)
            {
                Assert.AreEqual(0, track.Trains.Length);
                Assert.IsNull(track.DirectionLock);
            }
            foreach (var endpoint in new[] { edge.Start, edge.End })
                CollectionAssert.Contains(((Lookup<NodeView>.Found)view.FindNode(endpoint)).Value.AdjacentEdges.ToArray(), edge.Id);
            var location = ((Lookup<LocationView>.Found)view.FindLocation(new WorldLocation.Edge(edge.Id, edge.LengthMeters / 2))).Value;
            Assert.AreEqual(0, location.Trains.Length);
            Assert.AreEqual(0, location.Instances.Length);
            Assert.IsNull(location.Occupant);
        }
    }

    [TestMethod]
    public void InstancesAppearAtTheirOwnerNodeAcrossSeveralSettlements()
    {
        var input = WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with { WidthMeters = 100 } };
        using var candidate = Prepare(input);
        var view = candidate.View;
        var train = view.World.Trains.Single();
        var trainNode = ((TrainPosition.Node)train.Position).Id;
        var vehicle = ((Lookup<InstanceView>.Found)view.FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        Assert.AreEqual(new WorldLocation.Node(trainNode), vehicle.Location);
        for (var index = 0; index < view.World.Nodes.Length; index++)
        {
            var node = view.World.Nodes[index];
            var buildingId = new InstanceId.Building(new((ulong)index + 1));
            var building = ((Lookup<InstanceView>.Found)view.FindInstance(buildingId)).Value;
            Assert.AreEqual(new WorldLocation.Node(node.Id), building.Location);
            var location = ((Lookup<LocationView>.Found)view.FindLocation(building.Location)).Value;
            CollectionAssert.Contains(location.Instances.ToArray(), buildingId);
            Assert.AreEqual(node.Id == trainNode, location.Instances.Contains(vehicle.Id));
            Assert.AreEqual(node.Id == trainNode, location.Trains.Contains(train.Id));
        }
    }

    [TestMethod]
    public void CandidateReferencesDescribeHandWrittenTemplateAndDesigns()
    {
        using var candidate = Prepare();
        Assert.AreEqual(0, candidate.MissingReferences.Length);
        Assert.AreEqual(0, candidate.AdaptationReport.Length);
        CollectionAssert.AreEqual(new[] { WorldFixture.Vehicle, WorldFixture.Station }, candidate.References.Designs.ToArray());
        CollectionAssert.AreEqual(new[] { WorldFixture.Content.Identity }, candidate.References.Inputs.ToArray());
        Assert.AreEqual(1, candidate.References.Templates.Length);
        var template = candidate.References.Templates[0];
        Assert.AreEqual(new SettlementId(1), template.Owner);
        Assert.AreEqual(new SettlementTemplateId("fixture:town-template"), template.SourceTemplate);
        CollectionAssert.AreEqual(new[] { new TemplatePlotReference(1, PlotSize.Station) }, template.Plots.ToArray());
    }

    [TestMethod]
    public void RetainedViewRemainsReadableAfterAdvanceAndSessionDisposal()
    {
        using var candidate = Prepare();
        var session = candidate.Activate();
        var retained = session.Read();
        var original = retained.World;
        var position = retained.Position;
        session.Advance(1);
        session.Dispose();
        Assert.AreEqual(position, retained.Position);
        Assert.AreEqual(original, retained.World);
        Assert.AreEqual(original.Nodes[0], ((Lookup<NodeView>.Found)retained.FindNode(new(1))).Value.Map);
        Assert.IsInstanceOfType<Lookup<InstanceView>.Found>(retained.FindInstance(new InstanceId.Vehicle(new(1))));
    }
}
