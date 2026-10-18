using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainStateValidationTests
{
    [TestMethod]
    public void Historical_entry_track_may_exceed_current_capacity_but_the_exit_must_exist()
    {
        var state = StateWithPlan();
        var edge = state.World.Edges[0];
        var train = state.World.Trains[0] with
        {
            Position = new TrainPosition.Node(edge.Start, edge.Id, edge.Tracks + 1, edge.Id, 1,
                NodeTravelPhase.Departing, WorldFixture.ApproachLength, null)
        };
        RoundTrip(state with { World = state.World with { Trains = [train] } });
    }

    [TestMethod]
    [DataRow("entered_track", "0", "entered_track")]
    [DataRow("entered_track", "1", "entered_track")]
    [DataRow("next_track", "0", "next_track")]
    [DataRow("next_track", "3", "next_track")]
    [DataRow("phase", "0", "next_track")]
    public void Invalid_node_track_identity_or_phase_is_rejected_on_decode(string field, string value, string problemField)
    {
        var state = StateWithPlan();
        var edge = state.World.Edges[0];
        var train = state.World.Trains[0] with
        {
            Position = WorldFixture.Node(edge.Start, NodeTravelPhase.Departing, nextEdge: edge.Id)
        };
        var saved = RoundTrip(state with { World = state.World with { Trains = [train] } });
        var root = JsonNode.Parse(saved.Encode())!;
        root["world"]!["trains"]![0]!["position"]![field] = JsonNode.Parse(value);
        var result = CoreFactory.Create().DecodeState(root.ToJsonString());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(result);
        var problem = ((CoreResult<ICoreCapture>.Failure)result).Problems.Single();
        Assert.AreEqual("invalid_state", problem.Code);
        Assert.AreEqual("/world/trains/position/" + problemField, problem.Field);
    }

    [TestMethod]
    public void An_unselected_exit_on_an_open_edge_round_trips_and_is_filled_on_restore()
    {
        var state = StateWithPlan();
        var edge = state.World.Edges[0];
        var train = state.World.Trains[0] with
        {
            Position = WorldFixture.Node(edge.Start, NodeTravelPhase.Departing, nextEdge: edge.Id) with { NextTrack = null }
        };
        var saved = RoundTrip(state with { World = state.World with { Trains = [train] } });
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, state.Content, WorldFixture.Options));
        using var restored = candidate.Activate();
        var view = ((Lookup<TrainView>.Found)restored.Read().FindTrain(train.Id)).Value;
        Assert.IsTrue(view.Position is TrainPosition.Node { NextTrack: 1 });
    }

    [TestMethod]
    public void Running_edge_and_paused_station_round_trip_authoritative_railway_state()
    {
        var state = StateWithPlan();
        var world = state.World;
        var edge = world.Edges[0];
        var train = world.Trains[0] with
        {
            Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, edge.LengthMeters / 2),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = 2, StopRequested = true
        };
        RoundTrip(state with { World = world with { Trains = [train] } });
        var station = world.Settlements[0];
        train = train with
        {
            Position = WorldFixture.Node(station.Node, NodeTravelPhase.Docked, WorldFixture.ApproachLength + WorldFixture.PlatformLength / 2, platform: 1),
            Activity = TrainActivity.Stopped, SpeedMetersPerSecond = 0, StopRequested = false,
            Plan = new(false, [new PlanStop.Station(station.Node, 10, [])]), RemainingDwellSteps = 5
        };
        RoundTrip(state with { World = world with { Trains = [train] } });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Station_at_empty_or_hostile_node_round_trips(bool hostile)
    {
        var state = StateWithPlan(emptyNodes: !hostile);
        var world = state.World;
        NodeId target;
        if (hostile)
        {
            var settlement = world.Settlements[0] with { Relation = SettlementRelation.Hostile };
            target = settlement.Node;
            world = world with
            {
                Settlements = world.Settlements.SetItem(0, settlement),
                Nodes = world.Nodes.Select(node => node.Id == target ? node with
                    { Occupant = new NodeOccupant.Settlement(settlement.Id, settlement.Relation) } : node).ToImmutableArray(),
                Instances = world.Instances.Select(instance => instance.Details is InstanceDetails.Building building
                    && settlement.Plots.Any(plot => plot.Id == building.Plot)
                    ? instance with { Ownership = SettlementRelation.Hostile } : instance).ToImmutableArray()
            };
        }
        else target = world.Nodes.First(node => node.Occupant is NodeOccupant.Empty).Id;
        var train = world.Trains[0] with
        {
            Position = WorldFixture.Node(target, NodeTravelPhase.Docked, WorldFixture.ApproachLength + (hostile ? WorldFixture.PlatformLength / 2 : 0)),
            Plan = new(false, [new PlanStop.Station(target, 10, [])]),
            Activity = TrainActivity.Servicing, RemainingDwellSteps = 5
        };
        RoundTrip(state with { World = world with { Trains = [train] } });
    }

    [TestMethod]
    [DataRow("missing_field", "/world")]
    [DataRow("invalid_track", "/world/trains/position")]
    [DataRow("invalid_distance", "/world/trains/position")]
    [DataRow("invalid_direction", "/world/trains/position")]
    [DataRow("invalid_node_phase", "/world/trains/position/phase")]
    [DataRow("unsupported_activity", "/world/trains/activity")]
    [DataRow("stopped_on_edge", "/world/trains/activity")]
    [DataRow("missing_plan", "/world/trains/activity")]
    [DataRow("invalid_index", "/world/trains/next_stop_index")]
    [DataRow("empty_plan", "/world/trains/plan/stops")]
    [DataRow("single_target_loop", "/world/trains/plan")]
    [DataRow("missing_target", "/world/trains/plan/stops/0/node")]
    [DataRow("invalid_dwell", "/world/trains/remaining_dwell_steps")]
    public void Inconsistent_nonempty_railway_payload_is_rejected(string corruption, string expectedField)
    {
        var state = StateWithPlan();
        var edge = state.World.Edges[0];
        var original = state.World.Trains[0] with
        {
            Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, edge.LengthMeters / 2),
            Activity = TrainActivity.Running, SpeedMetersPerSecond = 2
        };
        if (corruption == "invalid_dwell")
            original = original with
            {
                Position = WorldFixture.Node(state.World.Settlements[0].Node, NodeTravelPhase.Docked, WorldFixture.ApproachLength + WorldFixture.PlatformLength / 2, platform: 1),
                Activity = TrainActivity.Stopped, SpeedMetersPerSecond = 0,
                Plan = new(false, [new PlanStop.Station(state.World.Settlements[0].Node, 10, [])]),
                RemainingDwellSteps = 10
            };
        var capture = new CoreCapture(state with { World = state.World with { Trains = [original] } });
        var root = JsonNode.Parse(capture.Encode())!;
        var train = root["world"]!["trains"]![0]!;
        switch (corruption)
        {
            case "missing_field": train.AsObject().Remove("stop_requested"); break;
            case "invalid_track": train["position"]!["track"] = 0; break;
            case "invalid_distance": train["position"]!["distance_meters"] = edge.LengthMeters + 1; break;
            case "invalid_direction": train["position"]!["direction"] = 99; break;
            case "invalid_node_phase": train["position"] = JsonNode.Parse("{\"$kind\":\"node\",\"id\":{\"value\":1},\"entered_from\":null,\"entered_track\":null,\"next_edge\":null,\"next_track\":null,\"phase\":99,\"distance_meters\":0,\"platform_number\":null}"); break;
            case "unsupported_activity": train["activity"] = (int)TrainActivity.Combat; break;
            case "stopped_on_edge": train["activity"] = (int)TrainActivity.Stopped; train["speed_meters_per_second"] = 0; break;
            case "missing_plan": train["plan"] = null; train["next_stop_index"] = null; train["current_run"] = null; break;
            case "invalid_index": train["next_stop_index"] = 20; break;
            case "empty_plan": train["plan"]!["stops"]!.AsArray().Clear(); break;
            case "single_target_loop": train["plan"]!["loop"] = true; break;
            case "missing_target": train["plan"]!["stops"]![0]!["node"]!["value"] = 999999; break;
            case "invalid_dwell": train["remaining_dwell_steps"] = 11; break;
        }
        var result = CoreFactory.Create().DecodeState(root.ToJsonString());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(result, corruption);
        var problem = ((CoreResult<ICoreCapture>.Failure)result).Problems.Single();
        Assert.AreEqual("invalid_state", problem.Code, corruption);
        Assert.AreEqual(expectedField, problem.Field, corruption);
    }

    [TestMethod]
    public void Parked_vehicle_has_one_settlement_owner_and_round_trips()
    {
        var state = StateWithPlan();
        var world = state.World;
        var vehicle = world.Instances.Single(i => i.Id is InstanceId.Vehicle);
        var parkedId = new VehicleId(world.Next.Vehicle);
        var parked = vehicle with
        {
            Id = new InstanceId.Vehicle(parkedId),
            Details = ((InstanceDetails.Vehicle)vehicle.Details) with { Train = null }
        };
        world = world with
        {
            Instances = world.Instances.Add(parked),
            Settlements = world.Settlements.SetItem(0, world.Settlements[0] with { ParkedVehicles = [parkedId] }),
            Next = world.Next with { Vehicle = parkedId.Value + 1 }
        };
        var capture = RoundTrip(state with { World = world });
        var root = JsonNode.Parse(capture.Encode())!;
        root["world"]!["settlements"]![0]!["parked_vehicles"]!.AsArray().Clear();
        var missingOwner = CoreFactory.Create().DecodeState(root.ToJsonString());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(missingOwner);
        Assert.AreEqual("/world/instances/details", ((CoreResult<ICoreCapture>.Failure)missingOwner).Problems.Single().Field);
        root = JsonNode.Parse(capture.Encode())!;
        var parkedVehicles = root["world"]!["settlements"]![0]!["parked_vehicles"]!.AsArray();
        parkedVehicles.Add(parkedVehicles[0]!.DeepClone());
        var duplicate = CoreFactory.Create().DecodeState(root.ToJsonString());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(duplicate);
        Assert.AreEqual("/world/settlements/parked_vehicles", ((CoreResult<ICoreCapture>.Failure)duplicate).Problems.Single().Field);
    }

    [TestMethod]
    [DataRow(true, false, TrainActivity.Running)]
    [DataRow(false, true, TrainActivity.Running)]
    [DataRow(true, true, TrainActivity.Waiting)]
    [DataRow(true, true, TrainActivity.Servicing)]
    [DataRow(true, true, TrainActivity.NoRoute)]
    public void Completed_plan_requires_stopped_activity_unless_running_with_a_pending_stop(
        bool hasPlan, bool stopRequested, TrainActivity activity)
    {
        var state = StateWithPlan();
        var edge = state.World.Edges[0];
        var train = state.World.Trains[0] with
        {
            Position = activity == TrainActivity.Running
                ? new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, edge.LengthMeters / 2)
                : WorldFixture.Node(edge.Start, NodeTravelPhase.Departing, nextEdge: edge.Id),
            Activity = activity, SpeedMetersPerSecond = activity == TrainActivity.Running ? 2 : 0,
            Plan = hasPlan ? state.World.Trains[0].Plan : null, NextStopIndex = null, StopRequested = stopRequested,
            CurrentRun = null
        };
        var capture = new CoreCapture(state with { World = state.World with { Trains = [train] } });
        var result = CoreFactory.Create().DecodeState(capture.Encode());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(result);
        var problem = ((CoreResult<ICoreCapture>.Failure)result).Problems.Single();
        Assert.AreEqual("invalid_state", problem.Code);
        Assert.AreEqual("/world/trains/activity", problem.Field);
    }

    private static CoreState StateWithPlan(bool emptyNodes = false)
    {
        using var candidate = WorldFixture.Prepare(WorldFixture.Input with
        {
            Configuration = WorldFixture.Input.Configuration with
            { WidthMeters = 100, SettlementRatio = emptyNodes ? 0.5 : WorldFixture.Input.Configuration.SettlementRatio }
        });
        using var session = (CoreSession)candidate.Activate();
        var state = session.State;
        var train = state.World.Trains[0] with
        {
            Plan = new(false, [new PlanStop.Waypoint(state.World.Nodes[^1].Id)]), NextStopIndex = 0,
            CurrentRun = new(state.Position, new(0), new(0), 0, 0, 0, [])
        };
        return state with { World = state.World with { Trains = [train] } };
    }

    private static ICoreCapture RoundTrip(CoreState state)
    {
        var capture = new CoreCapture(state);
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode()));
        Assert.AreEqual(capture.Encode(), decoded.Encode());
        Assert.AreEqual(capture.Checkpoint, decoded.Checkpoint);
        return decoded;
    }
}

