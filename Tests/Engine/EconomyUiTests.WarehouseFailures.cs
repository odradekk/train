using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Removed_warehouse_keeps_the_missing_selection_and_hides_old_cargo()
    {
        var before = EconomyFixture.Create();
        var removed = EconomyFixture.Create(2, leaseCount: 0);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        setup.Session.QueueAdvance(new(1, removed.Position, [], null, null), removed);
        await Run(setup, async (ui, panel) =>
        {
            await Page(panel, 3);
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "不能发布仓库移除修订。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, removed, host.Content!);
            Require(Find<OptionButton>(panel, "Lease").Selected == 1 && Find<Button>(panel, "ConfirmLease").Disabled &&
                Find<Label>(panel, "LeaseDetails").Text.Contains("已不存在") && !Find<VBoxContainer>(panel, "WarehouseGoods").IsVisibleInTree(),
                "仓库消失后应保留已失效的选择、隐藏旧货物并禁止续租。");
            Require(setup.Session.SubmitCount == 0, "失效的仓库不应提交命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Warehouse_quote_failures_explain_why_renting_is_unavailable_without_submitting()
    {
        var request = new LeaseRequest.Rent(new(1), 3, EconomyFixture.LeaseDuration);
        (CoreResult<LeaseQuote> Quote, string Reason)[] cases =
        [
            (new CoreResult<LeaseQuote>.Success(new(request, new(1), new(419), 3,
                [new("insufficient_money", "Wallet", new WorldObjectId.Inventory(new(11)), [])])), "资金不足"),
            (new CoreResult<LeaseQuote>.Failure([new("invalid_state", "lease", new WorldObjectId.Settlement(new(1)), ["fixture:failure"])]), "报价被拒绝"),
            (new CoreResult<LeaseQuote>.Faulted(new("fixture:fault", new(0, 1), "QuoteLease", "lease", null), null), "核心模拟发生故障")
        ];
        foreach (var item in cases)
        {
            var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(leaseCount: 0,
                leaseQuotes: new Dictionary<LeaseRequest, CoreResult<LeaseQuote>> { [request] = item.Quote }), EconomyFixture.Content());
            await Run(setup, async (_, panel) =>
            {
                await Page(panel, 3);
                await _input.Enter(Find<LineEdit>(panel, "LeaseCapacity"), "3");
                var text = Find<Label>(panel, "LeaseQuote").Text;
                Require(Find<Button>(panel, "ConfirmLease").Disabled && text.Contains(item.Reason) &&
                    !text.Contains("fixture:") && !text.Contains("聚居地 1") && !text.Contains("库存 11") && !text.Contains("QuoteLease"),
                    "报价失败应说明中文原因，隐藏内部身份并禁止提交。");
                Require(setup.Session.SubmitCount == 0, "报价失败不应提交租赁命令。");
            });
            setup.Session.AssertComplete();
        }
    }

    [Test]
    public async Task Missing_market_rule_explains_all_problems_without_exposing_rule_keys()
    {
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(missingMarketRule: true), EconomyFixture.Content(missingMarketRule: true));
        await Run(setup, async (_, panel) =>
        {
            await Page(panel, 3);
            var text = Find<Label>(panel, "LeaseQuote").Text;
            Require(text.Contains("市场规则缺失") && text.Contains("所需内容缺失") && text.Contains("状态不符合操作要求") &&
                !text.Contains("fixture:") && !text.Contains("聚居地 1") && !text.Contains("content_missing"), "缺少市场规则时应完整说明原因，隐藏代码和规则键。");
            Require(Find<Button>(panel, "ConfirmLease").Disabled && Find<OptionButton>(panel, "LeaseMultiple").Disabled &&
                !Find<LineEdit>(panel, "LeaseCapacity").Editable && setup.Session.SubmitCount == 0, "市场规则缺失时不能租赁。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Warehouse_renewal_rechecks_term_blockers_without_losing_the_selection()
    {
        LeaseRequest first = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration);
        LeaseRequest second = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 2);
        LeaseRequest third = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * 3);
        var before = EconomyFixture.Create();
        var changed = EconomyFixture.Create(2, leaseQuotes: new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>
        {
            [first] = new CoreResult<LeaseQuote>.Success(new(first, new(2), null, 3, [new("insufficient_capacity", "Request.Capacity", null, [])])),
            [second] = new CoreResult<LeaseQuote>.Success(new(second, new(2), null, 3, [new("invalid_state", "Request.DurationSteps", null, [])])),
            [third] = new CoreResult<LeaseQuote>.Failure([new("invalid_state", "lease", null, ["fixture:failure"])])
        });
        var faulted = EconomyFixture.Create(3, leaseQuotes: new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>
        {
            [first] = new CoreResult<LeaseQuote>.Success(new(first, new(3), new(251), 3, [])),
            [second] = new CoreResult<LeaseQuote>.Faulted(new("fixture:fault", new(0, 3), "QuoteLease", "lease", null), null),
            [third] = new CoreResult<LeaseQuote>.Success(new(third, new(3), new(503), 3, []))
        });
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, panel) =>
        {
            await Page(panel, 3);
            var terms = Find<OptionButton>(panel, "LeaseMultiple");
            await _input.Choose(terms, 1);
            var host = ui.GetParent<Train.App.App>().Host!;
            void Advance(Train.Core.ContractSupport.FixedCoreReadView next)
            {
                setup.Session.QueueAdvance(new(1, next.Position, [], null, null), next);
                Require(host.Step(1).Problem is null, "不能发布续租测试修订。");
                Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, next, host.Content!);
            }
            Advance(changed);
            Require(terms.Selected == 1 && !terms.IsItemDisabled(0) && terms.IsItemDisabled(1) && terms.IsItemDisabled(2) &&
                Find<Button>(panel, "ConfirmLease").Disabled, "应按核心期限阻塞禁用续租，容量阻塞不应禁用期限选择。");
            var text = Find<Label>(panel, "LeaseTermProblems").Text;
            Require(text.Contains("最长租期") && text.Contains("报价被拒绝") && !text.Contains("Request.") && !text.Contains("fixture:"),
                "不可选期限应说明中文原因。");
            Advance(faulted);
            Require(terms.Selected == 1 && terms.IsItemDisabled(1) && !terms.IsItemDisabled(2) &&
                Find<Label>(panel, "LeaseTermProblems").Text.Contains("核心模拟发生故障"), "报价修订后应恢复可用选项并显示故障原因。");
            Require(setup.Session.SubmitCount == 0, "不可用续租不应提交命令。");
        });
        setup.Session.AssertComplete();
    }
}
