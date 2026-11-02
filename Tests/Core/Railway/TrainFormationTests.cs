using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed partial class TrainFormationTests
{
    [TestMethod]
    public void New_formation_preview_uses_vehicle_designs_and_live_parts_without_creating_objects()
    {
        using var session = new CoreSession(State());
        Accepted(session, new CoreCommand.DisbandTrain(new(1)));
        var before = Success(session.Capture(CapturePurpose.Save)).Checkpoint;

        var preview = Success(session.Read().PreviewTrainFormation([new(2), new(1)], null));

        Assert.AreEqual(7.0, preview.LengthMeters);
        Assert.AreEqual(30.0, preview.MassTons);
        Assert.AreEqual(50.0, preview.TractionKilonewtons);
        Assert.AreEqual(20L, preview.Capacity);
        Assert.IsTrue(preview.Blockers.IsEmpty);
        Assert.IsNull(preview.DepartureProblem);
        Assert.AreEqual(before, Success(session.Capture(CapturePurpose.Save)).Checkpoint);
    }

    private static CoreState State()
    {
        var state = WorldFixture.State();
        var original = state.World.Instances.Single(i => i.Id is InstanceId.Vehicle);
        var locomotive = state.World.Designs.Single(d => d.Design == Vehicle) with
        {
            Kind = new DesignKind.Vehicle(SizeClass.Small, 4), MassTons = 10,
            Parts = [state.World.Designs.Single(d => d.Design == Vehicle).Parts[0] with
            { Capabilities = [new PartCapability.Control(), new PartCapability.Propulsion(30, 12), new PartCapability.Storage(12)] }]
        };
        var wagon = locomotive with
        {
            Design = SecondVehicle, Kind = new DesignKind.Vehicle(SizeClass.Large, 10), MassTons = 20,
            Parts = [locomotive.Parts[0] with
            { Capabilities = [new PartCapability.Propulsion(20, 8), new PartCapability.Storage(8)] }]
        };
        var designs = state.World.Designs.Select(d => d.Design == Vehicle ? locomotive : d).Append(wagon).ToImmutableArray();
        return state with
        {
            Content = state.Content with { Designs = designs },
            World = state.World with
            {
                Designs = designs,
                Instances = state.World.Instances.Add(original with
                { Id = new InstanceId.Vehicle(new(2)), Design = SecondVehicle, Details = new InstanceDetails.Vehicle(SizeClass.Large, null) }),
                Settlements = state.World.Settlements.Select(s => s with { ParkedVehicles = [new(2)] }).ToImmutableArray(),
                Next = state.World.Next with { Vehicle = 3 }
            }
        };
    }

    private static TrainView Train(ICoreSession session) => ((Lookup<TrainView>.Found)session.Read().FindTrain(new(1))).Value;
    private static SubmissionResult.Completed Accepted(ICoreSession session, CoreCommand command)
    {
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        return result;
    }
}
