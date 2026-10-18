using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Settlements;

internal static class FoundingFixture
{
    internal static GoodId Material => new GoodId.Voxel(WorldFixture.Material);
    internal static DesignHash Shed => new(new string('9', 64));
    internal static CoreContentProjection Content => WorldFixture.Content with
    {
        Designs = WorldFixture.Content.Designs.Select(d => d.Design == WorldFixture.Station ? d with
            { BillOfMaterials = [new(Material, 2)], BillOfMaterialsItemCount = 2 } : d).Append(
            WorldFixture.Content.Designs[1] with
            {
                Design = Shed, Kind = new DesignKind.Building(PlotSize.Small, BuildingKind.Warehouse, []),
                Parts = [WorldFixture.Content.Designs[1].Parts[0] with
                    { Capabilities = [new PartCapability.Storage(50), new PartCapability.Construction(1, PlotSize.Medium, null)] }],
                BillOfMaterials = [new(Material, 3)], BillOfMaterialsItemCount = 3
            }).OrderBy(d => d.Design.Value, StringComparer.Ordinal).ToImmutableArray(),
        ConstructionRules = [WorldFixture.Content.ConstructionRules[0] with { FoundingDurationTicks = 3 }],
        SettlementTemplates = [WorldFixture.Content.SettlementTemplates[0] with
        {
            Plots = [new(7, PlotSize.SmallStation, WorldFixture.Station), new(13, PlotSize.Small, Shed), new(21, PlotSize.Small, Shed)],
            FoundingBuildings = [new(7, WorldFixture.Station), new(13, Shed)],
            SpecialInitialInventory = [new(Material, 20)]
        }]
    };

    internal static CoreSession Session(CoreContentProjection? content = null, long stock = 10)
    {
        var input = WorldFixture.Input with
        {
            Configuration = WorldFixture.Input.Configuration with { WidthMeters = 40, HeightMeters = 40, SettlementRatio = .25 },
            Start = new(new(1000), [new([WorldFixture.Vehicle], stock == 0 ? [] : [new(Material, stock)])])
        };
        using var candidate = WorldFixture.Prepare(input, content ?? Content);
        using var generated = (CoreSession)candidate.Activate();
        var node = generated.State.World.Nodes.First(n => n.Occupant is NodeOccupant.Empty).Id;
        var world = generated.State.World;
        return new(generated.State with { World = world with
        {
            Trains = world.Trains.Select(t => t with { Position = WorldFixture.Node(node), SpeedMetersPerSecond = 0 }).ToImmutableArray()
        } });
    }

    internal static NodeId Node(ICoreSession session) => ((TrainPosition.Node)session.Read().World.Trains[0].Position).Id;
    internal static SubmissionResult.Completed Submit(ICoreSession session, CoreCommand command)
    {
        var result = session.Submit(command, CommandSource.AutomationTest);
        Assert.IsInstanceOfType<SubmissionResult.Completed>(result);
        var completed = (SubmissionResult.Completed)result;
        Assert.IsFalse(completed.Receipt.Decision is CommandDecision.Faulted,
            completed.Diagnostic?.Message + " " + completed.Diagnostic?.StackTrace);
        return completed;
    }
    internal static FoundingJobView Start(ICoreSession session)
    {
        var result = Submit(session, new CoreCommand.FoundSettlement(Node(session), session.Read().World.Trains[0].Id));
        Assert.IsInstanceOfType<CommandDecision.Accepted>(result.Receipt.Decision);
        var id = ((WorldObjectId.Job)((CommandDecision.Accepted)result.Receipt.Decision).Created.Single()).Id;
        return Job(session.Read(), id);
    }
    internal static FoundingJobView Job(ICoreReadView view, JobId id)
        => ((JobView.Founding)((Lookup<JobView>.Found)view.FindJob(id)).Value).Value;
    internal static AdvanceResult Step(ICoreSession session, uint steps = 1)
    {
        var result = session.Advance(steps);
        Assert.IsNull(result.Fault, result.Diagnostic?.Message + " " + result.Diagnostic?.StackTrace);
        Assert.AreEqual(steps, result.CompletedSteps);
        return result;
    }
}
