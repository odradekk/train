using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.World;

internal static class ResourceCycleFixture
{
    internal static readonly GoodId Good = new GoodId.Voxel(WorldFixture.Material);

    internal static CoreState State(bool renewable = false, double rate = 30, long reserve = 10,
        double interval = .05, double ratio = .5, bool multipleNodes = false)
    {
        var input = WorldFixture.Input;
        input = input with { Configuration = input.Configuration with
        {
            WidthMeters = multipleNodes ? 30 : 10,
            HeightMeters = multipleNodes ? 30 : 10,
            Resources = [new(WorldFixture.Material, [WorldFixture.Plain], 1, reserve, reserve,
                renewable, rate, interval, ratio)]
        } };
        using var candidate = WorldFixture.Prepare(input);
        using var session = (CoreSession)candidate.Activate();
        return session.State;
    }

    internal static CoreState Remaining(CoreState state, long quantity, bool allNodes = false)
        => state with { World = state.World with
        {
            Nodes = state.World.Nodes.Select((node, index) => node with
            {
                Resources = !allNodes && index != 0 ? []
                    : [node.Resources.Single() with { Quantity = quantity }]
            }).ToImmutableArray()
        } };

    internal static long Quantity(ICoreReadView view)
        => view.World.Nodes.SelectMany(n => n.Resources).Where(r => r.Good == Good).Sum(r => r.Quantity);

    internal static AdvanceResult Step(ICoreSession session, uint steps = 1)
    {
        var result = session.Advance(steps);
        Assert.IsNull(result.Fault, result.Diagnostic?.Message);
        Assert.AreEqual(steps, result.CompletedSteps);
        return result;
    }

    internal static ICoreSession Restore(ICoreSession session)
    {
        var capture = Capture(session);
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode()));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded,
            WorldFixture.Content, WorldFixture.Options));
        return candidate.Activate();
    }

    internal static ICoreCapture Capture(ICoreSession session)
        => WorldFixture.Success(session.Capture(CapturePurpose.Save));
}
