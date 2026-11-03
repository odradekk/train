using System.Globalization;
using System.Text;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

/// <summary>
/// 节点资源、建立聚居地和生物聚落的清剿说明。
/// Decision note: 2026-10-31-object-centric-management-screens
/// </summary>
internal sealed class NodeInspector
{
    private readonly HostCoordinator _host;
    private readonly Func<PanelTarget?, CoreCommand, SubmissionResult> _submit;
    private readonly Action _formation;
    private readonly Action<InstanceId> _refit, _service;
    private readonly NodeId _id;
    private readonly Label _title, _description, _preview, _conditions, _purchases, _reason, _result;
    private readonly VBoxContainer _sources, _secondary;
    private readonly OptionButton _trains;
    private readonly Button _found;
    private readonly ProgressBar _progress;
    private readonly ConfirmationDialog _confirmation;
    private readonly List<TrainId> _trainIds = [];
    private (InstanceId.Vehicle Id, string Name, bool Owned)[] _parked = [];
    private ICoreReadView _view = null!;
    private IContentView _content = null!;
    private NodeMapItem _node = null!;
    private CoreResult<SettlementFoundingPreview>? _previewResult;
    private TrainId? _selectedTrain, _confirmationTrain;
    private string _blocked = "";
    private bool _current;
    internal PanelTarget Target { get; }
    internal PanelContainer Panel { get; }

    internal NodeInspector(Control parent, HostCoordinator host, PanelTarget target,
        Func<PanelTarget?, CoreCommand, SubmissionResult> submit, Action formation,
        Action<InstanceId> refit, Action<InstanceId> service)
    {
        _host = host; Target = target; _id = ((WorldObjectId.Node)target.Object).Id;
        _submit = submit; _formation = formation; _refit = refit; _service = service;
        Panel = new PanelContainer { Name = "Details", MouseFilter = Control.MouseFilterEnum.Stop,
            MouseForcePassScrollEvents = false };
        var layout = new VBoxContainer(); Panel.AddChild(layout);
        var heading = new HBoxContainer(); layout.AddChild(heading);
        _title = Text(heading, "Title", "节点", true);
        heading.AddChild(UiStyle.Button("Close", "关闭", Panel.Hide));
        layout.AddChild(new HSeparator());
        var scroll = new ScrollContainer { Name = "Content", SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        layout.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10); scroll.AddChild(body);
        _description = Text(body, "Description", "");
        _preview = Text(body, "FoundingPreview", "");
        _progress = new ProgressBar { Name = "FoundingProgress", MaxValue = 100 }; body.AddChild(_progress);
        _sources = new VBoxContainer(); body.AddChild(_sources);
        Text(_sources, "MaterialSourceLabel", "材料来源列车");
        _trains = new OptionButton { Name = "MaterialSource", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            FitToLongestItem = false, TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis };
        _sources.AddChild(_trains);
        _trains.ItemSelected += index =>
        {
            _selectedTrain = index >= 0 && index < _trainIds.Count ? _trainIds[(int)index] : null;
            if (ReadCurrent()) { RefreshFounding(); UpdateActions(); }
        };
        _conditions = Text(body, "FoundingConditions", "");
        _purchases = Text(body, "FoundingPurchases", "");
        _secondary = new VBoxContainer(); body.AddChild(_secondary);
        layout.AddChild(new HSeparator());
        _result = Text(layout, "ActionResult", "");
        _reason = Text(layout, "FoundingReason", "");
        _found = UiStyle.Button("FoundSettlement", "建立聚居地", ConfirmFounding, true); layout.AddChild(_found);
        _confirmation = new ConfirmationDialog { Name = "FoundingConfirmation", Title = "建立聚居地？",
            OkButtonText = "确认建立", CancelButtonText = "取消", DialogAutowrap = true };
        _confirmation.Confirmed += SubmitFounding; Panel.AddChild(_confirmation);
        parent.AddChild(Panel);
    }

