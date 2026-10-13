using Godot;
using Train.App.Recording;
using Train.App.Saves;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class IssueReportPanel : Window
{
    private RecordingService? _service;
    private readonly Label _status = new();
    private readonly Label _range = new();
    private readonly Label _gaps = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _evidence = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _message = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Button _exportButton = new() { Text = "导出复现 ZIP" };
    private readonly Button _openEvidence = new() { Text = "打开证据目录" };
    private readonly FileDialog _export = new()
    {
        Title = "导出问题复现包",
        Access = FileDialog.AccessEnum.Filesystem,
        FileMode = FileDialog.FileModeEnum.SaveFile,
        UseNativeDialog = true,
        Filters = ["*.zip ; ZIP 复现包"]
    };
    private readonly ConfirmationDialog _overwrite = new()
    {
        Title = "覆盖文件",
        DialogText = "目标文件已存在。确认覆盖？"
    };
    private string? _exportPath;

    public override void _Ready()
    {
        Title = "报告问题";
        Size = new Vector2I(680, 460);
        Transient = true;
        Exclusive = true;
        Visible = false;
        CloseRequested += Hide;
        var margin = new MarginContainer();
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "top", "right", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 16);
        AddChild(margin);
        var column = new VBoxContainer();
        margin.AddChild(column);
        column.AddChild(_status);
        column.AddChild(_range);
        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(scroll);
        _gaps.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_gaps);
        column.AddChild(_evidence);
        column.AddChild(_message);
        var buttons = new HBoxContainer();
        column.AddChild(buttons);
        buttons.AddChild(_exportButton);
        buttons.AddChild(_openEvidence);
        _exportButton.Pressed += () =>
        {
            _export.CurrentFile = "问题复现.zip";
            _export.PopupCentered(new Vector2I(800, 560));
        };
        _openEvidence.Pressed += OpenEvidence;
        foreach (var dialog in new Window[] { _export, _overwrite })
        {
            AddChild(dialog);
            dialog.Transient = true;
            dialog.Exclusive = true;
        }
        _export.FileSelected += path => Export(path);
        _overwrite.Confirmed += () =>
        {
            if (_exportPath is { } path) Export(path, overwriteConfirmed: true);
            _exportPath = null;
        };
        _overwrite.Canceled += () => _exportPath = null;
        Refresh();
    }

    internal void Configure(RecordingService service)
    {
        if (_service is not null) _service.Changed -= Refresh;
        _service = service;
        service.Changed += Refresh;
        Refresh();
    }

    internal void Open()
    {
        PopupCentered();
        Refresh();
    }

    public override void _Process(double delta)
    {
        if (Visible) RefreshStatus();
    }

    public override void _ExitTree()
    {
        if (_service is not null) _service.Changed -= Refresh;
    }

    private void Refresh()
    {
        if (!Visible) return;
        RefreshStatus();
        _message.Text = _service?.LastMessage ?? "";
    }

    private void RefreshStatus()
    {
        var status = _service?.Status;
        _status.Text = status is null ? "当前没有活动会话" :
            status.Frozen ? "录制已停止在故障边界（核心已冻结，仍可导出）" :
            status.Incomplete ? "录制不完整" :
            !status.HasUsableStart ? "尚无可用起点" : "正在录制";
        if (status is { Frozen: true, Incomplete: true }) _status.Text += " · 录制不完整";
        _range.Text = status is null ? "" : $"录制范围：{FormatPosition(status.From)} → {FormatPosition(status.Until)}";
        _gaps.Text = status is null || status.Gaps.IsEmpty ? "没有已记录的缺口" :
            "录制缺口：\n" + string.Join("\n", status.Gaps.Select(gap =>
                $"{FormatPosition(gap.From)} → {(gap.Until is { } until ? FormatPosition(until) : "未结束")} · {gap.Code}"));
        _evidence.Text = _service is null ? "" :
            $"证据目录（普通快照与独立故障包）：\n{_service.EvidenceDirectory}";
        _exportButton.Disabled = status is null || _service!.IsBusy;
        _openEvidence.Disabled = _service is null;
    }

    internal void Export(string path, bool overwriteConfirmed = false)
    {
        if (_service is null) return;
        var failure = _service.Export(path, overwriteConfirmed);
        if (failure?.Code == "overwrite-confirmation")
        {
            _exportPath = path;
            _overwrite.PopupCentered();
        }
        else ShowFailure(failure);
    }

    private void OpenEvidence()
    {
        if (_service is null) return;
        var error = OS.ShellOpen(_service.EvidenceDirectory);
        if (error != Error.Ok) _message.Text = $"无法打开证据目录：{error}";
    }

    private void ShowFailure(SaveFailure? failure)
    {
        if (failure is not null) _message.Text = failure.Message;
    }

    private static string FormatPosition(SimulationPosition position) => $"{position.CompletedSteps}:{position.Sequence}";
}
