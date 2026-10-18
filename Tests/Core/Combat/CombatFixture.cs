using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Combat;

internal static class CombatFixture
{
    internal static readonly DesignHash ArmedVehicle = new(new string('7', 64));
    internal static readonly DesignHash ArmedBuilding = new(new string('8', 64));

    internal static WorldGenerationInput Input(int trains = 1, ImmutableArray<GoodsQuantity> cargo = default,
        int vehicles = 1) => WorldFixture.MultiInput with
    {
        Configuration = WorldFixture.MultiInput.Configuration with
        {
            SettlementRatio = .4, LairRatio = 0, HostileSettlementRatio = .5, LairWeights = [],
            NpcInitialization = WorldFixture.MultiInput.Configuration.NpcInitialization with
            {
                FillInputsAndLivingTargets = false, MinimumForSaleVehicles = 0, MaximumForSaleVehicles = 0,
                ForSaleVehicleCandidates = []
            },
            StartConstraints = WorldFixture.MultiInput.Configuration.StartConstraints with
            {
                MinimumReachableNonHostileSettlements = 1, MinimumReachableEmptyNodes = 1,
                RequireStation = true, ExcludeAdjacentLairs = false
            }
        },
        Start = new(new(1000), Enumerable.Range(0, trains)
            .Select(_ => new StartingTrainProjection(Enumerable.Repeat(ArmedVehicle, vehicles).ToImmutableArray(),
                cargo.IsDefault ? [] : cargo)).ToImmutableArray())
    };

    internal static CoreContentProjection Content(bool armedBuilding = true, long damage = 1)
    {
        var content = WorldFixture.MultiContent;
        var designs = content.Designs
            .Add(Design(ArmedVehicle, new DesignKind.Vehicle(SizeClass.Small, 1),
                [new PartCapability.Control(), new PartCapability.Propulsion(100, 10),
                    new PartCapability.Storage(100), new PartCapability.Weapon(damage, 1)]));
        if (armedBuilding)
            designs = designs.Add(Design(ArmedBuilding, new DesignKind.Building(PlotSize.Small, BuildingKind.Defense, []),
                [new PartCapability.Weapon(damage, 1)]));
        return content with
        {
            Designs = designs,
            SettlementTypes = [Town],
            SettlementTemplates = [Template(armedBuilding)]
        };
    }

    internal static CoreSession Session(int trains = 1, bool armedBuilding = true, long damage = 1,
        ImmutableArray<GoodsQuantity> cargo = default, int vehicles = 1)
    {
        var prepared = CoreFactory.Create().PrepareNew(Input(trains, cargo, vehicles), Content(armedBuilding, damage), WorldFixture.Options);
        if (prepared is CoreResult<ICoreSessionCandidate>.Failure failure)
            Assert.Fail(string.Join("; ", failure.Problems.Select(problem =>
                $"{problem.Code} {problem.Field} [{string.Join(",", problem.RelatedIdentities)}]")));
        using var candidate = ((CoreResult<ICoreSessionCandidate>.Success)prepared).Value;
        return (CoreSession)candidate.Activate();
    }

    internal static CoreSession SessionWithContent(CoreContentProjection content, int trains = 1,
        ImmutableArray<GoodsQuantity> cargo = default, int vehicles = 1)
    {
        var prepared = CoreFactory.Create().PrepareNew(Input(trains, cargo, vehicles), content, WorldFixture.Options);
        if (prepared is CoreResult<ICoreSessionCandidate>.Failure failure)
            Assert.Fail(string.Join("; ", failure.Problems.Select(problem =>
                $"{problem.Code} {problem.Field} [{string.Join(",", problem.RelatedIdentities)}]")));
        using var candidate = ((CoreResult<ICoreSessionCandidate>.Success)prepared).Value;
        return (CoreSession)candidate.Activate();
    }

    internal static NodeId HostileNode(ICoreSession session)
        => session.Read().World.Nodes.Where(node => node.Occupant is NodeOccupant.Settlement { Relation: SettlementRelation.Hostile })
            .OrderBy(node => node.Id.Value).First().Id;

    internal static NodeId StartNode(ICoreSession session, TrainId train)
        => ((TrainPosition.Node)((Lookup<TrainView>.Found)session.Read().FindTrain(train)).Value.Position).Id;

    internal static void Send(ICoreSession session, NodeId node, TrainId train)
    {
        var plan = new CoreCommand.ReplaceRunPlan(train, new(false, [new PlanStop.Waypoint(node)]));
        Accepted(session.Submit(plan, CommandSource.AutomationTest));
        Accepted(session.Submit(new CoreCommand.StartTrain(train), CommandSource.AutomationTest));
    }

    internal static ImmutableArray<CoreEvent> AdvanceUntil(ICoreSession session, Func<ICoreReadView, bool> condition,
        int maximum = 10000)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        for (var step = 0; step < maximum; step++)
        {
            var result = session.Advance(1);
            if (result.Fault is not null)
                Assert.Fail($"{result.Fault.Code}: {result.Diagnostic?.Message}\n{result.Diagnostic?.StackTrace}");
            events.AddRange(result.Events.Select(envelope => envelope.Event));
            if (condition(session.Read())) return events.ToImmutable();
        }
        Assert.Fail("未在限定步数内到达战斗测试边界。");
        return [];
    }

    internal static ImmutableArray<CoreEvent> AdvanceUntilEvent(ICoreSession session, Func<CoreEvent, bool> condition,
        int maximum = 1000)
    {
        var events = ImmutableArray.CreateBuilder<CoreEvent>();
        for (var step = 0; step < maximum; step++)
        {
            var result = session.Advance(1);
            if (result.Fault is not null)
                Assert.Fail($"{result.Fault.Code}: {result.Diagnostic?.Message}\n{result.Diagnostic?.StackTrace}");
            var batch = result.Events.Select(envelope => envelope.Event).ToArray();
            events.AddRange(batch);
            if (batch.Any(condition)) return events.ToImmutable();
        }
        Assert.Fail("未在限定步数内到达战斗事件。");
        return [];
    }

    internal static SubmissionResult.Completed Accepted(SubmissionResult result)
    {
        var completed = (SubmissionResult.Completed)result;
        Assert.IsInstanceOfType<CommandDecision.Accepted>(completed.Receipt.Decision);
        return completed;
    }

    private static SettlementTypeProjection Town => new(new("fixture:combat-town"), WorldFixture.Revision,
        1, [WorldFixture.Plain], 10, 10);

    private static SettlementTemplateProjection Template(bool armedBuilding) => new(
        new("fixture:combat-template"), Town.Id, 1,
        armedBuilding
            ? [new(1, PlotSize.SmallStation, WorldFixture.Station), new(2, PlotSize.Small, ArmedBuilding)]
            : [new(1, PlotSize.SmallStation, WorldFixture.Station)],
        [], [], [], WorldFixture.PlatformLength);

    private static DesignProjection Design(DesignHash hash, DesignKind kind, ImmutableArray<PartCapability> capabilities)
        => new(hash, new(new string('E', 64)), kind,
            [new(new(0), new CoreDefinitionReference.Voxel(WorldFixture.Material),
                new GoodId.Voxel(WorldFixture.Material), 100, 1, new(1), capabilities, false)],
            100, 1, 1, new(1), [], [], 0, [], true, []);
}
