using Godot;
using Train.App;
using Train.App.Development;
using Train.App.Saves;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

public partial class DeveloperInspector : Window
{
    private DeveloperTools? _tools;
    private WorldSceneController? _scenes;
    private readonly Label _status = new() { Name = "DeveloperStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _message = new() { Name = "DeveloperMessage", AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private readonly Label _performance = new() { Name = "DeveloperPerformance" };
    private readonly TextEdit _objectDetails = Details("ObjectDetails");
    private readonly TextEdit _commands = Details("CommandDetails");
    private readonly TextEdit _notes = Details("CheckpointDetails");
    private readonly OptionButton _objectType = Options("ObjectType", "节点", "线路", "列车", "车辆", "建筑", "聚居地", "库存", "任务", "战斗", "生物", "生物群", "巢穴");
    private readonly LineEdit _objectId = Input("ObjectId", "稳定对象编号");
    private readonly OptionButton _condition = Options("RunCondition", "到达步号", "命令被拒绝", "对象故障", "领域事件");
    private readonly LineEdit _conditionValue = Input("ConditionValue", "步号 / 命令 kind / 事件类型名");
    private readonly LineEdit _maximum = Input("MaximumSteps", "可选最大步数");
    private readonly LineEdit _kind = Input("CommandKind", "kind 筛选（空为全部）");
    private readonly OptionButton _result = Options("CommandResult", "全部", "accepted", "rejected", "faulted");
    private readonly CheckBox _subjectFilter = new() { Name = "FilterSubject", Text = "按检查对象筛选" };
    private readonly CheckBox _conditionSubject = new() { Name = "ConditionSubject", Text = "拒绝条件匹配已选对象（可选）" };
    private readonly OptionButton _related = Options("RelatedObjects");
    private readonly OptionButton _expected = Options("ExpectedCheckpoint");
    private readonly OptionButton _actual = Options("ActualCheckpoint");
    private readonly List<WorldObjectId> _relatedIds = [];
    private readonly Dictionary<string, Button> _buttons = [];
    private WorldObjectId? _selected;
    private string? _localMessage;
    private string? _exportPath;
    private double _refreshElapsed;
    private readonly FileDialog _openReplay = FilePicker("OpenReplay", "打开复现包", false, "*.zip ; ZIP 复现包");
    private readonly FileDialog _export = FilePicker("ExportBoundary", "导出重放边界为普通存档", true);
    private readonly ConfirmationDialog _overwrite = new() { Title = "覆盖文件", DialogText = "目标文件已存在。确认覆盖？" };

    internal void Configure(DeveloperTools tools, WorldSceneController? scenes)
    {
        _tools = tools;
        _scenes = scenes;
    }

    internal void Open() { PopupCentered(); Refresh(); }

    internal void Execute(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Count == 1 && args[0].Equals("open", StringComparison.OrdinalIgnoreCase)) Open();
        else if (args.Count == 1 && args[0].Equals("close", StringComparison.OrdinalIgnoreCase)) Hide();
        else _localMessage = "用法：dev [open|close]";
    }

    public override void _Ready()
    {
        Name = "DeveloperInspector";
        Title = "开发检查器";
        Size = new(900, 700);
        Transient = true;
        Exclusive = false;
        Visible = false;
        CloseRequested += Hide;
        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        root.AddChild(_status);
        root.AddChild(_message);
        var tabs = new TabContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        root.AddChild(tabs);
        BuildRun(Tab(tabs, "推进与重放"));
        BuildObjects(Tab(tabs, "对象"));
        BuildCommands(Tab(tabs, "命令"));
        BuildCheckpoints(Tab(tabs, "检查点与标记"));
        BuildScenes(Tab(tabs, "场景与性能"));
        foreach (var dialog in new Window[] { _openReplay, _export, _overwrite })
        {
            AddChild(dialog);
            dialog.Transient = true;
            dialog.Exclusive = true;
        }
        _openReplay.FileSelected += path => Run(() => _tools!.OpenReplay(path));
        _export.FileSelected += path => Export(path);
        _overwrite.Confirmed += () => { if (_exportPath is { } path) Export(path, true); _exportPath = null; };
        _overwrite.Canceled += () => _exportPath = null;
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _refreshElapsed += delta;
        if (_refreshElapsed < 0.25) return;
        _refreshElapsed = 0;
        Refresh();
    }

