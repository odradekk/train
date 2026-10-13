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
    public void Running_edge_and_paused_station_round_trip_authoritative_railway_state()
    {
        var state = StateWithPlan();
        var world = state.World;
        var edge = world.Edges[0];
        var train = world.Trains[0] with
        {
            Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, edge.LengthMeters / 2),
            Transit = null, Activity = TrainActivity.Running, SpeedMetersPerSecond = 2, StopRequested = true
        };
        RoundTrip(state with { World = world with { Trains = [train] } });
        var station = world.Settlements[0];
        train = train with
        {
            Position = new TrainPosition.Node(station.Node), Transit = new(null, null),
            Activity = TrainActivity.Stopped, SpeedMetersPerSecond = 0, StopRequested = false,
            Plan = new(false, [new PlanStop.Station(station.Node, 10, [])]), RemainingDwellSteps = 5
        };
        RoundTrip(state with { World = world with { Trains = [train] } });
    }

    [TestMethod]
    [DataRow("missing_field")]
    [DataRow("invalid_track")]
    [DataRow("invalid_distance")]
    [DataRow("invalid_direction")]
    [DataRow("edge_transit")]
    [DataRow("unsupported_activity")]
    [DataRow("stopped_on_edge")]
    [DataRow("missing_plan")]
    [DataRow("invalid_index")]
    [DataRow("empty_plan")]
    [DataRow("single_target_loop")]
    [DataRow("missing_target")]
    [DataRow("invalid_dwell")]
    public void Inconsistent_nonempty_railway_payload_is_rejected(string corruption)
    {
        var state = StateWithPlan();
        var edge = state.World.Edges[0];
        var original = state.World.Trains[0] with
        {
            Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, edge.LengthMeters / 2),
            Transit = null, Activity = TrainActivity.Running, SpeedMetersPerSecond = 2
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
            case "edge_transit": train["transit"] = JsonNode.Parse("{\"entered_from\":null,\"next_edge\":null}"); break;
            case "unsupported_activity": train["activity"] = (int)TrainActivity.Combat; break;
            case "stopped_on_edge": train["activity"] = (int)TrainActivity.Stopped; train["speed_meters_per_second"] = 0; break;
            case "missing_plan": train["plan"] = null; train["next_stop_index"] = null; break;
            case "invalid_index": train["next_stop_index"] = 20; break;
            case "empty_plan": train["plan"]!["stops"]!.AsArray().Clear(); break;
            case "single_target_loop": train["plan"]!["loop"] = true; break;
            case "missing_target": train["plan"]!["stops"]![0]!["node"]!["value"] = 999999; break;
            case "invalid_dwell": train["remaining_dwell_steps"] = 1; break;
        }
        var result = CoreFactory.Create().DecodeState(root.ToJsonString());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(result, corruption);
        Assert.AreEqual("invalid_state", ((CoreResult<ICoreCapture>.Failure)result).Problems.Single().Code);
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
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(CoreFactory.Create().DecodeState(root.ToJsonString()));
        root = JsonNode.Parse(capture.Encode())!;
        var parkedVehicles = root["world"]!["settlements"]![0]!["parked_vehicles"]!.AsArray();
        parkedVehicles.Add(parkedVehicles[0]!.DeepClone());
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Failure>(CoreFactory.Create().DecodeState(root.ToJsonString()));
    }

    private static CoreState StateWithPlan()
    {
        using var candidate = WorldFixture.Prepare(WorldFixture.Input with
        {
            Configuration = WorldFixture.Input.Configuration with { WidthMeters = 100 }
        });
        using var session = (CoreSession)candidate.Activate();
        var state = session.State;
        var train = state.World.Trains[0] with
        {
            Plan = new(false, [new PlanStop.Waypoint(state.World.Nodes[^1].Id)]), NextStopIndex = 0
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

