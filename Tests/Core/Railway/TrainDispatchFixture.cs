using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Railway;

internal static class TrainDispatchFixture
{
    internal static CoreState State(int count = 3, int tracks = 1, double length = 20, CoreContentProjection? content = null)
    {
        var input = WorldFixture.Input with { Start = WorldFixture.Input.Start with
        { Trains = Enumerable.Range(0, count).Select(_ => new StartingTrainProjection([WorldFixture.Vehicle], [])).ToImmutableArray() } };
        using var candidate = WorldFixture.Prepare(input, content);
        using var session = (CoreSession)candidate.Activate();
        var state = session.State;
        var world = state.World;
        var node = world.Nodes[0] with { Position = new(0, 0) };
        world = world with
        {
            Source = world.Source with { Configuration = world.Source.Configuration with { WidthMeters = Math.Max(20, length) } },
            Nodes = [node, node with { Id = new(2), Position = new(Math.Min(20, length), 0), Occupant = new NodeOccupant.Empty(), Platforms = 0 }],
            Edges = [new(new(1), new(1), new(2), length, node.Terrain, tracks, 2, [], [])],
            Trains = world.Trains.Select(t => t with { Plan = new(false, [new PlanStop.Waypoint(new(2))]), NextStopIndex = 0 }).ToImmutableArray(),
            Next = world.Next with { Node = 3, Edge = 2 }
        };
        WorldStateValidation.Validate(world);
        return state with { World = world };
    }

    internal static TrainView View(ICoreSession session, ulong id) => ((Lookup<TrainView>.Found)session.Read().FindTrain(new(id))).Value;
    internal static SubmissionResult.Completed Submit(ICoreSession session, CoreCommand command)
    {
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        return result;
    }

    internal static void Step(ICoreSession session)
    {
        var result = session.Advance(1);
        Assert.IsNull(result.Fault);
        Assert.AreEqual(1u, result.CompletedSteps);
    }
}
