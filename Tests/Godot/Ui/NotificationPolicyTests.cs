using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Notifications;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class NotificationPolicyTests
{
    [TestMethod]
    public void Defaults_slow_only_encounters_and_joined_units_count_as_encounters()
    {
        var policy = new NotificationPolicy();
        foreach (var category in Enum.GetValues<NotificationCategory>())
            Assert.AreEqual(NotificationPolicy.DefaultAction(category), policy.Action(category));
        Assert.AreEqual(NotificationAction.SlowToOne, policy.Action(NotificationCategory.Encounter));
        var started = policy.Classify(new CoreEvent.CombatStarted(new(4), new WorldLocation.Node(new(1)), []), null, null);
        var joined = policy.Classify(new CoreEvent.CombatJoined(new(4), new CombatUnitId.CreatureGroup(new(2))), null, null);
        Assert.AreEqual(NotificationCategory.Encounter, started!.Category);
        Assert.AreEqual(NotificationLevel.Warning, started.Level);
        Assert.AreEqual(NotificationCategory.Encounter, joined!.Category);
        Assert.AreEqual(NotificationAction.SlowToOne, policy.Decide(new CoreEvent.CombatJoined(new(4), new CombatUnitId.CreatureGroup(new(2)))));
        Assert.IsNull(policy.Classify(new CoreEvent.CombatHits(new(4), new WorldLocation.Node(new(1)), []), null, null));
        Assert.IsNull(policy.Classify(new CoreEvent.LairCreated(new(1), new(1), new("lair")), null, null));
        Assert.IsNull(policy.Classify(new CoreEvent.RunReported(Report(RunEndReason.PlanCompleted)), null, null));
        Assert.IsNull(policy.Classify(new CoreEvent.SettlementRelationChanged(new(1),
            SettlementRelation.Hostile, SettlementRelation.NonHostile), null, null));
    }

    [TestMethod]
    public void Each_action_is_returned_for_its_category_and_level_does_not_change_it()
    {
        var policy = new NotificationPolicy();
        var hostile = Destruction(new InstanceId.Building(new(3)));
        var previous = View((new InstanceId.Building(new(3)), SettlementRelation.Hostile));
        Assert.AreEqual(NotificationLevel.Information, policy.Classify(hostile, previous, null)!.Level);
        Assert.AreEqual(NotificationAction.None, policy.Decide(hostile));
        policy.SetAction(NotificationCategory.Destruction, NotificationAction.Pause);
        Assert.AreEqual(NotificationAction.Pause, policy.Decide(hostile));
        policy.SetAction(NotificationCategory.Encounter, NotificationAction.None);
        Assert.AreEqual(NotificationAction.None, policy.Decide(new CoreEvent.CombatStarted(new(1), new WorldLocation.Node(new(1)), [])));
        policy.SetAction(NotificationCategory.Encounter, NotificationAction.SlowToOne);
        Assert.AreEqual(NotificationAction.SlowToOne, policy.Decide(new CoreEvent.CombatStarted(new(1), new WorldLocation.Node(new(1)), [])));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Pause_beats_slow_in_either_event_order(bool reverse)
    {
        var policy = new NotificationPolicy();
        policy.SetAction(NotificationCategory.Destruction, NotificationAction.Pause);
        CoreEvent slow = new CoreEvent.CombatStarted(new(1), new WorldLocation.Node(new(1)), []);
        CoreEvent pause = Destruction(new InstanceId.Vehicle(new(1)));
        var action = NotificationAction.None;
        foreach (var item in reverse ? new[] { pause, slow } : new[] { slow, pause })
        {
            var next = policy.Decide(item);
            if (next > action) action = next;
        }
        Assert.AreEqual(NotificationAction.Pause, action);
    }

    [TestMethod]
    public void Destruction_level_uses_the_previous_boundary_ownership()
    {
        var policy = new NotificationPolicy();
        var owned = new InstanceId.Vehicle(new(7));
        var hostile = new InstanceId.Building(new(3));
        var missing = new InstanceId.Building(new(9));
        Assert.AreEqual(NotificationLevel.Severe, policy.Classify(Destruction(owned), null, null)!.Level);
        var previous = View((owned, SettlementRelation.PlayerOwned), (hostile, SettlementRelation.Hostile));
        Assert.AreEqual(NotificationLevel.Severe, policy.Classify(Destruction(owned), previous, null)!.Level);
        Assert.AreEqual(NotificationLevel.Information, policy.Classify(Destruction(hostile), previous, null)!.Level);
        Assert.AreEqual(NotificationLevel.Severe, policy.Classify(new CoreEvent.CombatDestruction(new(1),
            new WorldLocation.Node(new(1)), [new(InstanceRef(hostile), new(2))], [new DestroyedUnit.Instance(InstanceRef(owned))]), previous, null)!.Level);
        var missingView = new FixedCoreReadView
        {
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>> { [missing] = new Lookup<InstanceView>.Missing() }
        };
        Assert.AreEqual(NotificationLevel.Severe, policy.Classify(Destruction(missing), missingView, null)!.Level);
        var creature = new CoreEvent.CombatDestruction(new(1), new WorldLocation.Edge(new(1), 100), [],
            [new DestroyedUnit.Creature(new(new(1), new(2), new("wolf"), new WorldLocation.Edge(new(1), 100)))]);
        Assert.AreEqual(NotificationLevel.Information, policy.Classify(creature, null, null)!.Level);
    }

    [TestMethod]
    public void Relation_and_total_loss_use_the_approved_levels_and_text()
    {
        var policy = new NotificationPolicy();
        var owned = policy.Classify(new CoreEvent.SettlementRelationChanged(new(4),
            SettlementRelation.NonHostile, SettlementRelation.PlayerOwned), null, null);
        var hostile = policy.Classify(new CoreEvent.SettlementRelationChanged(new(4),
            SettlementRelation.NonHostile, SettlementRelation.Hostile), null, null);
        var lost = policy.Classify(new CoreEvent.RunReported(Report(RunEndReason.AllVehiclesDestroyed)), null, null);
        var ended = policy.Classify(new CoreEvent.CombatEnded(new(4), new WorldLocation.Node(new(1)), new(1, 2, [])), null, null);
        Assert.AreEqual(NotificationLevel.Completion, owned!.Level);
        Assert.AreEqual(NotificationLevel.Warning, hostile!.Level);
        Assert.AreEqual(NotificationLevel.Severe, lost!.Level);
        Assert.AreEqual(NotificationPolicy.RunDestroyedText, lost.Text);
        Assert.IsNull(ended!.Subject);
        Assert.AreEqual(new WorldLocation.Node(new(1)), ended.Location);
        Assert.AreEqual(NotificationAction.None, policy.Decide(new CoreEvent.RunReported(Report(RunEndReason.AllVehiclesDestroyed))));
    }

    [TestMethod]
    public void Joined_combat_keeps_the_summary_location()
    {
        var edge = new WorldLocation.Edge(new(3), 100);
        var view = new FixedCoreReadView
        {
            World = new(1, new("fixture:railway"), 1, 1, [], [], [], [], [new(new(8), edge)])
        };
        var notice = new NotificationPolicy().Classify(new CoreEvent.CombatJoined(new(8), new CombatUnitId.CreatureGroup(new(1))), null, view);
        Assert.AreEqual(edge, notice!.Location);
        Assert.AreEqual(new WorldObjectId.Combat(new(8)), notice.Subject);
    }

    private static CoreEvent.CombatDestruction Destruction(InstanceId id) => new(new(1), new WorldLocation.Node(new(1)),
        [new(InstanceRef(id), new(4))], []);

    private static InstancePresentationRef InstanceRef(InstanceId id) =>
        new(id, new("design"), new WorldLocation.Node(new(1)));

    private static FixedCoreReadView View(params (InstanceId Id, SettlementRelation Ownership)[] instances) => new()
    {
        Instances = instances.ToDictionary(item => item.Id, item => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(Instance(item.Id, item.Ownership)))
    };

    private static InstanceView Instance(InstanceId id, SettlementRelation ownership) => new(id, new("design"), new("composition"),
        new WorldLocation.Node(new(1)), ownership,
        id is InstanceId.Vehicle vehicle ? new InstanceDetails.Vehicle(SizeClass.Small, new(vehicle.Id.Value))
            : new InstanceDetails.Building(new(1), PlotSize.Small),
        InstanceLock.None, new(10, 10), [], []);

    private static RunReport Report(RunEndReason reason) => new(new(7), new(1, 0), reason,
        new(new(0, 0), new(0), new(0), new(0), 0, 0, null, []));
}
