using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.World;
using Train.Core.World;

namespace Train.Core.Tests.Combat;

[TestClass]
public sealed class CombatTargetingTests
{
    [TestMethod]
    public void Armed_first_prefers_the_armed_building()
    {
        using var session = CombatFixture.Session(damage: 1);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var target = TrainTarget(session, train, events);
        Assert.AreEqual(CombatFixture.ArmedBuilding, Design(session, target));
    }

    [TestMethod]
    public void Lowest_and_highest_durability_use_effective_structure()
    {
        using var low = DamagedSession(TargetPriority.LowestDurability);
        var hostile = CombatFixture.HostileNode(low);
        var station = Station(low, hostile);
        Assert.AreEqual(station, TrainTarget(low, new TrainId(1), CombatEvents(low)));

        using var high = DamagedSession(TargetPriority.HighestDurability);
        var builder = BuilderBuilding(high, hostile);
        Assert.AreEqual(builder, TrainTarget(high, new TrainId(1), CombatEvents(high)));
    }

    [TestMethod]
    public void Focus_fire_overrides_the_priority_for_the_next_shot()
    {
        var content = SettlementDamage(50);
        using var session = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var combat = session.Read().World.Combats.Single();
        var station = Station(session, hostile);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0)), CommandSource.AutomationTest));
        CombatFixture.Accepted(session.Submit(new CoreCommand.FocusFire(combat.Id, new CombatController.Train(train),
            new WorldObjectId.Instance(station)), CommandSource.AutomationTest));
        var next = session.Advance(60);
        Assert.IsNull(next.Fault);
        var hits = next.Events.Select(envelope => envelope.Event).OfType<CoreEvent.CombatHits>().ToArray();
        Assert.IsTrue(hits.Length > 0);
        var trainInstance = new InstanceId.Vehicle(Train(session, train).Vehicles[0]);
        var hit = hits.SelectMany(batch => batch.Hits).First(value => value.Source is AttackSource.Weapon weapon
            && weapon.Instance.Instance == trainInstance);
        Assert.AreEqual(station, Target(hit.Target));
    }

    [TestMethod]
    public void Ties_choose_the_canonical_unit_order()
    {
        using var session = CombatFixture.SessionWithContent(SettlementDamage(100));
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.LowestDurability, TargetScope.All, EncounterAction.Engage, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var expected = new InstanceId.Building(new BuildingId(((Lookup<LocationView>.Found)session.Read().FindLocation(
                new WorldLocation.Node(hostile))).Value.Instances.OfType<InstanceId.Building>()
            .Min(instance => instance.Id.Value)));
        Assert.AreEqual(expected, TrainTarget(session, train, events));
    }

    [TestMethod]
    public void Focus_fire_rejects_missing_controllers_and_invalid_targets()
    {
        using var session = CombatFixture.SessionWithContent(CombatFixture.Content(damage: 1));
        var hostile = CombatFixture.HostileNode(session);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(TargetPriority.ArmedFirst, TargetScope.All, EncounterAction.Engage, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var combat = session.Read().World.Combats.Single();
        var own = new WorldObjectId.Instance(new InstanceId.Vehicle(Train(session, train).Vehicles[0]));
        var other = OtherHostileArmedBuilding(session, hostile);
        AssertRejected(session, new CoreCommand.FocusFire(combat.Id, new CombatController.Train(new(999)), own),
            "object_missing", "Controller");
        AssertRejected(session, new CoreCommand.FocusFire(combat.Id, new CombatController.Train(train), own),
            "invalid_state", "Target");
        AssertRejected(session, new CoreCommand.FocusFire(combat.Id, new CombatController.Train(train),
            new WorldObjectId.Instance(other)), "invalid_state", "Target");
    }

    private static void AssertRejected(ICoreSession session, CoreCommand command, string code, string field)
    {
        var result = (SubmissionResult.Completed)session.Submit(command, CommandSource.AutomationTest);
        var rejected = (CommandDecision.Rejected)result.Receipt.Decision;
        Assert.AreEqual(code, rejected.Problems.Single().Code);
        Assert.AreEqual(field, rejected.Problems.Single().Field);
        Assert.IsNull(session.Read().Fault);
    }

    private static InstanceId OtherHostileArmedBuilding(CoreSession session, NodeId combatNode)
    {
        var settlement = session.State.World.Settlements.Where(item => item.Node != combatNode
            && item.Relation == SettlementRelation.Hostile).OrderBy(item => item.Node.Value).FirstOrDefault();
        if (settlement is null) Assert.Fail("夹具世界没有第二个敌对聚居地。");
        return settlement.Plots.Select(plot => plot.Building).OfType<BuildingId>()
            .Select(building => (InstanceId)new InstanceId.Building(building))
            .Single(instance => Instance(session, instance).Design == CombatFixture.ArmedBuilding);
    }

    private static CoreSession DamagedSession(TargetPriority priority)
    {
        var content = CombatFixture.Content(damage: 1, builder: true) with
        {
            Designs = CombatFixture.Content(damage: 1, builder: true).Designs.Select(design =>
                design.Design == CombatFixture.ArmedBuilding ? design with { MaxStructureDurability = 1000 }
                : design.Design == CombatFixture.BuilderBuilding ? design with { MaxStructureDurability = 500 }
                : design).ToImmutableArray()
        };
        var original = CombatFixture.SessionWithContent(content);
        var hostile = CombatFixture.HostileNode(original);
        var station = Station(original, hostile);
        var armed = ArmedBuilding(original, hostile);
        var builder = BuilderBuilding(original, hostile);
        var state = original.State;
        original.Dispose();
        var damaged = state.World.Instances.Select(instance => instance.Id == station
            ? instance with { StructureDurability = 50 }
            : instance.Id == armed ? instance with { StructureDurability = 100 }
            : instance.Id == builder ? instance with { StructureDurability = 300 }
            : instance).ToImmutableArray();
        var session = new CoreSession(state with { World = state.World with { Instances = damaged } });
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(priority, TargetScope.All, EncounterAction.Engage, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, hostile, train);
        return session;
    }

    private static CoreContentProjection SettlementDamage(long stationStructure) => CombatFixture.Content(damage: 1) with
    {
        Designs = CombatFixture.Content(damage: 1).Designs.Select(design => design.Design != WorldFixture.Station
            ? design : design with { MaxStructureDurability = stationStructure }).ToImmutableArray()
    };

    private static CoreEvent[] CombatEvents(CoreSession session)
    {
        var events = CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        return events.ToArray();
    }

    private static InstanceId TrainTarget(ICoreSession session, TrainId train, IEnumerable<CoreEvent> events)
    {
        var instance = new InstanceId.Vehicle(Train(session, train).Vehicles[0]);
        var hit = events.OfType<CoreEvent.CombatHits>().SelectMany(batch => batch.Hits)
            .First(value => value.Source is AttackSource.Weapon weapon && weapon.Instance.Instance == instance);
        return Target(hit.Target);
    }

    private static InstanceId Station(ICoreSession session, NodeId node)
        => Instance(session, node, WorldFixture.Station);

    private static InstanceId ArmedBuilding(ICoreSession session, NodeId node)
        => Instance(session, node, CombatFixture.ArmedBuilding);

    private static InstanceId BuilderBuilding(ICoreSession session, NodeId node)
        => Instance(session, node, CombatFixture.BuilderBuilding);

    private static InstanceId Instance(ICoreSession session, NodeId node, DesignHash design)
        => ((Lookup<LocationView>.Found)session.Read().FindLocation(new WorldLocation.Node(node))).Value.Instances
            .Single(instance => Instance(session, instance).Design == design);

    private static InstanceId Target(HitTarget target) => target switch
    {
        HitTarget.Structure structure => structure.Instance.Instance,
        HitTarget.Part part => part.Instance.Instance,
        _ => throw new AssertFailedException("不支持的目标。")
    };

    private static DesignHash Design(ICoreSession session, InstanceId instance)
        => ((Lookup<InstanceView>.Found)session.Read().FindInstance(instance)).Value.Design;

    private static InstanceView Instance(ICoreSession session, InstanceId instance)
        => ((Lookup<InstanceView>.Found)session.Read().FindInstance(instance)).Value;

    private static TrainView Train(ICoreSession session, TrainId id)
        => ((Lookup<TrainView>.Found)session.Read().FindTrain(id)).Value;
}
