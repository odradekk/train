using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

/// <summary>非敌对聚居地的经营摘要。Decision note: 2026-10-31-object-centric-management-screens</summary>
internal sealed class SettlementOverviewPage(Action<string> openPage)
{
    private Label _trains = null!, _jobs = null!, _leases = null!, _market = null!;
    private VBoxContainer _body = null!;

    internal void Build(VBoxContainer body)
    {
        _body = body;
        _trains = Text(body, "PresentTrains", "");
        _jobs = Text(body, "Commissions", "");
        _leases = Text(body, "LeaseSummary", "");
        body.AddChild(UiStyle.Button("ManageLeases", "管理租用仓库", () => openPage("仓库租用")));
        _market = Text(body, "MarketTrends", "");
        body.AddChild(UiStyle.Button("OpenMarket", "查看市场", () => openPage("市场")));
        Text(body, "ServicesTitle", "可用服务");
        body.AddChild(UiStyle.Button("OpenVehicles", "查看现车", () => openPage("现车")));
        body.AddChild(UiStyle.Button("OpenServices", "委托制造、修理、改装与拆解", () => openPage("建造与工地")));
    }

    internal void Refresh(ICoreReadView view, IContentView content, SettlementView settlement,
        IReadOnlyList<(InventoryView Inventory, LeaseView? Lease)> locals)
    {
        var trains = view.World.Trains.Where(item => item.Position is TrainPosition.Node node && node.Id == settlement.Node);
        _trains.Text = "在站列车\n" + Join(trains.Select(train => $"列车 {train.Id.Value} · {WorldPanels.Activity(train.Activity)}"), "没有列车在站");
        _jobs.Text = "进行中的委托\n" + Join(settlement.CommissionJobs.Select(view.FindJob)
            .OfType<Lookup<JobView>.Found>().Select(item => item.Value).OfType<JobView.Construction>()
            .Select(job => DescribeJob(job.Value)), "没有进行中的委托");
        _leases.Text = "租用仓库\n" + Join(locals.Where(item => item.Lease is not null).Select(item =>
            $"用量 {item.Inventory.Used}/{item.Lease!.Capacity} · {UiInput.FormatTime(item.Lease.ExpiresAtStep)}到期" +
            (item.Lease.Expired ? " · 已到期，只能取出" : "")), "尚未租用仓库");
        _market.Text = "市场动向\n" + Join(settlement.Market.Goods.Where(item => item.SellableQuantity > 0 || item.ReceivableQuantity > 0)
            .Select(item => $"{GoodName(item.Good, content)} · 可买 {item.SellableQuantity} · 可卖 {item.ReceivableQuantity}"), "当前没有可交易的货物");
    }

    private static string DescribeJob(ConstructionJobView job)
    {
        var kind = job.Kind switch { ConstructionKind.Construction => "制造", ConstructionKind.Refit => "改装",
            ConstructionKind.Repair => "修理", _ => "拆解" };
        var remaining = job.ActualWorkPerSecond > 0
            ? $" · 预计还需 {Math.Ceiling(Math.Max(0, job.FrozenTotalWork - job.ProgressWork) / job.ActualWorkPerSecond):0} 秒"
            : " · 当前未施工";
        return $"{kind} · {job.Progress:P0}{remaining}";
    }

    private static string Join(IEnumerable<string> lines, string empty)
    {
        var items = lines.ToArray();
        return items.Length == 0 ? empty : string.Join("\n", items);
    }

    internal static string GoodName(GoodId good, IContentView content) =>
        content.FindGood(good) is ContentLookup<GoodPresentation>.Found found ? found.Value.Display.Text.Name : "未知货物（内容缺失）";
}
