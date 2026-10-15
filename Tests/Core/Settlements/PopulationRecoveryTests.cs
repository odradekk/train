using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Settlements;
using Train.Core.Tests.World;
using static Train.Core.Tests.Settlements.PopulationFixture;

namespace Train.Core.Tests.Settlements;

[TestClass]
public sealed class PopulationRecoveryTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Restore_tracks_new_current_food_before_the_first_period_and_round_trips_its_references(bool missingGood)
    {
        var original = Content(Rule(), []);
        using var session = Start(original);
        var saved = Capture(session);
        var retainedPayload = saved.Encode();
        var retainedCheckpoint = saved.Checkpoint;
        var replacement = Good("replacement-food");
        var reference = new CoreDefinitionReference.Voxel(((GoodId.Voxel)replacement).Definition);
        var current = WithReplacementFood(original, replacement) with
        {
            Goods = missingGood ? original.Goods : original.Goods.Add(original.Goods[0] with { Good = replacement }),
            MissingReferences = missingGood ? [new(reference, "fixture")] : []
        };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, current, WorldFixture.Options));
        Assert.IsTrue(candidate.References.Definitions.Any(r => r.Definition == reference));
        Assert.AreEqual(missingGood, candidate.MissingReferences.Any(r => r.Reference == reference));
        using var restored = candidate.Activate();
        var capture = Capture(restored);
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode()));
        Assert.AreEqual(capture.Encode(), decoded.Encode());
        Assert.AreEqual(capture.Checkpoint, decoded.Checkpoint);
        Assert.AreEqual(retainedPayload, saved.Encode());
        Assert.AreEqual(retainedCheckpoint, saved.Checkpoint);
    }

    [TestMethod]
    public void Restore_tracks_current_food_and_preserves_old_food_until_the_started_period_finishes()
    {
        var original = Content(Rule() with { StatisticsPeriodTicks = 4 }, Stock(100, 100));
        using var session = Start(original);
        Advance(session, 1);
        var saved = Capture(session);
        var retainedPayload = saved.Encode();
        var replacement = Good("replacement-food");
        var current = WithReplacementFood(original, replacement) with
        {
            Goods = original.Goods.Add(original.Goods[0] with { Good = replacement })
        };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, current, WorldFixture.Options));
        foreach (var good in new[] { Food, replacement })
        {
            var reference = new CoreDefinitionReference.Voxel(((GoodId.Voxel)good).Definition);
            Assert.IsTrue(candidate.References.Definitions.Any(r => r.Definition == reference));
        }
        Assert.IsEmpty(candidate.MissingReferences);
        using var restored = candidate.Activate();
        var capture = Capture(restored);
        Assert.AreEqual(capture.Encode(), WorldFixture.Success(CoreFactory.Create().DecodeState(capture.Encode())).Encode());
        var expected = Reports(Advance(session, 3)).Single();
        var actual = Reports(Advance(restored, 3)).Single();
        Assert.AreEqual(expected, actual);
        Assert.AreEqual(1, actual.BasicSatisfaction);
        Assert.AreEqual(Quantity(session.Read(), Food), Quantity(restored.Read(), Food));
        Assert.AreEqual(retainedPayload, saved.Encode());
    }

    private static CoreContentProjection WithReplacementFood(CoreContentProjection original, GoodId replacement)
        => original with
        {
            Identity = Identity('A'),
            PopulationRules = [original.PopulationRules[0] with
            {
                Revision = new(new string('F', 64)),
                ConsumptionGoods = original.PopulationRules[0].ConsumptionGoods.Select(g => g.Good == Food
                    ? g with { Good = replacement } : g).ToImmutableArray()
            }]
        };

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Midperiod_save_preserves_consumption_and_signed_population_fractions(bool growth)
    {
        var content = Content(Rule() with { BaseGrowthPerSecond = 1, MaximumLossPerSecond = 1 }, growth ? Stock(100, 100) : []);
        using var continuous = Start(content);
        Advance(continuous, 3);
        var saved = Capture(continuous);
        var decoded = WorldFixture.Success(CoreFactory.Create().DecodeState(saved.Encode()));
        Assert.AreEqual(saved.Encode(), decoded.Encode());
        Assert.AreEqual(saved.Checkpoint, decoded.Checkpoint);
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(decoded, content, WorldFixture.Options));
        using var restored = candidate.Activate();
        var expected = Advance(continuous, 9);
        var actual = Advance(restored, 9);
        CollectionAssert.AreEqual(Reports(expected), Reports(actual));
        Assert.AreEqual(Capture(continuous).Encode(), Capture(restored).Encode());
        Assert.AreEqual(Quantity(continuous.Read(), Food), Quantity(restored.Read(), Food));
        Assert.IsTrue(Reports(actual).Any(r => r.Change != 0));
    }

    [TestMethod]
    public void Publication_and_commandless_tail_replay_through_the_production_session()
    {
        var original = Content(Rule() with { BaseGrowthPerSecond = 1, StatisticsPeriodTicks = 4 }, Stock(100, 100));
        using var session = Start(original);
        Advance(session, 3);
        var next = original with { Identity = Identity('A'), PopulationRules = [Rule() with { StatisticsPeriodTicks = 2, BaseGrowthPerSecond = 2 }] };
        Publish(session, next);
        var middle = Capture(session);
        Advance(session, 7);
        var end = Capture(session);
        var recording = session.ReadRecording();
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, recording.Starts[0].State.Encode(), recording.Commands,
            [middle.Checkpoint, end.Checkpoint], recording.EndPosition, null);
        using var replay = WorldFixture.Success(CoreFactory.Create().PrepareReplay(input, WorldFixture.Options.Runtime, [original, next]));
        foreach (var sample in new[] { middle, end })
        {
            var result = replay.Advance(100, sample.Position);
            Assert.IsNull(result.Difference);
            Assert.IsNull(result.Fault);
            Assert.AreEqual(sample.Encode(), replay.CaptureBoundary().Encode());
        }
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(0).Stop);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Missing_rule_or_frozen_good_disables_population_and_repair_resumes_the_same_period(bool missingRule)
    {
        var content = Content(Rule() with { StatisticsPeriodTicks = 4 }, Stock(100, 100));
        using var original = Start(content);
        Advance(original, 1);
        var missingReference = missingRule ? (CoreDefinitionReference)new CoreDefinitionReference.Rule(WorldFixture.Rules.Population)
            : new CoreDefinitionReference.Voxel(((GoodId.Voxel)Food).Definition);
        var missing = content with { Identity = Identity('A'), PopulationRules = missingRule ? [] : content.PopulationRules,
            Goods = missingRule ? content.Goods : content.Goods.Where(g => g.Good != Food).ToImmutableArray(),
            MissingReferences = [new(missingReference, "fixture")] };
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(Capture(original), missing, WorldFixture.Options));
        using var restored = (CoreSession)candidate.Activate();
        Assert.AreEqual(0, PopulationOperations.Begin(restored.State.World, missing, new(1)).AvailableLabor);
        var stock = Quantity(restored.Read(), Food);
        var disabled = Advance(restored, 8);
        Assert.IsEmpty(Reports(disabled));
        Assert.IsEmpty(disabled.OfType<CoreEvent.PopulationShortageChanged>().ToArray());
        Assert.AreEqual(1, disabled.OfType<CoreEvent.PopulationAvailabilityChanged>().Count(e => !e.Available));
        Assert.AreEqual(stock, Quantity(restored.Read(), Food));
        var repaired = content with { Identity = Identity('B') };
        Publish(restored, repaired);
        var repairedEvents = Advance(restored, 2);
        Assert.IsEmpty(Reports(repairedEvents));
        Assert.AreEqual(1, repairedEvents.OfType<CoreEvent.PopulationAvailabilityChanged>().Count(e => e.Available));
        var resumed = Advance(restored, 1);
        Assert.AreEqual(1, Reports(resumed).Length);
        var expected = Reports(Advance(original, 3)).Single();
        var actual = Reports(resumed).Single();
        Assert.AreEqual(expected.Before, actual.Before);
        Assert.AreEqual(expected.After, actual.After);
        Assert.AreEqual(expected.BasicSatisfaction, actual.BasicSatisfaction);
        Assert.AreEqual(Quantity(original.Read(), Food), Quantity(restored.Read(), Food));
    }

    [TestMethod]
    public void Publication_cannot_remove_a_good_still_needed_by_the_frozen_period()
    {
        var content = Content(Rule() with { StatisticsPeriodTicks = 4 }, []);
        using var session = Start(content);
        Advance(session, 1);
        var replacement = Good("replacement-food");
        var next = content with { Identity = Identity('A'),
            Goods = content.Goods.Where(g => g.Good != Food).Append(content.Goods[0] with { Good = replacement }).ToImmutableArray(),
            PopulationRules = [Rule() with { ConsumptionGoods = Rule().ConsumptionGoods.Select(g => g.Good == Food ? g with { Good = replacement } : g).ToImmutableArray() }] };
        var before = Capture(session);
        var result = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(next), CommandSource.AutomationTest);
        var rejected = (CommandDecision.Rejected)result.Receipt.Decision;
        Assert.IsTrue(rejected.Problems.Any(p => p.Code == "content_missing"));
        Assert.AreEqual(content.Identity, session.Read().InputIdentity);
        Assert.IsEmpty(result.Events);
        Assert.AreEqual(before.References.Definitions.Length, Capture(session).References.Definitions.Length);
        Assert.AreEqual(WorldFixture.Revision, Reports(Advance(session, 3)).Single().ExecutionRevision);
    }

    [TestMethod]
    public void Conversion_changes_keep_remaining_goods_in_physical_units()
    {
        var rule = Rule() with { Needs = Rule().Needs.Select(n => n.Need == PopulationNeed.Food
            ? n with { AmountPerPersonSecond = 1.875 } : n).ToImmutableArray() };
        var content = Content(rule, [new(Food, 1), new(Clothing, 10)]);
        using var session = Start(content);
        Advance(session, 2);
        var next = content with { Identity = Identity('A'), PopulationRules = [rule with
        {
            Revision = new(new string('F', 64)),
            ConsumptionGoods = rule.ConsumptionGoods.Select(g => g.Good == Food ? g with
                { Satisfies = [new(PopulationNeed.Food, 2)] } : g).ToImmutableArray()
        }] };
        Publish(session, next);
        var report = Reports(Advance(session, 2)).Single();
        Assert.AreEqual(1, report.BasicSatisfaction);
        Assert.AreEqual(0L, Quantity(session.Read(), Food));
    }

    [TestMethod]
    public void Retired_remaining_goods_survive_missing_definitions_and_resume_without_refund()
    {
        var original = Content(Rule(), [new(Food, 1), new(Clothing, 10)]);
        using var session = Start(original);
        Advance(session, 2);
        var replacement = Good("replacement-food");
        var changed = WithReplacementFood(original, replacement) with
            { Goods = original.Goods.Add(original.Goods[0] with { Good = replacement }) };
        Publish(session, changed);
        Advance(session, 2);
        var removed = changed with { Identity = Identity('B'), Goods = changed.Goods.Where(g => g.Good != Food).ToImmutableArray() };
        Publish(session, removed);
        var saved = WorldFixture.Success(CoreFactory.Create().DecodeState(Capture(session).Encode()));
        using var candidate = WorldFixture.Success(CoreFactory.Create().PrepareRestore(saved, removed, WorldFixture.Options));
        using var restored = candidate.Activate();
        var missing = Advance(restored, 2);
        Assert.IsFalse(missing.OfType<CoreEvent.PopulationAvailabilityChanged>().Any(e => !e.Available));
        Assert.AreEqual(1, Reports(missing).Length);
        Publish(restored, original with { Identity = Identity('C') });
        var resumed = Advance(restored, 2);
        Assert.AreEqual(1, Reports(resumed).Single().BasicSatisfaction);
        Assert.IsTrue(resumed.OfType<CoreEvent.PopulationShortageChanged>().Any(e => e.Need == PopulationNeed.Food && !e.Shortage));
        Assert.AreEqual(0L, Quantity(restored.Read(), Food));
    }
}
