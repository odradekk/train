using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Transfer_actions_only_appear_with_a_lease_and_multiple_leases_require_a_choice()
    {
        var none = new HostSimulationTests.HostSetup(EconomyFixture.Create(leaseCount: 0), EconomyFixture.Content());
        await Run(none, async (_, panel) =>
        {
            await Market(panel);
            Require(Find<OptionButton>(panel, "Action").ItemCount == 2, "无租约时只能买入或卖出。");
            Require(!Find<OptionButton>(panel, "TradeLease").IsVisibleInTree(), "无租约时不应显示仓库选择。");
        });
        none.Session.AssertComplete();
        var view = EconomyFixture.Create(leaseCount: 2);
        var multiple = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
        multiple.Session.QueueSubmit(new CoreCommand.LoadGoods(new(7), new(1), new(14), EconomyFixture.Food, 1),
            CommandSource.UserInterface, Completed(1, view, view, new CommandDecision.Accepted([], [])), view);
        await Run(multiple, async (_, panel) =>
        {
            await Market(panel);
            await _input.Choose(Find<OptionButton>(panel, "Action"), 2);
            var lease = Find<OptionButton>(panel, "TradeLease");
            Require(lease.IsVisibleInTree() && Find<Button>(panel, "ConfirmTrade").Disabled,
                "多租约时应选择仓库，选择前不能装卸。");
            await _input.Choose(lease, 2);
            await _input.Activate(Find<Button>(panel, "ConfirmTrade"));
            Require(lease.Selected == 2, "装卸后应保留所选仓库。");
        });
        multiple.Session.AssertComplete();
    }
}