    private void BuildRun(VBoxContainer tab)
    {
        tab.AddChild(new Label { Text = "重放为只读：按录制推进，不提交新命令。导出边界后可作为普通存档读取。", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var steps = Input("StepCount", "推进步数");
        steps.Text = "1";
        tab.AddChild(steps);
        Button(tab, "暂停", "Pause", () => Run(() => _tools!.Pause()));
        Button(tab, "单步", "SingleStep", () => Run(() => _tools!.StartSteps(1)));
        Button(tab, "推进指定步数", "AdvanceSteps", () => { if (Number(steps, out var n)) Run(() => _tools!.StartSteps(n)); });
        tab.AddChild(_condition);
        tab.AddChild(_conditionValue);
        tab.AddChild(_conditionSubject);
        tab.AddChild(_maximum);
        Button(tab, "运行至条件", "RunUntil", StartCondition);
        Button(tab, "取消自动推进", "CancelRun", () => { _tools?.Cancel(); Refresh(); });
        var position = Input("ReplayPosition", "重放位置 step:sequence");
        tab.AddChild(position);
        Button(tab, "重放至位置", "AdvanceToPosition", () =>
        {
            var parts = position.Text.Split(':');
            if (parts.Length == 2 && ulong.TryParse(parts[0], out var step) && ulong.TryParse(parts[1], out var sequence))
                Run(() => _tools!.AdvanceTo(new(step, sequence)));
            else _localMessage = "位置格式应为 step:sequence。";
        });
        Button(tab, "打开复现包", "OpenReplayPackage", () => _openReplay.PopupCentered(new(800, 560)));
        Button(tab, "导出当前重放边界", "ExportReplayBoundary", () => _export.PopupCentered(new(800, 560)));
    }

    private void BuildObjects(VBoxContainer tab)
    {
        tab.AddChild(_objectType);
        tab.AddChild(_objectId);
        Button(tab, "查询对象", "InspectObject", () => { _selected = ReadObject(); RefreshObject(); });
        tab.AddChild(_objectDetails);
    }

    private void BuildCommands(VBoxContainer tab)
    {
        tab.AddChild(_kind); tab.AddChild(_result); tab.AddChild(_subjectFilter);
        tab.AddChild(_related);
        Button(tab, "检查关联对象", "InspectRelated", () =>
        {
            if (_related.Selected >= 0 && _related.Selected < _relatedIds.Count)
            {
                _selected = _relatedIds[_related.Selected];
                RefreshObject();
            }
        });
        tab.AddChild(_commands);
    }

    private void BuildCheckpoints(VBoxContainer tab)
    {
        var label = Input("CheckpointLabel", "检查点说明");
        tab.AddChild(label);
        Button(tab, "捕获内存检查点", "CaptureCheckpoint", () => CaptureCheckpoint(label.Text));
        tab.AddChild(_expected); tab.AddChild(_actual);
        Button(tab, "比较检查点", "CompareCheckpoints", () =>
        {
            if (_tools is not null && _expected.Selected >= 0 && _actual.Selected >= 0)
                _localMessage = _tools.Compare(_expected.Selected, _actual.Selected);
        });
        var mark = Input("MarkText", "说明标记");
        tab.AddChild(mark);
        Button(tab, "添加说明标记", "AddMark", () => Run(() => _tools!.AddMark(mark.Text)));
        tab.AddChild(_notes);
    }

    private void BuildScenes(VBoxContainer tab)
    {
        foreach (var layer in new[] { SceneDebugLayer.Chunks, SceneDebugLayer.RailwaySegments,
            SceneDebugLayer.Origin, SceneDebugLayer.PickRay, SceneDebugLayer.Centerline })
        {
            var box = new CheckBox { Name = "Layer" + layer, Text = layer.ToString(), ButtonPressed = _scenes is not null && (_scenes.DebugLayers & layer) != 0 };
            box.Toggled += enabled => { if (_scenes is not null) _scenes.DebugLayers = enabled ? _scenes.DebugLayers | layer : _scenes.DebugLayers & ~layer; };
            tab.AddChild(box);
        }
        Button(tab, "重新生成当前场景", "RegenerateScene", () => _localMessage = _scenes?.Regenerate()?.Detail ?? "场景重新生成已请求。");
        tab.AddChild(_performance);
    }

    private void StartCondition()
    {
        if (_tools is null) return;
        ulong? maximum = null;
        if (_maximum.Text.Length > 0) { if (!Number(_maximum, out var n)) return; maximum = n; }
        DeveloperCondition condition;
        switch (_condition.Selected)
        {
            case 0:
                if (!Number(_conditionValue, out var step)) return;
                condition = new DeveloperCondition.AtStep(step); break;
            case 1:
                if (_conditionSubject.ButtonPressed && _selected is null)
                { _localMessage = "请先选择对象，或关闭对象匹配。"; return; }
                condition = new DeveloperCondition.Rejected(_conditionValue.Text.Trim(), _conditionSubject.ButtonPressed ? _selected : null); break;
            case 2:
                if (_selected is null) { _localMessage = "先在对象页选择故障主体。"; return; }
                condition = new DeveloperCondition.ObjectFault(_selected); break;
            default: condition = new DeveloperCondition.Event(_conditionValue.Text.Trim()); break;
        }
        Run(() => _tools.StartCondition(condition, maximum));
    }

    private WorldObjectId? ReadObject()
    {
        if (!Number(_objectId, out var id) || id == 0) { _localMessage = "对象编号必须为非零整数。"; return null; }
        return _objectType.Selected switch
        {
            0 => new WorldObjectId.Node(new(id)), 1 => new WorldObjectId.Edge(new(id)),
            2 => new WorldObjectId.Train(new(id)), 3 => new WorldObjectId.Instance(new InstanceId.Vehicle(new(id))),
            4 => new WorldObjectId.Instance(new InstanceId.Building(new(id))), 5 => new WorldObjectId.Settlement(new(id)),
            6 => new WorldObjectId.Inventory(new(id)), 7 => new WorldObjectId.Job(new(id)),
            8 => new WorldObjectId.Combat(new(id)), 9 => new WorldObjectId.Creature(new(id)),
            10 => new WorldObjectId.CreatureGroup(new(id)), _ => new WorldObjectId.Lair(new(id))
        };
    }

    private void Refresh()
    {
        if (_tools is null) return;
        var status = _tools.Status;
        RefreshButtons(status);
        _status.Text = status is null ? "没有活动会话" :
            $"{(status.IsReplay ? "只读重放" : "活动会话")} · {FormatPosition(status.View.Position)} · 自动推进：{_tools.Running}\n" +
            $"暂停：玩家={status.PlayerPaused} 焦点={status.FocusPaused} 操作={status.OperationLocked} 冻结={status.IsFrozen}\n" +
            string.Join("；", _tools.StopReasons);
        _message.Text = _localMessage ?? _tools.LastMessage ?? "";
        RefreshObject(); RefreshCommands();
        SyncCheckpoints(_expected); SyncCheckpoints(_actual);
        SetDetails(_notes, string.Join("\n", _tools.Checkpoints.Select((x, i) =>
            $"[{i}] {x.Label} · {FormatPosition(x.Position)} · seed={x.WorldSeed} · {x.Runtime} · {x.InputIdentity} · {x.Checkpoint.Sha256}"))
            + "\n" + string.Join("\n", _tools.Marks.Select(x => $"{FormatPosition(x.Position)} · {x.Binding} · {x.Text}")));
        _performance.Text = $"进程帧耗时：{Performance.GetMonitor(Performance.Monitor.TimeProcess):F6}s\n" +
            $"静态内存：{Performance.GetMonitor(Performance.Monitor.MemoryStatic):F0} bytes\n" +
            $"对象数：{Performance.GetMonitor(Performance.Monitor.ObjectCount):F0}\n" +
            (status is null ? "模拟速度：未采集\n" : status.IsReplay ? "推进方式：按录制边界\n" : $"请求速度：{(int)status.Speed}× · 实际速度：{status.ActualSpeed:F3}× · " +
                (status.IsPaused ? "已暂停\n" : status.ActualSpeed >= (int)status.Speed ? "跟得上\n" : "未跟上\n")) +
            $"调试线数：{(_scenes?.Display?.DisplayedFrame is { } frame ? frame.DebugLines.Length.ToString() : "未采集")}\n模拟耗时与录制容量：未采集";
    }

    private void RefreshObject()
    {
        if (_selected is null) return;
        var data = _tools?.Inspect(_selected);
        SetDetails(_objectDetails, $"{_selected}\n{data?.Status.ToString() ?? "无活动视图"}\n{data?.Explanation}\n{data?.Json}");
    }

    private void RefreshCommands()
    {
        var window = _tools?.ReadCommands();
        if (window is null) { _commands.Text = "没有可用命令窗口。"; return; }
        var data = InspectorData.ReadCommands(window.Commands, _kind.Text.Length == 0 ? null : _kind.Text,
            _result.Selected <= 0 ? null : _result.GetItemText(_result.Selected), _subjectFilter.ButtonPressed ? _selected : null);
        SetDetails(_commands, $"{(window.Replay ? "重放" : "录制")}窗口 {FormatPosition(window.From)} → {FormatPosition(window.Until)}\n" +
            string.Join("\n", window.Gaps.Select(x => $"缺口 {FormatPosition(x.From)} → {(x.Until is { } until ? FormatPosition(until) : "未结束")} · {x.Code}")) + "\n" +
            string.Join("\n\n", data.Select(x => $"#{x.Record.Result.Number} {FormatPosition(x.Record.Result.Before)} → {FormatPosition(x.Record.Result.After)}\n{x.Kind} · {x.Result} · {x.Record.Source}\n关联：{string.Join(", ", x.Subjects.Select(FormatSubject))}\n{x.Record.CanonicalPayload}\n{x.Explanation}")));
        var related = data.SelectMany(x => x.Subjects).Distinct().ToArray();
        if (!_relatedIds.SequenceEqual(related))
        {
            _relatedIds.Clear(); _relatedIds.AddRange(related); _related.Clear();
            foreach (var id in related) _related.AddItem(FormatSubject(id));
        }
        for (var i = 0; i < related.Length; i++) _related.SetItemText(i, FormatSubject(related[i]));
    }

    private string FormatSubject(WorldObjectId id)
        => $"{id}（{(_tools?.Status is { } status && InspectorData.Exists(status.View, id) ? "存在" : "已失效")}）";

    private void SyncCheckpoints(OptionButton selector)
    {
        if (_tools is null || selector.ItemCount == _tools.Checkpoints.Count) return;
        var selected = selector.Selected;
        selector.Clear();
        foreach (var checkpoint in _tools.Checkpoints) selector.AddItem(checkpoint.Label + " · " + FormatPosition(checkpoint.Position));
        if (selector.ItemCount > 0) selector.Select(Math.Clamp(selected, 0, selector.ItemCount - 1));
    }

    private void Export(string path, bool confirmed = false)
    {
        if (_tools is null) return;
        var failure = _tools.ExportReplayBoundary(path, confirmed);
        if (failure?.Code == "overwrite-confirmation") { _exportPath = path; _overwrite.PopupCentered(); }
        else _localMessage = failure?.Message;
        Refresh();
    }

    private void CaptureCheckpoint(string label)
    {
        if (_tools is null) return;
        _localMessage = _tools.CaptureCheckpoint(label) switch
        {
            CoreResult<ICoreCapture>.Success => null,
            CoreResult<ICoreCapture>.Failure failure => "无法捕获检查点：" +
                string.Join("；", failure.Problems.Select(problem => $"{problem.Code}/{problem.Field}")),
            CoreResult<ICoreCapture>.Faulted fault =>
                $"无法捕获检查点：{fault.Fault.Code}/{fault.Fault.Operation}/{fault.Fault.Field}" +
                (fault.Diagnostic is { } diagnostic ? $" · {diagnostic.Message}" : ""),
            _ => throw new InvalidOperationException("未知检查点捕获结果。")
        };
        Refresh();
    }

    private void Run(Func<SaveFailure?> action)
    {
        if (_tools is null) return;
        _localMessage = action()?.Message;
        Refresh();
    }

    private bool Number(LineEdit input, out ulong value)
    {
        if (ulong.TryParse(input.Text, out value)) return true;
        _localMessage = "请输入非负整数。"; return false;
    }

    private static string FormatPosition(SimulationPosition position) => $"{position.CompletedSteps}:{position.Sequence}";
    private static void SetDetails(TextEdit control, string text)
    {
        if (control.Text != text) control.Text = text;
    }
    private static LineEdit Input(string name, string placeholder) => new() { Name = name, PlaceholderText = placeholder };
    private static TextEdit Details(string name) => new() { Name = name, Editable = false, CustomMinimumSize = new(0, 280), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
    private static OptionButton Options(string name, params string[] items)
    {
        var result = new OptionButton { Name = name };
        foreach (var item in items) result.AddItem(item);
        return result;
    }
    private static VBoxContainer Tab(TabContainer tabs, string name)
    {
        var scroll = new ScrollContainer { Name = name };
        tabs.AddChild(scroll);
        var column = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(column); return column;
    }
    private void RefreshButtons(HostStatus? status)
    {
        var busy = _tools!.Running || _tools.IsBusy || status?.OperationLocked == true;
        var canAdvance = status is { IsFrozen: false } && !busy;
        _buttons["Pause"].Disabled = status is null || _tools.IsBusy;
        foreach (var name in new[] { "SingleStep", "AdvanceSteps" }) _buttons[name].Disabled = !canAdvance;
        _buttons["RunUntil"].Disabled = !canAdvance || status!.IsReplay;
        _buttons["AdvanceToPosition"].Disabled = !canAdvance || !status!.IsReplay;
        _buttons["CancelRun"].Disabled = !busy;
        _buttons["ExportReplayBoundary"].Disabled = status is not { IsReplay: true } || busy;
        _buttons["OpenReplayPackage"].Disabled = busy;
        _buttons["CaptureCheckpoint"].Disabled = status is null || busy;
        _buttons["AddMark"].Disabled = status is null;
        _buttons["InspectObject"].Disabled = status is null;
        _buttons["InspectRelated"].Disabled = status is null || _relatedIds.Count == 0;
        _buttons["CompareCheckpoints"].Disabled = _tools.Checkpoints.Count == 0;
        _buttons["RegenerateScene"].Disabled = _scenes is null || status is null || busy;
    }

    private void Button(VBoxContainer parent, string text, string name, Action action)
    {
        var button = new Button { Text = text, Name = name }; button.Pressed += action; parent.AddChild(button);
        _buttons.Add(name, button);
    }
    private static FileDialog FilePicker(string name, string title, bool save, params string[] filters)
        => new() { Name = name, Title = title, Access = FileDialog.AccessEnum.Filesystem,
            FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile, Filters = filters };
}
