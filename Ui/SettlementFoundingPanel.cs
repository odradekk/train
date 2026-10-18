using System.Globalization;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class SettlementFoundingPanel : Control
{
    private HostCoordinator _host = null!;
    private Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit = null!;
    private PanelContainer _panel = null!;
    private Label _preview = null!;
    private OptionButton _trains = null!;
    private Button _confirm = null!;
    private Button _openSettlement = null!;
    private readonly List<TrainId> _trainIds = [];
    private PanelTarget? _target;
    private NodeId _node;
    private bool _open;
    private CoreRevision? _previewRevision;
    private CoreResult<SettlementFoundingPreview>? _previewResult;
    private TrainId? _selectedTrain;
    private bool _syncingTrains;

    internal event Action<SettlementId>? SettlementRequested;

    internal void Initialize(HostCoordinator host, Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit)
    { _host = host; _submit = submit; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _panel = UiStyle.Panel(this, "Founding", "建立聚居地", new(760, 70), 520, Close, out var body);
        _panel.Hide();
        _preview = UiStyle.Label("");
        _preview.Name = "FoundingPreview";
        _preview.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_preview);
        body.AddChild(UiStyle.Label("材料从所选列车的库存一次扣除。关闭面板不提交。"));
        _trains = new OptionButton { Name = "MaterialSource" };
        _trains.ItemSelected += OnTrainSelected;
        body.AddChild(_trains);
        _confirm = UiStyle.Button("ConfirmFounding", "确认建立", Confirm, true);
        body.AddChild(_confirm);
        _openSettlement = UiStyle.Button("OpenFoundedSettlement", "库存、市场与服务…", OpenFoundedSettlement);
        _openSettlement.Hide();
        body.AddChild(_openSettlement);
    }

    internal void Open(PanelTarget source, NodeId node)
    {
        _target = source;
        _node = node;
        _open = true;
        _previewRevision = null;
        _previewResult = null;
        _selectedTrain = null;
        _trainIds.Clear();
        if (_trains is not null) _trains.Clear();
        _panel.Show();
        _panel.MoveToFront();
        if (_host.GetStatus() is { } status && _host.Content is { } content)
            Refresh(status.Binding, status.View, content);
    }

    internal void Close()
    {
        _open = false;
        _target = null;
        if (_panel is not null) _panel.Hide();
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (!_open || _preview is null) return;
        if (_target is not { } target || !target.IsAvailable(binding, view,
            _host.PanelGenerationCurrent(target.Binding, target.Generation)))
        {
            _preview.Text = "已不存在：节点或所属对局已失效。";
            _confirm.Disabled = true;
            _trains.Disabled = true;
            _openSettlement.Disabled = true;
            return;
        }
        _target = new(binding, content.Generation, target.Object);
        var map = view.World.Nodes.FirstOrDefault(item => item.Id == _node);
        if (map?.Occupant is NodeOccupant.Settlement settlement) { ShowFounded(settlement.Id); return; }
        if (map?.FoundingJob is { } job) { ShowProgress(view, content, job); return; }
        ShowPreview(view, content);
    }

    private void ShowFounded(SettlementId id)
    {
        _preview.Text = $"聚居地 {id.Value} 已建立。\n成立后库存为空，需另行运入生活物资。";
        _trains.Hide();
        _confirm.Hide();
        _openSettlement.Show();
        _openSettlement.Disabled = _host.IsBusy;
    }

    private void ShowProgress(ICoreReadView view, IContentView content, JobId job)
    {
        _trains.Hide();
        _confirm.Hide();
        _openSettlement.Hide();
        if (view.FindJob(job) is Lookup<JobView>.Found { Value: JobView.Founding founding })
        {
            var value = founding.Value;
            var invested = value.InvestedGoods.IsDefaultOrEmpty ? "无"
                : string.Join("，", value.InvestedGoods.Select(item => Quantity(item, content)));
            _preview.Text = $"建城进度\n已用 {value.ElapsedTicks} / {value.FrozenDurationTicks} 步\n预留聚居地 {value.ReservedSettlementId.Value}\n已投入：{invested}";
        }
        else _preview.Text = "建城进度\n任务已不在当前查询中。";
    }

    private void ShowPreview(ICoreReadView view, IContentView content)
    {
        _trains.Show();
        _confirm.Show();
        _openSettlement.Hide();
        if (_previewRevision != view.Revision || _previewResult is null)
        {
            _previewRevision = view.Revision;
            _previewResult = view.PreviewSettlement(_node);
        }
        SyncTrains(view);
        _preview.Text = Describe(_previewResult, content);
        var blocked = _previewResult is CoreResult<SettlementFoundingPreview>.Success { Value.Blockers: { IsDefaultOrEmpty: false } };
        _confirm.Disabled = _host.IsBusy || _selectedTrain is null || blocked;
        _trains.Disabled = _host.IsBusy;
    }

    private static string Describe(CoreResult<SettlementFoundingPreview> result, IContentView content) => result switch
    {
        CoreResult<SettlementFoundingPreview>.Failure failure =>
            "建城条件暂不可用。\n" + string.Join("\n", failure.Problems.Select(CommandDiagnostics.Describe)) +
            "\n选择停在该节点的列车后仍可确认。",
        CoreResult<SettlementFoundingPreview>.Success success => Describe(success.Value, content),
        _ => "建城条件暂不可用。"
    };

    private static string Describe(SettlementFoundingPreview preview, IContentView content)
    {
        var consumption = preview.ExpectedConsumption.IsDefaultOrEmpty ? "无"
            : string.Join("\n", preview.ExpectedConsumption.Select(rate =>
                $"{WorldPanels.GoodName(rate.Good, content)} {rate.UnitsPerSecond.ToString("0.###", CultureInfo.InvariantCulture)}/秒"));
        var materials = preview.RequiredMaterials.IsDefaultOrEmpty ? "无"
            : string.Join("\n", preview.RequiredMaterials.Select(item => Quantity(item, content)));
        var blockers = preview.Blockers.IsDefaultOrEmpty ? ""
            : "\n当前不能确认：\n" + string.Join("\n", preview.Blockers.Select(CommandDiagnostics.Describe));
        return $"初始人口：{preview.InitialResidents}\n预计生活物资\n{consumption}\n所需材料\n{materials}\n固定工期 {preview.DurationTicks} 步\n成立后库存为空，需另行运入生活物资。{blockers}";
    }

    private static string Quantity(GoodsQuantity item, IContentView content)
        => $"{WorldPanels.GoodName(item.Good, content)} × {item.Quantity}";

    private void SyncTrains(ICoreReadView view)
    {
        var ids = new List<TrainId>();
        foreach (var item in view.World.Trains)
        {
            if (view.FindTrain(item.Id) is not Lookup<TrainView>.Found train) continue;
            if (train.Value.Position is not TrainPosition.Node position || position.Id != _node) continue;
            ids.Add(train.Value.Id);
        }
        if (ids.Count == 0)
        {
            if (_trainIds.Count == 0 && _trains.ItemCount == 1 && _trains.GetItemText(0) == "该节点没有停靠的列车") return;
            _syncingTrains = true;
            _trains.Clear();
            _trainIds.Clear();
            _selectedTrain = null;
            _trains.AddItem("该节点没有停靠的列车");
            _trains.Select(0);
            _syncingTrains = false;
            return;
        }
        if (_trainIds.SequenceEqual(ids)) return;
        var selected = _selectedTrain;
        _syncingTrains = true;
        _trains.Clear();
        _trainIds.Clear();
        _trains.AddItem("选择列车");
        foreach (var id in ids)
        {
            var train = ((Lookup<TrainView>.Found)view.FindTrain(id)).Value;
            _trainIds.Add(id);
            _trains.AddItem($"列车 {id.Value} · {train.SpeedMetersPerSecond.ToString("0.##", CultureInfo.InvariantCulture)} m/s");
        }
        var index = 0;
        if (selected is { } kept)
        {
            var found = _trainIds.FindIndex(item => item == kept);
            if (found >= 0) index = found + 1;
        }
        _selectedTrain = index == 0 ? null : _trainIds[index - 1];
        _trains.Select(index);
        _syncingTrains = false;
    }

    private void OnTrainSelected(long index)
    {
        if (_syncingTrains) return;
        _selectedTrain = index > 0 && index <= _trainIds.Count ? _trainIds[(int)index - 1] : null;
        if (_host.GetStatus() is { } status && _host.Content is { } content)
            Refresh(status.Binding, status.View, content);
    }

    private void Confirm()
    {
        if (_confirm.Disabled || _target is null || _selectedTrain is not { } train) return;
        _submit(_target, new CoreCommand.FoundSettlement(_node, train));
    }

    private void OpenFoundedSettlement()
    {
        if (_host.GetStatus()?.View.World.Nodes.FirstOrDefault(item => item.Id == _node)?.Occupant is NodeOccupant.Settlement settlement)
            SettlementRequested?.Invoke(settlement.Id);
    }
}
