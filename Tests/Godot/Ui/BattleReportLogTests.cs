using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Core.Contracts;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class BattleReportLogTests
{
    [TestMethod]
    public void A_report_copies_the_combat_ended_summary_and_ignores_hits()
    {
        var log = new BattleReportLog();
        var session = new HostBinding(Guid.NewGuid());
        var location = new WorldLocation.Edge(new(4), 100);
        var vehicle = new InstancePresentationRef(new InstanceId.Vehicle(new(7)), new("wagon"), location);
        var summary = new CombatSummary(10, 20, [new CombatSideSummary(new CombatOwner.Player(),
            [new DestroyedUnit.Instance(vehicle)], [new DestroyedPart(vehicle, new(3))],
            [new GoodsQuantity(new GoodId.Voxel(new("coal")), 4)], new(9))]);
        var ended = new CoreEvent.CombatEnded(new(5), location, summary);
        var hit = new CoreEvent.CombatHits(new(5), location, [new(new AttackSource.Weapon(vehicle, new(1)),
            new HitTarget.Structure(vehicle), 999)]);
        log.Observe(session, [new(new(20, 0), 0, hit), new(new(20, 0), 1, ended)]);
        Assert.AreEqual(1, log.Reports.Count);
        var text = BattleReportLog.Describe(log.Reports[0]);
        StringAssert.Contains(text, "线路 4");
        StringAssert.Contains(text, "100");
        StringAssert.Contains(text, "开始步：10");
        StringAssert.Contains(text, "结束步：20");
        StringAssert.Contains(text, "车辆 7");
        StringAssert.Contains(text, "wagon");
        StringAssert.Contains(text, "部件 3");
        StringAssert.Contains(text, "coal");
        StringAssert.Contains(text, "× 4");
        StringAssert.Contains(text, "奖励：9");
        Assert.IsFalse(text.Contains("999", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains('\r'));
        log.Observe(session, []);
        Assert.AreEqual(1, log.Reports.Count);
    }

    [TestMethod]
    public void Changing_binding_clears_reports_and_loading_the_old_session_does_not_restore_them()
    {
        var log = new BattleReportLog();
        var played = new HostBinding(Guid.NewGuid());
        var loaded = new HostBinding(Guid.NewGuid());
        var ended = new CoreEvent.CombatEnded(new(5), new WorldLocation.Node(new(1)), new(1, 2, []));
        log.Observe(played, [new(new(2, 0), 0, ended)]);
        Assert.AreEqual(1, log.Reports.Count);
        log.Observe(loaded, []);
        Assert.AreEqual(0, log.Reports.Count);
        log.Observe(played, []);
        Assert.AreEqual(0, log.Reports.Count);
    }
}
