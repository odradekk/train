using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Warehouse_keeps_the_pressed_rent_price_then_renews_at_the_core_price()
    {
        LeaseRequest rent = new LeaseRequest.Rent(new(1), 3, EconomyFixture.LeaseDuration);
        LeaseRequest renew = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration);
        Dictionary<LeaseRequest, CoreResult<LeaseQuote>> Quotes(ulong revision, long rentPrice) => new()
        {
            [rent] = new CoreResult<LeaseQuote>.Success(new(rent, new(revision), new(rentPrice), 3, [])),
            [renew] = new CoreResult<LeaseQuote>.Success(new(renew, new(revision), new(953), 20, [])),
            [new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 2)] = new CoreResult<LeaseQuote>.Success(new(new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 2), new(revision), new(619), 20, [])),
            [new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 3)] = new CoreResult<LeaseQuote>.Success(new(new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 3), new(revision), new(731), 20, []))
        };
        var before = EconomyFixture.Create(leaseCount: 0, leaseQuotes: Quotes(1, 827));
        var changed = EconomyFixture.Create(2, leaseCount: 0, leaseQuotes: Quotes(2, 109));
        var rented = EconomyFixture.Create(3, leaseQuotes: Quotes(3, 109));
        var renewed = EconomyFixture.Create(4, leaseQuotes: Quotes(4, 109), leaseExpiresAt: EconomyFixture.LeaseDuration * 2);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content(mapPresentation: true));
        setup.Session.QueueAdvance(new(1, changed.Position, [], null, null), changed);
        setup.Session.QueueSubmit(new CoreCommand.AcquireLease(rent, new(827)), CommandSource.UserInterface,
            Completed(1, changed, rented, new CommandDecision.Accepted([], [])), rented);
        var lease = ((Lookup<LeaseView>.Found)renewed.FindLease(new(1))).Value;
        setup.Session.QueueSubmit(new CoreCommand.AcquireLease(renew, new(953)), CommandSource.UserInterface,
            new SubmissionResult.Completed(new(2, rented.Position, renewed.Position, new CommandDecision.Accepted([], [])),
                [new(renewed.Position, 0, new CoreEvent.LeaseAcquired(lease, new(907), true))], null), renewed);
        await Run(setup, async (ui, panel) =>
        {
            await Page(panel, 3);
            await _input.Enter(Find<LineEdit>(panel, "LeaseCapacity"), "3");
            var host = ui.GetParent<Train.App.App>().Host!;
            await _input.ClickAcrossRevision(Find<Button>(panel, "ConfirmLease"), () =>
            {
                Require(host.Step(1).Problem is null, "不能发布租赁确认修订。");
                Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, changed, host.Content!);
                Require(Find<Button>(panel, "ConfirmLease").Text.Contains("827"), "按下期间应保留玩家看到的付款上限。");
            });
            await _input.WaitUntil(() => ReferenceEquals(ui.View, rented));
            await _input.Choose(Find<OptionButton>(panel, "Lease"), 1);
            Require(Find<Button>(panel, "ConfirmLease").Text.Contains("953") &&
                Find<Label>(panel, "LeaseQuote").Text.Contains("第3天 00:00"), "有效租约续租应从原到期时间追加，并使用核心金额。");
            await _input.Activate(Find<Button>(panel, "ConfirmLease"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, renewed));
            Require(Find<Label>(panel, "LeaseDetails").Text.Contains("5/20") && Find<Label>(panel, "LeaseDetails").Text.Contains("第3天 00:00"),
                "续租后应保留原容量并读取实际到期时间。");
            Require(Find<Label>(panel, "LeaseResult").Text.Contains("907") && !Find<Label>(panel, "LeaseResult").Text.Contains("953"),
                "续租反馈应使用实际付款。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Warehouse_rejection_stays_at_the_form_and_does_not_retry()
    {
        var before = EconomyFixture.Create();
        var request = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content(mapPresentation: true));
        setup.Session.QueueSubmit(new CoreCommand.AcquireLease(request, new(419)), CommandSource.UserInterface,
            Completed(1, before, before, new CommandDecision.Rejected([
                new("insufficient_money", "Wallet", new WorldObjectId.Inventory(new(11)), ["fixture:rejected"])])), before);
        await Run(setup, async (ui, panel) =>
        {
            await Page(panel, 3);
            await _input.Activate(Find<Button>(panel, "ConfirmLease"));
            var text = Find<Label>(panel, "LeaseResult").Text;
            Require(text.Contains("资金不足") && !text.Contains("库存 11") && !text.Contains("fixture:") && ui.ErrorText.Length == 0,
                $"续租拒绝应就地说明原因并隐藏内部身份：原处={text}，全局={ui.ErrorText}。");
            await _input.Frames(3);
            Require(setup.Session.SubmitCount == 1 && Find<OptionButton>(panel, "Lease").Selected == 1,
                "续租拒绝后应保留选择，等待玩家重试。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Expired_warehouse_renewal_starts_from_the_current_game_time()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(completedSteps: 7200, leaseExpiresAt: 3600), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            await Page(panel, 3);
            Require(Find<Label>(panel, "LeaseDetails").Text.Contains("已到期") &&
                Find<Label>(panel, "LeaseQuote").Text.Contains("第2天 00:02"), "已到期仓库续租应从当前完成时间起算。");
        });
        setup.Session.AssertComplete();
    }
}
