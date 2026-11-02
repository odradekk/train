using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

/// <summary>
/// 列车的概况、运行计划、货物和车辆；主要操作固定在底部。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal sealed partial class TrainInspector
{
    private readonly HostCoordinator _host;
    private readonly Func<PanelTarget?, CoreCommand, SubmissionResult> _submit;
    private readonly Action<TrainView> _plan, _formation;
    private readonly Action _combat, _disband;
    private readonly Action<InstanceId> _refit, _service;
    private readonly Label _title, _description, _departureReason, _result, _unavailable;
    private readonly Button _edit, _start, _stop, _openCombat;
    private readonly TabContainer _pages;
    private readonly VBoxContainer _planPage, _cargoPage, _vehiclesPage;
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private TrainView _train = null!;
    private bool _current;
    internal PanelTarget Target { get; }
    internal PanelContainer Panel { get; }

    internal TrainInspector(Control parent, HostCoordinator host, PanelTarget target,
        Func<PanelTarget?, CoreCommand, SubmissionResult> submit,
        Action<TrainView> plan, Action<TrainView> formation, Action combat, Action disband,
        Action<InstanceId> refit, Action<InstanceId> service)
    {
        _host = host; Target = target; _submit = submit; _plan = plan; _formation = formation;
        _combat = combat; _disband = disband; _refit = refit; _service = service;
        Panel = new PanelContainer { Name = "Details", MouseFilter = Control.MouseFilterEnum.Stop,
            MouseForcePassScrollEvents = false };
        var layout = new VBoxContainer { Name = "Layout" };
        layout.AddThemeConstantOverride("separation", 10); Panel.AddChild(layout);
        var heading = new HBoxContainer { Name = "Heading" }; layout.AddChild(heading);
        _title = Text(heading, "Title", "列车", title: true);
        heading.AddChild(UiStyle.Button("Close", "关闭", Panel.Hide));
        layout.AddChild(new HSeparator());
        _unavailable = Text(layout, "Unavailable", "已不存在：对象或所属对局已失效。");
        _unavailable.Hide();
        _pages = new TabContainer { Name = "Pages", SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        layout.AddChild(_pages);
        var overview = Page(_pages, "概况");
        _openCombat = UiStyle.Button("OpenCombat", "查看战斗", OpenCombat);
        overview.AddChild(_openCombat);
        _description = Text(overview, "Description", "");
        _planPage = Page(_pages, "计划"); _cargoPage = Page(_pages, "货物"); _vehiclesPage = Page(_pages, "车辆");
        BuildReadOnlyPages();
        BuildVehicles();
        layout.AddChild(new HSeparator());
        _result = Text(layout, "ActionResult", "");
        _departureReason = Text(layout, "DepartureReason", "");
        var footer = new HFlowContainer { Name = "Actions" }; layout.AddChild(footer);
        _edit = UiStyle.Button("EditPlan", "编辑计划", () => { if (ReadCurrent()) _plan(_train); });
        _start = UiStyle.Button("Start", "发车", () => Submit(new CoreCommand.StartTrain(_train.Id)));
        _stop = UiStyle.Button("Stop", "停车", () => Submit(new CoreCommand.StopTrain(_train.Id)));
        footer.AddChild(_edit); footer.AddChild(_start); footer.AddChild(_stop);
        parent.AddChild(Panel);
        _pages.TabChanged += _ => ReadCurrent();
    }

    private static VBoxContainer Page(TabContainer tabs, string title)
    {
        var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10); scroll.AddChild(body); tabs.AddChild(scroll);
        return body;
    }

    private static Label Text(Node parent, string name, string value, bool title = false)
    {
        var label = UiStyle.Label(value, title); label.Name = name;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart; parent.AddChild(label); return label;
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (!Panel.IsVisibleInTree()) return;
        _current = Target.IsAvailable(binding, view, _host.PanelGenerationCurrent(Target.Binding, Target.Generation));
        if (!_current || view.FindTrain(((WorldObjectId.Train)Target.Object).Id) is not Lookup<TrainView>.Found found)
        {
            _current = false; _title.Text = "列车已不存在";
            _pages.Hide(); _unavailable.Show(); _departureReason.Hide();
            EconomyPanelControls.Clear(_vehicleList); _vehicles.Clear(); _repairQuote = null;
            _edit.Disabled = _start.Disabled = _stop.Disabled = true;
            _openCombat.Disabled = true;
            _result.Visible = _result.Text.Length > 0;
            return;
        }
        var changed = !_pages.Visible || !ReferenceEquals(_view, view) || !ReferenceEquals(_content, content);
        _pages.Show(); _unavailable.Hide();
        _view = view; _content = content; _train = found.Value;
        _title.Text = $"列车 {_train.Id.Value} · {WorldPanels.Activity(_train.Activity)}";
        if (changed) { RefreshOverview(); RefreshReadOnlyPages(); RefreshVehicles(); _repairQuote = null; }
        if (_pages.CurrentTab == 3 && _repairQuote is null && _train.Activity == TrainActivity.Stopped &&
            LocalSettlement() is { Relation: SettlementRelation.NonHostile } settlement)
            _repairQuote = VehicleRepairs.Quote(_train.Vehicles, settlement, _view, _content);
        UpdateVehicleActions();
        _edit.Disabled = _host.IsBusy;
        _edit.ThemeTypeVariation = _train.Plan is null ? "PrimaryButton" : "";
        _start.Visible = _train.Plan is null || _train.Activity == TrainActivity.Stopped;
        _stop.Visible = !_start.Visible;
        _start.Disabled = _host.IsBusy || _train.Plan is null;
        _stop.Disabled = _host.IsBusy;
        _openCombat.Disabled = _host.IsBusy;
        _start.ThemeTypeVariation = _train.Plan is not null ? "PrimaryButton" : "";
        _departureReason.Text = _host.IsBusy ? "当前操作尚未结束。" : _train.Plan is null ? "没有运行计划" : "";
        _departureReason.Visible = _departureReason.Text.Length > 0;
        _result.Visible = _result.Text.Length > 0;
    }

    private bool ReadCurrent()
    {
        if (!Panel.IsVisibleInTree() || _host.GetStatus() is not { } status || _host.Content is not { } content) return false;
        Refresh(status.Binding, status.View, content);
        return _current && !_host.IsBusy;
    }

    private void OpenCombat()
    {
        if (ReadCurrent() && _openCombat.Visible) _combat();
    }

    private void Submit(CoreCommand command)
    {
        if (!ReadCurrent()) return;
        var result = _submit(Target, command);
        _result.Text = FailureReason(result) ?? "操作已受理。";
        ReadCurrent();
    }

    private string? FailureReason(SubmissionResult result) => result switch
        {
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted } => null,
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } =>
                string.Join("\n", rejected.Problems.Select(ProblemText)),
            SubmissionResult.NotAccepted rejected => ProblemText(rejected.Reason),
            _ => "核心模拟发生故障。"
        };
}