    private static Label Text(Node parent, string name, string text, bool title = false)
    {
        var label = UiStyle.Label(text, title); label.Name = name;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart; parent.AddChild(label); return label;
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content)
    {
        if (!Panel.IsVisibleInTree()) return;
        _current = Target.IsAvailable(binding, view, _host.PanelGenerationCurrent(Target.Binding, Target.Generation));
        if (!_current || view.FindNode(_id) is not Lookup<NodeView>.Found found)
        {
            _current = false; _title.Text = "节点已不存在";
            _description.Text = _blocked = "对象或所属对局已失效，请从当前列表重新打开。";
            _sources.Hide(); _preview.Hide(); _conditions.Hide(); _purchases.Hide(); _progress.Hide(); _secondary.Hide();
            UpdateActions(); return;
        }
        var changed = !ReferenceEquals(_view, view) || !ReferenceEquals(_content, content);
        _view = view; _content = content; _node = found.Value.Map;
        _title.Text = WorldPanels.NodeName(_id, view, content);
        if (changed) { RefreshNode(); RefreshSecondary(); }
        UpdateActions();
    }

    private void RefreshNode()
    {
        var terrain = _content.FindDefinitionDisplay(new CoreDefinitionReference.Terrain(_node.Terrain.TypeId))
            is ContentLookup<ContentDisplay>.Found display ? display.Value.Text.Name : "资料缺失";
        var occupant = _node.Occupant switch { NodeOccupant.Lair => "生物聚落", NodeOccupant.Settlement => "聚居地", _ => "空节点" };
        var text = new StringBuilder($"占据者：{occupant}\n地貌：{terrain}\n\n节点资源\n");
        if (_node.Resources.IsEmpty) text.AppendLine("没有已知节点资源。");
        foreach (var resource in _node.Resources)
            text.AppendLine($"{SettlementOverviewPage.GoodName(resource.Good, _content)}：{resource.Quantity}");
        var empty = _node.Occupant is NodeOccupant.Empty;
        var founding = _node.FoundingJob is not null;
        _sources.Visible = _conditions.Visible = _purchases.Visible = _found.Visible = empty && !founding;
        _preview.Visible = empty; _progress.Visible = empty && founding;
        if (_node.Occupant is NodeOccupant.Lair lair)
        {
            var type = _view.FindLair(lair.Id) is Lookup<LairView>.Found found &&
                _content.FindLair(found.Value.Type) is ContentLookup<LairAppearanceProjection>.Found appearance
                    ? appearance.Value.Display.Text.Name : "资料缺失";
            text.Insert(0, $"聚落类型：{type}\n\n节点上没有生物，又有玩家列车在场时，生物聚落被清剿，节点变为空节点。\n\n");
        }
        _description.Text = text.ToString().ReplaceLineEndings("\n");
        if (!empty) return;
        if (_node.FoundingJob is { } job) { ShowProgress(job); return; }
        SyncTrains();
        _previewResult = _view.PreviewSettlement(_id);
        RefreshFounding();
    }

