using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class TrainFormationPanel : Control
{
    internal event Action<TrainId>? TrainCreated;
    private HostCoordinator _host = null!;
    private Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit = null!;
    private Action<string> _reportError = null!;
    private PanelTarget? _target;
    private ItemList _available = null!;
    private ItemList _ordered = null!;
    private Button _form = null!;
    private Label _reason = null!;

    public override void _Ready() => SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    private readonly List<(VehicleId Id, string Text)> _candidates = [];
    private readonly List<(VehicleId Id, string Text)> _selection = [];

    internal void Initialize(HostCoordinator host,
        Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit, Action<string> reportError)
    {
        _host = host; _submit = submit; _reportError = reportError;
        Name = "TrainFormationPanel";
        MouseFilter = MouseFilterEnum.Ignore;
        UiStyle.Panel(this, "Formation", "编组列车", new(310, 90), 660, Hide, out var body);
        body.AddChild(UiStyle.Label("选择本站未编组的玩家车辆，再调整编组顺序。"));
        _reason = UiStyle.Label("");
        _reason.Name = "Reason";
        _reason.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_reason);
        var columns = new HBoxContainer();
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddChild(UiStyle.Label("可用车辆")); right.AddChild(UiStyle.Label("编组顺序"));
        _available = new ItemList { Name = "AvailableVehicles", SelectMode = ItemList.SelectModeEnum.Multi,
            CustomMinimumSize = new(290, 0), AutoHeight = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _ordered = new ItemList { Name = "OrderedVehicles", CustomMinimumSize = new(290, 0), AutoHeight = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddChild(_available); right.AddChild(_ordered);
        left.AddChild(UiStyle.Button("AddSelected", "添加所选车辆 →", AddSelected));
        var reorder = new HBoxContainer();
        reorder.AddChild(UiStyle.Button("MoveUp", "上移", () => Move(-1)));
        reorder.AddChild(UiStyle.Button("MoveDown", "下移", () => Move(1)));
        reorder.AddChild(UiStyle.Button("Remove", "移除", Remove));
        right.AddChild(reorder); columns.AddChild(left); columns.AddChild(right); body.AddChild(columns);
        var actions = new HBoxContainer();
        _form = UiStyle.Button("Form", "确认编组", Form, true);
        actions.AddChild(_form); actions.AddChild(UiStyle.Button("Cancel", "取消", Hide)); body.AddChild(actions);
        Hide();
    }

    internal void Open(PanelTarget target, NodeId node, ICoreReadView view, IContentView content)
    {
        _target = target; _candidates.Clear(); _selection.Clear(); _available.Clear(); _ordered.Clear();
        if (view.FindLocation(new WorldLocation.Node(node)) is Lookup<LocationView>.Found location)
        {
            foreach (var id in location.Value.Instances.OfType<InstanceId.Vehicle>())
            {
                if (view.FindInstance(id) is not Lookup<InstanceView>.Found found ||
                    found.Value.Ownership != SettlementRelation.PlayerOwned ||
                    found.Value.Details is not InstanceDetails.Vehicle { Train: null } vehicle) continue;
                var display = content.FindDesign(found.Value.Design) is ContentLookup<DesignEvaluation>.Found
                    { Value: DesignEvaluation.Complete complete } ? complete.Definition.Display.Name : "";
                var text = $"车辆 {id.Id.Value} · {vehicle.Size}" + (display.Length == 0 ? "" : $" · {display}");
                _candidates.Add((id.Id, text)); _available.AddItem(text);
            }
        }
        MoveToFront(); Show(); Refresh(target.Binding, view, content);
    }

    private void AddSelected()
    {
        foreach (var index in _available.GetSelectedItems())
        {
            var candidate = _candidates[index];
            if (_selection.Any(item => item.Id == candidate.Id)) continue;
            _selection.Add(candidate);
        }
        RenderOrder();
    }

    private void Move(int offset)
    {
        var selected = _ordered.GetSelectedItems(); if (selected.Length == 0) return;
        var index = selected[0]; var next = index + offset;
        if (next < 0 || next >= _selection.Count) return;
        var vehicle = _selection[index]; _selection.RemoveAt(index); _selection.Insert(next, vehicle);
        RenderOrder(); _ordered.Select(next);
    }

    private void Remove()
    {
        var selected = _ordered.GetSelectedItems(); if (selected.Length == 0) return;
        _selection.RemoveAt(selected[0]); RenderOrder();
    }

    private void RenderOrder()
    {
        _ordered.Clear();
        for (var i = 0; i < _selection.Count; i++) _ordered.AddItem($"{i + 1}. {_selection[i].Text}");
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        var unavailable = _target is null || !_target.IsAvailable(binding, content.Generation, view);
        _form.Disabled = _host.IsBusy || unavailable || _candidates.Count == 0;
        _reason.Text = unavailable && _target is not null ? "已不存在：编组草稿所属节点或对局已失效。"
            : _candidates.Count == 0 ? "本站没有未编组的玩家车辆可供编组。" : "";
    }

    private void Form()
    {
        if (_selection.Count == 0) { _reportError("请至少选择一辆车。"); return; }
        if (_submit(_target, new CoreCommand.FormTrain(_selection.Select(item => item.Id).ToImmutableArray())) is { } accepted)
        {
            Hide();
            foreach (var train in accepted.Created.OfType<WorldObjectId.Train>()) TrainCreated?.Invoke(train.Id);
        }
    }
}
