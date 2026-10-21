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
        Assert.IsNull(policy.Classify(new CoreEvent.RunReported(Report(RunEndReason.LoopCompleted)), null, null));
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

    [TestMethod]
    public void Economy_railway_and_population_events_use_their_categories_levels_and_jump_targets()
    {
        var policy = new NotificationPolicy();
        var settlement = new WorldObjectId.Settlement(new(4));
        var train = new WorldObjectId.Train(new(7));
        var ore = new GoodId.Voxel(new("ore"));
        var job = new ConstructionJobView(new(5), ConstructionKind.Construction, new(4),
            new(new(1), CapabilityKind.Construction), new ConstructionTarget.NewVehicle(new("design")),
            ConstructionState.ResourceBlocked, 0, 0, 0, 0, 0, [], [], []);
        var jobs = new FixedCoreReadView
        {
            Jobs = new Dictionary<JobId, Lookup<JobView>> { [new(5)] = new Lookup<JobView>.Found(new JobView.Construction(job)) },
            Edges = new Dictionary<EdgeId, Lookup<EdgeView>> { [new(3)] = new Lookup<EdgeView>.Missing() }
        };
        var expected = new (CoreEvent Event, NotificationCategory Category, NotificationLevel Level, WorldObjectId? Subject)[]
        {
            (new CoreEvent.RunReported(Report(RunEndReason.PlanCompleted)), NotificationCategory.TrainOperation,
                NotificationLevel.Completion, train),
            (new CoreEvent.TrainActivityChanged(new(7), TrainActivity.Running, TrainActivity.NoRoute, Position(), []),
                NotificationCategory.TrainOperation, NotificationLevel.Warning, train),
            (new CoreEvent.PopulationShortageChanged(new(4), PopulationNeed.Food, true), NotificationCategory.Population,
                NotificationLevel.Warning, settlement),
            (new CoreEvent.PopulationShortageChanged(new(4), PopulationNeed.Food, false), NotificationCategory.Population,
                NotificationLevel.Completion, settlement),
            (new CoreEvent.PopulationMinimumReached(new(4), 1), NotificationCategory.Population, NotificationLevel.Severe, settlement),
            (new CoreEvent.ConstructionCompleted(new(5), ConstructionKind.Construction, job.Target, new InstanceId.Vehicle(new(9)), []),
                NotificationCategory.Construction, NotificationLevel.Completion, new WorldObjectId.Instance(new InstanceId.Vehicle(new(9)))),
            (new CoreEvent.ConstructionCompleted(new(5), ConstructionKind.Refit,
                new ConstructionTarget.Existing(new InstanceId.Vehicle(new(8)), new("design")), null, []),
                NotificationCategory.Construction, NotificationLevel.Completion, new WorldObjectId.Instance(new InstanceId.Vehicle(new(8)))),
            (new CoreEvent.ConstructionCompleted(new(5), ConstructionKind.Deconstruction,
                new ConstructionTarget.Existing(new InstanceId.Vehicle(new(8)), new("design")), null, []),
                NotificationCategory.Construction, NotificationLevel.Completion, settlement),
            (new CoreEvent.ConstructionStateChanged(new(5), ConstructionState.Working, ConstructionState.ResourceBlocked, []),
                NotificationCategory.Construction, NotificationLevel.Warning, settlement),
            (new CoreEvent.ConstructionStateChanged(new(5), ConstructionState.Working, ConstructionState.Failed, []),
                NotificationCategory.Construction, NotificationLevel.Warning, settlement),
            (new CoreEvent.LeaseExpired(new(2), new(4), new(6)), NotificationCategory.Lease, NotificationLevel.Warning, settlement),
            (new CoreEvent.SettlementFounded(new(4), new(1), 100), NotificationCategory.Founding, NotificationLevel.Completion, settlement),
        };
        foreach (var (item, category, level, subject) in expected)
        {
            var notice = policy.Classify(item, jobs, jobs);
            Assert.IsNotNull(notice, item.ToString());
            Assert.AreEqual((category, level, subject), (notice.Category, notice.Level, notice.Subject), item.ToString());
            Assert.AreEqual(NotificationAction.None, policy.Decide(item), item.ToString());
        }
        var edgeNotices = new CoreEvent[]
        {
            new CoreEvent.RailBroken(new(3), new(new(1), 0, 120)), new CoreEvent.RailRepaired(new(3), new(1), 0),
            new CoreEvent.RailExpanded(new(3), 2), new CoreEvent.RailwayWorkChanged(new(3), new(6), [])
        }.Select(item => policy.Classify(item, jobs, jobs)!).ToArray();
        CollectionAssert.AreEqual(new[] { NotificationLevel.Warning, NotificationLevel.Completion, NotificationLevel.Completion,
            NotificationLevel.Warning }, edgeNotices.Select(item => item.Level).ToArray());
        Assert.IsTrue(edgeNotices.All(item => item.Category == NotificationCategory.Railway &&
            item.Location is WorldLocation.Edge { Id.Value: 3 }));
        Assert.AreEqual(new WorldLocation.Edge(new(3), 120), edgeNotices[0].Location);
        var exhausted = policy.Classify(new CoreEvent.ResourceExhausted(new(2), ore, false), null, null)!;
        var regenerated = policy.Classify(new CoreEvent.ResourceRegenerated(new(2), ore, 40), null, null)!;
        Assert.AreEqual((NotificationCategory.Resource, NotificationLevel.Warning, ore), (exhausted.Category, exhausted.Level, exhausted.Good));
        Assert.AreEqual((NotificationCategory.Resource, NotificationLevel.Information, ore),
            (regenerated.Category, regenerated.Level, regenerated.Good));
        Assert.AreEqual(new WorldLocation.Node(new(2)), exhausted.Location);
    }

    [TestMethod]
    public void New_categories_default_to_no_action_and_unstable_or_routine_events_do_not_notify()
    {
        var policy = new NotificationPolicy();
        foreach (var category in new[] { NotificationCategory.Population, NotificationCategory.Construction,
            NotificationCategory.Railway, NotificationCategory.TrainOperation, NotificationCategory.Resource,
            NotificationCategory.Lease, NotificationCategory.Founding })
            Assert.AreEqual(NotificationAction.None, policy.Action(category));
        var ore = new GoodId.Voxel(new("ore"));
        var silent = new CoreEvent[]
        {
            new CoreEvent.ResourceExhausted(new(2), ore, true),
            new CoreEvent.ResourceRecovered(new(2), ore, 1),
            new CoreEvent.TrainActivityChanged(new(7), TrainActivity.NoRoute, TrainActivity.Running, Position(), []),
            new CoreEvent.ConstructionStateChanged(new(5), ConstructionState.ResourceBlocked, ConstructionState.Working, []),
            new CoreEvent.ProductionStateChanged(new(new(1), CapabilityKind.Production), ProductionState.Running,
                ProductionState.WaitingForInputs, []),
            new CoreEvent.RunReported(Report(RunEndReason.PlanReplaced)),
        };
        foreach (var item in silent) Assert.IsNull(policy.Classify(item, null, null), item.ToString());
    }

    private static TrainPosition Position() =>
        new TrainPosition.Node(new(1), null, null, null, null, NodeTravelPhase.Docked, 0, null);

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
