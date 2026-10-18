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
        CombatFixture.Send(session, hostile, train);
        var events = CombatFixture.AdvanceUntil(session, view => view.World.Combats.Any());
        var target = TrainTarget(session, train, events);
        Assert.AreEqual(CombatFixture.ArmedBuilding, Design(session, target));
    }

    [TestMethod]
    public void Lowest_and_highest_durability_use_effective_structure()
    {
        using var low = Session(SettlementDamage(50), TargetPriority.LowestDurability);
        var lowTarget = TrainTarget(low, new TrainId(1), CombatEvents(low));
        Assert.AreEqual(50, Instance(low, lowTarget).Structure.EffectiveCurrent);

        using var high = Session(SettlementDamage(50), TargetPriority.HighestDurability);
        var highTarget = TrainTarget(high, new TrainId(1), CombatEvents(high));
        Assert.AreEqual(100, Instance(high, highTarget).Structure.EffectiveCurrent);
        Assert.AreNotEqual(lowTarget, highTarget);
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

    private static CoreSession Session(CoreContentProjection content, TargetPriority priority)
    {
        var session = CombatFixture.SessionWithContent(content);
        var train = new TrainId(1);
        CombatFixture.Accepted(session.Submit(new CoreCommand.SetTrainEngagement(train,
            new(priority, TargetScope.All, EncounterAction.Engage, 0)), CommandSource.AutomationTest));
        CombatFixture.Send(session, CombatFixture.HostileNode(session), train);
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
        => ((Lookup<LocationView>.Found)session.Read().FindLocation(new WorldLocation.Node(node))).Value.Instances
            .Single(instance => ((Lookup<InstanceView>.Found)session.Read().FindInstance(instance)).Value.Design
                == WorldFixture.Station);

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
