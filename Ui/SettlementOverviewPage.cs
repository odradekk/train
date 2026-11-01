using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.App;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

/// <summary>
/// 非敌对聚居地的经营摘要。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal sealed class SettlementOverviewPage(Action<string> openPage, Func<bool> canAct,
    Func<CoreCommand, SubmissionResult> submit)
{
    private Label _trains = null!, _jobs = null!, _leases = null!, _market = null!;
    private Label _warReason = null!;
    private Button _declare = null!;
    private ConfirmationDialog _confirmation = null!;
    private ICoreReadView _view = null!;
    private SettlementView _settlement = null!;
    private string _warFailure = "", _warCondition = "";

    internal void Build(VBoxContainer body)
    {
        _trains = Text(body, "PresentTrains", "");
        _jobs = Text(body, "Commissions", "");
        _leases = Text(body, "LeaseSummary", "");
        body.AddChild(UiStyle.Button("ManageLeases", "管理租用仓库", () => openPage("仓库租用")));
        _market = Text(body, "MarketTrends", "");
        body.AddChild(UiStyle.Button("OpenMarket", "查看市场", () => openPage("市场")));
        Text(body, "ServicesTitle", "可用服务");
        body.AddChild(UiStyle.Button("OpenVehicles", "查看现车", () => openPage("现车")));
        body.AddChild(UiStyle.Button("OpenServices", "委托制造、修理与改装", () => openPage("建造与工地")));
        body.AddChild(new HSeparator());
        _warReason = Text(body, "WarReason", "");
        _declare = UiStyle.Button("DeclareWar", "宣战…", () =>
        {
            if (!canAct()) return;
            _warFailure = "";
            UpdateActions(); _confirmation.PopupCentered(new(480, 320));
        });
        body.AddChild(_declare);
        _confirmation = new ConfirmationDialog { Name = "DeclareWarConfirmation", Title = "确认宣战",
            OkButtonText = "宣战", CancelButtonText = "取消", Exclusive = true };
        body.AddChild(_confirmation);
        _confirmation.Confirmed += () =>
        {
            if (!canAct() || WarReason().Length > 0) return;
            var result = submit(new CoreCommand.DeclareWar(_settlement.Id));
            _warFailure = result switch
            {
                SubmissionResult.NotAccepted rejected => CommandDiagnostics.Describe(rejected.Reason with { Subject = null }),
                SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected failure } =>
                    string.Join("\n", failure.Problems.Select(problem => CommandDiagnostics.Describe(problem with { Subject = null }))),
                SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted fault } => CommandDiagnostics.Describe(fault.Fault),
                _ => ""
            };
            UpdateActions();
        };
    }

    internal void Refresh(ICoreReadView view, IContentView content, SettlementView settlement,
        IReadOnlyList<(InventoryView Inventory, LeaseView? Lease)> locals)
    {
        _view = view; _settlement = settlement;
        _confirmation.Title = content.FindSettlementLayout(settlement.Id) is ContentLookup<SettlementLayoutProjection>.Found layout
            ? $"向{layout.Value.Display.Name}宣战？" : "向这个聚居地宣战？";
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
        UpdateActions();
    }

    internal void UpdateActions()
    {
        if (_settlement is null) return;
        _declare.Disabled = !canAct();
        var reason = WarReason();
        if (reason != _warCondition) _warFailure = "";
        _warCondition = reason;
        _warReason.Text = reason.Length > 0 ? reason : _warFailure;
        _confirmation.GetOkButton().Disabled = !canAct() || reason.Length > 0;
        _confirmation.DialogText = "宣战后，这个聚居地将与你敌对，停止货物和现车交易，不再接受委托。\n" +
            "租用仓库将冻结，无法存取货物，也无法新租或续租。已有租约保留，租期继续计算。\n" +
            "列车进入这里时，可能遭到防御单位攻击。敌对后无法恢复为非敌对关系。" +
            (reason.Length == 0 ? "" : "\n\n当前不能宣战：" + reason);
    }

    private string WarReason()
    {
        if (_settlement.Relation != SettlementRelation.NonHostile) return "只有非敌对聚居地可以宣战。";
        if (!_settlement.CommissionJobs.IsEmpty) return "这里有尚未完成的委托，请等待委托完成。";
        if (_view.World.Trains.Any(train => train.Position is TrainPosition.Node node && node.Id == _settlement.Node) ||
            _settlement.ParkedVehicles.Any(id => _view.FindInstance(new InstanceId.Vehicle(id)) is
                Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned }))
            return "这里有玩家车辆，请先让列车离站并移走停放车辆。";
        return "";
    }

    private static string DescribeJob(ConstructionJobView job)
    {
        var remaining = job.ActualWorkPerSecond > 0
            ? $" · 预计还需 {Math.Ceiling(Math.Max(0, job.FrozenTotalWork - job.ProgressWork) / job.ActualWorkPerSecond):0} 秒"
            : " · 当前未施工";
        return $"{EconomyPanels.ServiceKind(job.Kind)} · {job.Progress:P0}{remaining}";
    }

    private static string Join(IEnumerable<string> lines, string empty)
    {
        var items = lines.ToArray();
        return items.Length == 0 ? empty : string.Join("\n", items);
    }

    internal static string GoodName(GoodId good, IContentView content) =>
        content.FindGood(good) is ContentLookup<GoodPresentation>.Found found ? found.Value.Display.Text.Name : "未知货物（内容缺失）";
}