    private void RefreshSecondary()
    {
        var parked = _view.FindLocation(new WorldLocation.Node(_id)) is Lookup<LocationView>.Found location
            ? location.Value.Instances.OfType<InstanceId.Vehicle>().Select((id, index) =>
                (Id: id, Name: EconomyPanels.TrainVehicleName(_view, id.Id) ?? $"第 {index + 1} 辆停放车辆",
                    Owned: _view.FindInstance(id) is Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned })).ToArray()
            : [];
        if (_secondary.GetChildCount() > 0 && parked.SequenceEqual(_parked)) return;
        _parked = parked;
        EconomyPanelControls.Clear(_secondary);
        _secondary.AddChild(UiStyle.Button("Form", "编组", () => { if (ReadCurrent()) _formation(); }));
        foreach (var vehicle in parked)
        {
            _secondary.AddChild(UiStyle.Button("RefitParkedVehicle", $"改装{vehicle.Name}…", () => { if (ReadCurrent()) _refit(vehicle.Id); }));
            if (vehicle.Owned)
                _secondary.AddChild(UiStyle.Button("ParkedVehicleServices", $"{vehicle.Name}服务…", () => { if (ReadCurrent()) _service(vehicle.Id); }));
        }
    }

    private bool ReadCurrent()
    {
        if (!Panel.IsVisibleInTree() || _host.GetStatus() is not { } status || _host.Content is not { } content) return false;
        Refresh(status.Binding, status.View, content);
        return _current && !_host.IsBusy;
    }

    private void UpdateActions()
    {
        _found.Disabled = !_current || _host.IsBusy || _blocked.Length > 0;
        _trains.Disabled = !_current || _host.IsBusy;
        if (_trains.ItemCount > 0) _trains.TooltipText = _trains.GetItemText(_trains.Selected);
        _reason.Text = _host.IsBusy ? "当前操作尚未结束。" : _blocked;
        _reason.Visible = _found.Visible && _reason.Text.Length > 0;
        _result.Visible = _result.Text.Length > 0;
        foreach (var button in _secondary.GetChildren().OfType<Button>()) button.Disabled = !_current || _host.IsBusy;
        if (_confirmation.Visible) UpdateConfirmation();
    }

    private void SyncTrains()
    {
        var ids = _view.World.Trains.Where(item => item.Position is TrainPosition.Node position && position.Id == _id)
            .Select(item => item.Id).ToArray();
        if (_trainIds.SequenceEqual(ids) && _trains.ItemCount > 0) return;
        _trainIds.Clear(); _trainIds.AddRange(ids); _trains.Clear();
        if (ids.Length == 0) _trains.AddItem("该节点没有停靠的列车");
        foreach (var id in ids) _trains.AddItem($"列车 {id.Value}");
        var index = _selectedTrain is { } selected ? Math.Max(0, Array.IndexOf(ids, selected)) : 0;
        _trains.Select(index); _selectedTrain = ids.Length > 0 ? ids[index] : null;
    }

    private void RefreshFounding()
    {
        if (_previewResult is not CoreResult<SettlementFoundingPreview>.Success success)
        {
            _blocked = "建立条件暂不可用。" + (_previewResult is CoreResult<SettlementFoundingPreview>.Failure failure
                ? "\n" + EconomyPanels.DescribeProblems(failure.Problems, _content) : "\n核心模拟发生故障。");
            _preview.Text = _blocked; _conditions.Text = _purchases.Text = ""; return;
        }
        var preview = success.Value;
        var consumption = preview.ExpectedConsumption.IsEmpty ? "无" : string.Join("\n", preview.ExpectedConsumption.Select(rate =>
            $"{SettlementOverviewPage.GoodName(rate.Good, _content)} {rate.UnitsPerSecond.ToString("0.###", CultureInfo.InvariantCulture)}/秒"));
        _preview.Text = $"建立聚居地\n初始人口：{preview.InitialResidents}\n预计生活物资\n{consumption}\n" +
            $"工期：{EconomyPanels.ServiceDuration(_view, preview.DurationTicks / (double)SimulationTime.StepsPerSecond)}\n" +
            "成立后库存为空，需另行运入生活物资。";
        var text = new StringBuilder();
        var reasons = new List<string>();
        if (!preview.Blockers.IsEmpty) reasons.Add(EconomyPanels.DescribeProblems(preview.Blockers, _content));
        var train = _selectedTrain is { } selected && _view.FindTrain(selected) is Lookup<TrainView>.Found found ? found.Value : null;
        Check(train is not null && train.Position is TrainPosition.Node position && position.Id == _id && train.SpeedMetersPerSecond == 0,
            train is not null ? $"列车 {train.Id.Value} 停在这里" : "需要一列玩家列车停在这里",
            _trainIds.Count == 0 ? "该节点没有停靠的列车。" : "材料列车必须停在本节点，速度为零。");
        InventoryView? inventory = null;
        if (train is not null)
        {
            var vehicles = train.Vehicles.Select(id => _view.FindInstance(new InstanceId.Vehicle(id))).ToArray();
            Check(vehicles.All(item => item is Lookup<InstanceView>.Found { Value.Ownership: SettlementRelation.PlayerOwned }),
                "列车全部车辆归玩家所有", "材料列车必须全部归玩家所有。");
            const InstanceLock construction = InstanceLock.UnderRefit | InstanceLock.UnderRepair | InstanceLock.UnderDeconstruction;
            Check(vehicles.All(item => item is Lookup<InstanceView>.Found vehicle && (vehicle.Value.Lock & construction) == 0),
                "列车没有施工任务", "材料列车正在改装、修理或拆解。");
            inventory = (_view.FindInventory(train.Inventory) as Lookup<InventoryView>.Found)?.Value;
            if (inventory is null) reasons.Add("无法读取材料列车的库存。");
        }
        var missing = new List<GoodId>();
        foreach (var material in preview.RequiredMaterials)
        {
            var have = inventory?.Stacks.FirstOrDefault(item => item.Good == material.Good)?.Quantity ?? 0;
            var name = SettlementOverviewPage.GoodName(material.Good, _content);
            var enough = have >= material.Quantity;
            text.AppendLine($"{(enough ? "✓" : "×")} {name}：{have} / {material.Quantity}" +
                (enough ? "" : $"，还差 {material.Quantity - have}"));
            if (enough) continue;
            reasons.Add($"{name}还差 {material.Quantity - have}。"); missing.Add(material.Good);
        }
        _conditions.Text = text.ToString().ReplaceLineEndings("\n");
        _blocked = string.Join("\n", reasons);
        var markets = new List<(NodeId Node, MarketView Market)>();
        if (missing.Count > 0)
            foreach (var node in _view.World.Nodes)
                if (node.Occupant is NodeOccupant.Settlement { Relation: SettlementRelation.NonHostile } occupant &&
                    _view.FindSettlement(occupant.Id) is Lookup<SettlementView>.Found { Value.Relation: SettlementRelation.NonHostile } settlement)
                    markets.Add((node.Id, settlement.Value.Market));
        _purchases.Text = string.Join("\n", missing.Select(good =>
            $"{SettlementOverviewPage.GoodName(good, _content)}：{PurchaseLocations(good, markets)}"));

        void Check(bool met, string label, string reason)
        {
            text.AppendLine($"{(met ? "✓" : "×")} {label}");
            if (!met) reasons.Add(reason);
        }
    }

    private string PurchaseLocations(GoodId good, List<(NodeId Node, MarketView Market)> markets)
    {
        var names = new List<string>();
        foreach (var market in markets)
            if (market.Market.Goods.Any(item => item.Good == good && item.SellableQuantity > 0 && item.Blockers.IsEmpty))
                names.Add(WorldPanels.NodeName(market.Node, _view, _content));
        var groups = names.GroupBy(name => name, StringComparer.Ordinal).ToArray();
        return names.Count == 0 ? "目前没有市场有货。" :
            string.Join("、", groups.Take(3).Select(group => group.Count() > 1 ? $"{group.Key} {group.Count()} 处" : group.Key)) +
            (groups.Length > 3 ? $"等 {names.Count} 处" : "") + "当前有货。";
    }

    private void ShowProgress(JobId job)
    {
        if (_view.FindJob(job) is not Lookup<JobView>.Found { Value: JobView.Founding founding })
        { _preview.Text = "建立进度暂不可用。"; _progress.Hide(); return; }
        var value = founding.Value;
        var seconds = Math.Max(0, value.FrozenDurationTicks - value.ElapsedTicks) / (double)SimulationTime.StepsPerSecond;
        _progress.Value = 100d * value.ElapsedTicks / value.FrozenDurationTicks;
        _preview.Text = "建立进度\n" + (seconds < 60 ? "预计还需 " : "预计完成于 ") + EconomyPanels.ServiceDuration(_view, seconds) +
            "\n已投入：" + EconomyPanels.DescribeGoods(value.InvestedGoods, _content) + "\n期间列车可以离开。";
    }

    private void ConfirmFounding()
    {
        if (!ReadCurrent() || _found.Disabled || !_found.Visible) return;
        _confirmationTrain = _selectedTrain;
        _confirmation.PopupCentered(new(480, 180)); UpdateConfirmation();
        _confirmation.GetCancelButton().GrabFocus();
    }

    private void UpdateConfirmation()
    {
        var blocked = _found.Disabled || !_found.Visible || _confirmationTrain != _selectedTrain;
        _confirmation.GetOkButton().Disabled = blocked;
        _confirmation.DialogText = blocked ? (_reason.Text.Length > 0 ? _reason.Text : "建立条件已变化，请关闭后重新核对。") :
            _previewResult is CoreResult<SettlementFoundingPreview>.Success success
                ? $"会从列车 {_confirmationTrain!.Value.Value} 一次扣除：{EconomyPanels.DescribeGoods(success.Value.RequiredMaterials, _content)}。\n" +
                    "建立后无法撤销，成立时库存为空，需要另行运入生活物资。\n期间列车可以离开。" : "建立条件暂不可用。";
    }

    private void SubmitFounding()
    {
        if (!ReadCurrent() || _found.Disabled || !_found.Visible || _confirmationTrain is not { } train || train != _selectedTrain) return;
        var result = _submit(Target, new CoreCommand.FoundSettlement(_id, train));
        _result.Text = result switch
        {
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted } => "已开始建立聚居地。",
            SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => EconomyPanels.DescribeProblems(rejected.Problems, _content),
            SubmissionResult.NotAccepted rejected => EconomyPanels.DescribeProblems([rejected.Reason], _content),
            _ => "核心模拟发生故障，未能建立聚居地。"
        };
        ReadCurrent();
    }
}
