using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Warehouse_keeps_usable_days_out_of_nonadjacent_quote_failures()
    {
        var quotes = new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>();
        for (var multiple = 1; multiple <= 3; multiple++)
        {
            var request = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * (ulong)multiple);
            quotes.Add(request, multiple == 2 ? new CoreResult<LeaseQuote>.Success(new(request, new(1), new(419), 20, []))
                : new CoreResult<LeaseQuote>.Faulted(new("fixture:fault", new(0, 1), "QuoteLease", "lease", null), null));
        }
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(leaseQuotes: quotes), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            await Page(panel, 3);
            Require(Find<Label>(panel, "LeaseTermProblems").Text == "核心模拟发生故障，1 天、3 天不可选。", "不连续的失败期限不应合成包含可用天数的范围。");
            await _input.Choose(Find<OptionButton>(panel, "LeaseMultiple"), 1);
            Require(!Find<Button>(panel, "ConfirmLease").Disabled, "失败期限之间的可用天数仍应可以续租。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Warehouse_term_choices_show_rule_durations_in_days_and_requote_on_selection()
    {
        var first = new LeaseRequest.Rent(new(1), 3, EconomyFixture.LeaseDuration * 2);
        var second = first with { DurationSteps = EconomyFixture.LeaseDuration * 4 };
        var quotes = new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>
        {
            [first] = new CoreResult<LeaseQuote>.Success(new(first, new(1), new(731), 3, [])),
            [second] = new CoreResult<LeaseQuote>.Success(new(second, new(1), new(827), 3, []))
        };
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(leaseCount: 0, leaseQuotes: quotes),
            EconomyFixture.Content(maxLeaseMultiple: 4, leaseDurationTicks: (long)EconomyFixture.LeaseDuration * 2));
        await Run(setup, async (_, panel) =>
        {
            await Page(panel, 3);
            var terms = Find<OptionButton>(panel, "LeaseMultiple");
            Require(Enumerable.Range(0, terms.ItemCount).Select(terms.GetItemText).SequenceEqual(new[] { "2 天", "4 天", "6 天", "8 天" }),
                "期限应使用当前市场的固定租期与上限，并直接显示天数。");
            await _input.Enter(Find<LineEdit>(panel, "LeaseCapacity"), "3");
            Require(Find<Button>(panel, "ConfirmLease").Text.Contains("731") && Find<Label>(panel, "LeaseQuote").Text.Contains("第3天 00:00"),
                "初始期限应自动显示核心金额和预计到期时间。");
            await _input.Choose(terms, 1);
            Require(Find<Button>(panel, "ConfirmLease").Text.Contains("827") && Find<Label>(panel, "LeaseQuote").Text.Contains("第5天 00:00"),
                "选择期限后应自动更新金额和到期时间。");
            Require(terms.GetParent().FindChildren("*", "Label", true, false).Cast<Label>()
                .Where(label => label.IsVisibleInTree()).All(label => !label.Text.Contains('倍')), "仓库页应显示天数，不显示倍数。");
            Require(setup.Session.SubmitCount == 0, "选择期限不应提交租赁命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Warehouse_groups_unavailable_renewal_days_and_explains_the_rule_horizon()
    {
        (int Maximum, int LastAllowed, string Message)[] cases =
        [
            (7, 2, "续租后最长 7 天，3 到 7 天不可选。"),
            (12, 10, "续租后最长 12 天，11 到 12 天不可选。"),
            (3, 2, "续租后最长 3 天，3 天不可选。")
        ];
        foreach (var item in cases)
        {
            var quotes = new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>();
            for (var multiple = 1; multiple <= item.Maximum; multiple++)
            {
                var request = new LeaseRequest.Renew(new(1), EconomyFixture.LeaseDuration * (ulong)multiple);
                quotes.Add(request, new CoreResult<LeaseQuote>.Success(new(request, new(1), multiple <= item.LastAllowed ? new Money(337) : null, 20,
                    multiple <= item.LastAllowed ? [] : [new("invalid_state", "Request.DurationSteps", null, [])])));
            }
            var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(leaseQuotes: quotes,
                leaseExpiresAt: EconomyFixture.LeaseDuration * (ulong)(item.Maximum - item.LastAllowed)), EconomyFixture.Content(maxLeaseMultiple: item.Maximum));
            await Run(setup, async (_, panel) =>
            {
                await Page(panel, 3);
                var terms = Find<OptionButton>(panel, "LeaseMultiple");
                Require(terms.ItemCount == item.Maximum && !terms.IsItemDisabled(item.LastAllowed - 1) &&
                    terms.IsItemDisabled(item.LastAllowed) && terms.IsItemDisabled(item.Maximum - 1), "续租天数应按核心报价禁用超期选项。");
                Require(Find<Label>(panel, "LeaseTermProblems").Text == item.Message, "同一种期限阻塞应合成一行，说明最长天数和不可选天数。");
                Require(setup.Session.SubmitCount == 0, "不可选期限不应提交租赁命令。");
            });
            setup.Session.AssertComplete();
        }
    }
}
