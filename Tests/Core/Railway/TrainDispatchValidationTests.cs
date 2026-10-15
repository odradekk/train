using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainDispatchValidationTests
{
    [TestMethod]
    public void Queued_station_with_remaining_dwell_round_trips_and_checks_boundary_age()
    {
        var state = State();
        var train = state.World.Trains[0];
        var node = ((TrainPosition.Node)train.Position).Id;
        train = train with { Activity = TrainActivity.Waiting, QueueEnteredStep = 3,
            Plan = new(false, [new PlanStop.Station(node, 10, [])]), RemainingDwellSteps = 5 };
        state = state with { World = state.World with { Trains = [train] } };
        var capture = RoundTrip(state);
        Assert.IsNull(CoreCapture.ValidateCheckpoint(capture.Checkpoint));
        var future = new CoreCapture(state with { World = state.World with { Trains = [train with { QueueEnteredStep = 6 }] } });
        Assert.AreEqual("/world/trains/queue_entered_step", Failure(future.Encode()));
        Assert.AreEqual("/world/trains/queue_entered_step", CoreCapture.ValidateCheckpoint(future.Checkpoint)!.Field);
        AssertInvalid(state.World with { Trains = [train with { Activity = TrainActivity.Stopped }] }, "/world/trains/queue_entered_step");
    }

    [TestMethod]
    public void Breaks_and_return_marker_round_trip_and_require_valid_references()
    {
        var state = State();
        var edge = state.World.Edges[0] with { Breaks = [new(new(1), 1, 0), new(new(2), 1, state.World.Edges[0].LengthMeters)] };
        var train = state.World.Trains[0] with { Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Reverse, 0),
            Activity = TrainActivity.Running, ReturningFromBreak = new(1) };
        var world = state.World with { Edges = state.World.Edges.SetItem(0, edge), Trains = [train], Next = state.World.Next with { Break = 3 } };
        RoundTrip(state with { World = world });
        AssertInvalid(world with { Trains = [train with { ReturningFromBreak = new(3) }] }, "/world/trains/returning_from_break");
        AssertInvalid(world with { Trains = [state.World.Trains[0] with { ReturningFromBreak = new(1) }] }, "/world/trains/returning_from_break");
        AssertInvalid(world with { Next = world.Next with { Break = 2 } }, "/world/next/break");
        foreach (var breaks in new ImmutableArray<TrackBreakView>[] { default, [new(new(0), 1, 0)], [edge.Breaks[1], edge.Breaks[0]] })
            AssertInvalid(world with { Edges = world.Edges.SetItem(0, edge with { Breaks = breaks }) }, "/world/edges/breaks");
        AssertInvalid(world with { Edges = world.Edges.SetItem(0, edge with { Breaks = [new(new(1), 0, 0)] }) }, "/world/edges/breaks/track");
        foreach (var distance in new[] { -1, double.NaN, double.PositiveInfinity, edge.LengthMeters + 1 })
            AssertInvalid(world with { Edges = world.Edges.SetItem(0, edge with { Breaks = [new(new(1), 1, distance)] }) }, "/world/edges/breaks/distance_meters");
    }

    [TestMethod]
    public void Handoffs_require_ordered_unique_existing_empty_open_tracks()
    {
        var state = State();
        var edge = state.World.Edges[0];
        var handoff = new TrackHandoff(edge.Id, 1, TravelDirection.Reverse);
        var world = state.World with { TrackHandoffs = [handoff] };
        RoundTrip(state with { World = world });
        AssertInvalid(world with { TrackHandoffs = default }, "/world/track_handoffs");
        AssertInvalid(world with { TrackHandoffs = [handoff, handoff] }, "/world/track_handoffs");
        AssertInvalid(world with { TrackHandoffs = [handoff with { Edge = new(ulong.MaxValue) }] }, "/world/track_handoffs/edge");
        AssertInvalid(world with { TrackHandoffs = [handoff with { Track = 0 }] }, "/world/track_handoffs/track");
        AssertInvalid(world with { TrackHandoffs = [handoff with { Direction = (TravelDirection)99 }] }, "/world/track_handoffs/direction");
        var train = world.Trains[0] with { Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, 0),
            Activity = TrainActivity.Running };
        AssertInvalid(world with { Trains = [train] }, "/world/track_handoffs/track");
        var broken = world with { Edges = world.Edges.SetItem(0, edge with { Breaks = [new(new(1), 1, 0)] }),
            Next = world.Next with { Break = 2 } };
        Assert.AreEqual("/world/track_handoffs/track", Failure(new CoreCapture(state with { World = broken }).Encode()));
    }

    [TestMethod]
    public void Same_track_trains_must_have_the_same_direction()
    {
        var state = State();
        var world = state.World;
        var edge = world.Edges[0];
        var train = world.Trains[0] with { Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Forward, 0),
            Activity = TrainActivity.Running };
        var second = train with { Id = new(world.Next.Train), Vehicles = [new(world.Next.Vehicle)], Inventory = new(world.Next.Inventory) };
        var vehicle = world.Instances.Single(i => i.Id == new InstanceId.Vehicle(train.Vehicles[0]));
        var inventory = world.Inventories.Single(i => i.Id == train.Inventory);
        world = world with
        {
            Trains = [train, second],
            Instances = world.Instances.Add(vehicle with { Id = new InstanceId.Vehicle(second.Vehicles[0]),
                Details = ((InstanceDetails.Vehicle)vehicle.Details) with { Train = second.Id } }),
            Inventories = world.Inventories.Add(inventory with { Id = second.Inventory, Owner = new InventoryOwner.Train(second.Id) }),
            Next = world.Next with { Train = world.Next.Train + 1, Vehicle = world.Next.Vehicle + 1, Inventory = world.Next.Inventory + 1 }
        };
        WorldStateValidation.Validate(world);
        AssertInvalid(world with { Trains = [train, second with { Position = new TrainPosition.Edge(edge.Id, 1, TravelDirection.Reverse, 0) }] },
            "/world/trains/position/direction");
    }

    [TestMethod]
    [DataRow("queue_entered_step")]
    [DataRow("returning_from_break")]
    [DataRow("track_handoffs")]
    [DataRow("break")]
    public void New_saved_fields_are_required_even_when_null_or_empty(string field)
    {
        var root = JsonNode.Parse(new CoreCapture(State()).Encode())!;
        var world = root["world"]!;
        var owner = field switch { "track_handoffs" => world, "break" => world["next"]!, _ => world["trains"]![0]! };
        owner.AsObject().Remove(field);
        Assert.AreEqual("/world", Failure(root.ToJsonString()));
    }

    private static CoreState State()
    {
        var state = TrainDispatchFixture.State(1) with { Position = new(5, 0) };
        var train = state.World.Trains[0] with { Plan = new(false, [new PlanStop.Waypoint(state.World.Nodes[^1].Id)]), NextStopIndex = 0 };
        return state with { World = state.World with { Trains = [train] } };
    }

    private static ICoreCapture RoundTrip(CoreState state)
    {
        WorldStateValidation.Validate(state.World);
        var capture = new CoreCapture(state);
        var restored = WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode()));
        Assert.AreEqual(capture.Encode(), restored.Encode());
        Assert.AreEqual(capture.Checkpoint, restored.Checkpoint);
        return restored;
    }

    private static string Failure(string payload)
        => ((CoreResult<ICoreCapture>.Failure)CoreFactory.Create().DecodeState(payload)).Problems.Single().Field;

    private static void AssertInvalid(WorldState world, string field)
        => Assert.AreEqual(field, Assert.ThrowsExactly<CoreStateFormatException>(() => WorldStateValidation.Validate(world)).Field);
}
