using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Warehouse_displays_lease_cargo_without_internal_names_and_allows_another_rental()
    {
        var quotes = new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>();
        foreach (var lease in new ulong[] { 1, 2 })
        foreach (var multiple in new ulong[] { 1, 2, 3 })
        {
            var request = new LeaseRequest.Renew(new(lease), EconomyFixture.LeaseDuration * multiple);
            quotes.Add(request, new CoreResult<LeaseQuote>.Success(new(request, new(1), new(419), 3, [])));
        }
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(leaseCount: 2, leaseQuotes: quotes), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            await Page(panel, 3);
            Require(Find<Label>(panel, "LeaseDetails").Text.Contains("5/20") &&
                Find<Label>(panel, "LeaseDetails").Text.Contains("第2天 00:00"), "已有仓库应直接显示用量、容量和到期时间。");
            var stock = Find<VBoxContainer>(panel, "WarehouseGoods");
            var names = stock.FindChildren("GoodName", "Label", true, false).Cast<Label>().Select(item => item.Text).ToArray();
            Require(names.Any(name => name.StartsWith("主食 ×")) && names.Any(name => name == "未知货物（内容缺失） × 1") &&
                names.All(name => !name.Contains("fixture:") && !name.Contains("Definition")), "仓库货物应显示名称和数量，缺内容时也不显示内部标识。");
            Require(stock.FindChildren("*", "", true, false).OfType<Control>().Where(control => control.MouseFilter != Control.MouseFilterEnum.Ignore)
                .All(control => !control.TooltipText.Contains("fixture:")),
                "仓库货物提示不应显示内部标识。");
            var choices = Find<OptionButton>(panel, "Lease");
            Require(choices.ItemCount == 3 && Enumerable.Range(1, 2).All(index => choices.GetItemText(index).Contains("用量") &&
                !choices.GetItemText(index).Contains("租约 ")), "多租约应按用量和到期区分。");
            await _input.Choose(choices, 2);
            Require(Find<Label>(panel, "LeaseDetails").Text.Contains("第3天 00:00"), "应能切换查看另一份仓库租约。");
            await _input.Choose(choices, 0);
            Require(Find<LineEdit>(panel, "LeaseCapacity").IsVisibleInTree() && !stock.IsVisibleInTree(), "已有租约时仍应能新租仓库。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Warehouse_quotes_rent_automatically_and_keeps_capacity_errors_at_the_form()
    {
        var request = new LeaseRequest.Rent(new(1), 3, EconomyFixture.LeaseDuration);
        var quotes = new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>
        {
            [request] = new CoreResult<LeaseQuote>.Success(new(request, new(1), new(827), 3, [])),
            [request with { Capacity = 4 }] = new CoreResult<LeaseQuote>.Success(new(request with { Capacity = 4 }, new(1), null, 3,
                [new("insufficient_capacity", "Request.Capacity", new WorldObjectId.Inventory(new(11)), [])]))
        };
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(leaseCount: 0, leaseQuotes: quotes), EconomyFixture.Content());
        await Run(setup, async (_, panel) =>
        {
            await Page(panel, 3);
            await _input.Enter(Find<LineEdit>(panel, "LeaseCapacity"), "3");
            Require(Find<Button>(panel, "ConfirmLease").Text.Contains("827") && !Find<Button>(panel, "ConfirmLease").Disabled,
                "有效容量输入后应自动显示核心金额并允许租用。");
            Require(!panel.FindChildren("QuoteLease", "Button", true, false).Any(), "仓库页不应要求手动获取报价。");
            Require(Find<Label>(panel, "LeaseQuote").Text.Contains("第2天 00:00"), "新租应显示预计到期时间。");
            await _input.Enter(Find<LineEdit>(panel, "LeaseCapacity"), "4");
            var text = Find<Label>(panel, "LeaseQuote").Text;
            Require(Find<Button>(panel, "ConfirmLease").Disabled && text.Contains("容量不足") && text.Contains("3") &&
                !text.Contains("库存 11") && !text.Contains("Request.Capacity"), "容量超限应就地说明当前可租容量且隐藏内部身份。");
            await _input.Enter(Find<LineEdit>(panel, "LeaseCapacity"), "0");
            Require(Find<Button>(panel, "ConfirmLease").Disabled && Find<Label>(panel, "LeaseQuote").Text.Contains("正整数"),
                "非法容量应在表单说明且不能提交。");
            Require(setup.Session.SubmitCount == 0, "修改表单不应提交核心命令。");
        });
        setup.Session.AssertComplete();
    }
}
