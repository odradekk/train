using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Ui.EconomyPanelControls;

namespace Train.Ui;

/// <summary>
/// 按列车查看运行结果，并从报告进入对应的运行计划。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal sealed class EconomyReportsWorkspace(Control parent, HostCoordinator host)
{
    private const float SummaryColumnWidth = 240;
    internal event Action<Control>? Opened;
    internal event Action<PanelTarget, TrainView, ICoreReadView, IContentView>? PlanRequested;
    private PanelContainer? _panel;
    private HostBinding _binding;
    private ContentGeneration _generation;
    private CoreRevision? _revision;
    private VBoxContainer _body = null!, _trains = null!;
    private TabContainer _pages = null!;
    private Label _state = null!, _wallet = null!, _empty = null!;
    private TrainId? _expanded;
    private readonly Dictionary<TrainId, Row> _rows = [];
    private sealed record Row(VBoxContainer Root, Button Expand, Label Summary, VBoxContainer Details,
        Label Reports, Button Edit, Label Reason);

    internal void Open(PanelTarget? source = null)
    {
        if (host.GetStatus() is not { } status || host.Content is not { } content) return;
        if (source is not null && !source.IsAvailable(status.Binding, status.View,
            host.PanelGenerationCurrent(source.Binding, source.Generation))) return;
        if (_panel is null || !Current(host, status.Binding, _binding, _generation))
        {
            Close();
            _binding = status.Binding; _generation = content.Generation;
            Build();
        }
        if (source?.Object is WorldObjectId.Train train)
        { _expanded = train.Id; _pages.CurrentTab = 0; }
        _panel!.Show();
        Refresh(status.Binding, status.View, content);
        ShowExpanded();
        Opened?.Invoke(_panel);
    }

    private void Build()
    {
        _panel = UiStyle.Panel(parent, "EconomyReports", "经营报告", Vector2.Zero, 0,
            () => _panel!.Hide(), out _body, fill: true);
        _state = Text(_body, "State", "");
        var summary = new HFlowContainer { Name = "Summary" }; _body.AddChild(summary);
        _wallet = Text(summary, "Wallet", "");
        // 自动换行文字的最小宽度不足以排成统计栏，先保留可读宽度，再让整栏换行。
        _wallet.CustomMinimumSize = new(SummaryColumnWidth, 0);
        _wallet.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        var totals = new VBoxContainer { Name = "CashFlow", CustomMinimumSize = new(SummaryColumnWidth, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin }; summary.AddChild(totals);
        Text(totals, "NetCashFlow", "本局净现金流：—");
        Text(totals, "IncomeAndExpenses", "收入 — · 支出 —");
        Text(_body, "CashFlowMeaning", "净现金流是实际收入减实际支出，不扣除货物成本和折旧。");
        Text(_body, "DailyCashFlow", "最近 7 个游戏日净现金流：暂无统计。");
        _pages = new TabContainer { Name = "ReportPages", SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(0, 200) };
        _body.AddChild(_pages);
        _trains = Page(_pages, "列车");
        _empty = Text(_trains, "Empty", "没有列车或已结束的运行报告。");
        Text(Page(_pages, "聚居地"), "Empty", "暂无聚居地报告。");
        Text(Page(_pages, "收支明细"), "Empty", "暂无收支明细。");
    }

    internal void Close()
    {
        RemoveWindow(_panel); _panel = null; _revision = null; _expanded = null; _rows.Clear();
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (_panel?.Visible != true) return;
        if (!Current(host, binding, _binding, _generation)) { Invalidate(_body, _state); return; }
        if (_revision != view.Revision)
        {
            _revision = view.Revision;
            _wallet.Text = $"资金：{view.Economy.Wallet.Value}";
            var trains = view.World.Trains.Select(item => item.Id)
                .Concat(view.Economy.RecentRunReports.Select(item => item.Train)).Distinct().ToArray();
            foreach (var id in _rows.Keys.Except(trains).ToArray())
            { RemoveControl(_rows[id].Root); _rows.Remove(id); }
            _empty.Visible = trains.Length == 0;
            for (var index = 0; index < trains.Length; index++)
            {
                var id = trains[index];
                if (!_rows.TryGetValue(id, out var row)) { row = BuildRow(id); _rows.Add(id, row); }
                _trains.MoveChild(row.Root, index + 1);
                var train = view.World.Trains.Any(item => item.Id == id) && view.FindTrain(id) is Lookup<TrainView>.Found found
                    ? found.Value : null;
                var reports = view.Economy.RecentRunReports.Where(item => item.Train == id)
                    .OrderByDescending(item => item.EndedAt.CompletedSteps).ThenByDescending(item => item.EndedAt.Sequence).ToArray();
                row.Expand.Text = $"列车 {id.Value} · " +
                    (train is null ? "已不存在" : WorldPanels.Activity(train.Activity) + " · " +
                        WorldPanels.LocationText(train.Position is TrainPosition.Node node ? new WorldLocation.Node(node.Id) :
                            new WorldLocation.Edge(((TrainPosition.Edge)train.Position).Id, ((TrainPosition.Edge)train.Position).DistanceMeters), view, content));
                var latest = reports.FirstOrDefault();
                row.Summary.Text = latest is null ? "暂无已结束的运行报告。" : $"最近运行净现金流 {latest.Totals.NetCashFlow:+0;-0;0}";
                row.Summary.AddThemeColorOverride("font_color", latest is null || latest.Totals.NetCashFlow == 0 ? UiStyle.Foreground :
                    latest.Totals.NetCashFlow > 0 ? UiStyle.Success : UiStyle.Error);
                row.Reports.Text = (train?.CurrentRun is { } current ? "当前运行累计\n" + EconomyPanels.RunText(current, content) + "\n\n" : "") +
                    (reports.Length == 0 ? "暂无已结束的运行报告。" : string.Join("\n\n", reports.Select(report => EconomyPanels.ReportText(report, content))));
                row.Reason.Text = train is null ? "列车已不存在，无法编辑计划。" : "";
            }
            ShowExpanded();
        }
        foreach (var (id, row) in _rows)
            row.Edit.Disabled = host.IsBusy || !view.World.Trains.Any(item => item.Id == id);
    }

    private Row BuildRow(TrainId id)
    {
        var root = new VBoxContainer { Name = $"Train{id.Value}" }; _trains.AddChild(root);
        var expand = UiStyle.Button("Expand", "", () => { _expanded = _expanded == id ? null : id; ShowExpanded(); });
        expand.ToggleMode = true; expand.TooltipText = "展开或收起运行报告";
        expand.AutowrapMode = TextServer.AutowrapMode.WordSmart; root.AddChild(expand);
        var summary = Text(root, "RunSummary", "");
        var details = new VBoxContainer { Name = "RunDetails", Visible = false }; root.AddChild(details);
        var edit = UiStyle.Button("EditPlan", "编辑计划", () => EditPlan(id)); details.AddChild(edit);
        var reason = Text(details, "Reason", "");
        var reports = Text(details, "ReportsText", "");
        root.AddChild(new HSeparator());
        return new(root, expand, summary, details, reports, edit, reason);
    }

    private void ShowExpanded()
    {
        foreach (var (id, row) in _rows)
        { row.Details.Visible = _expanded == id; row.Expand.SetPressedNoSignal(_expanded == id); }
    }

    private void EditPlan(TrainId id)
    {
        if (host.IsBusy || host.GetStatus() is not { } status || host.Content is not { } content ||
            !Current(host, status.Binding, _binding, _generation) || status.View.FindTrain(id) is not Lookup<TrainView>.Found train) return;
        PlanRequested?.Invoke(new(_binding, _generation, new WorldObjectId.Train(id)), train.Value, status.View, content);
    }
}
