using System.Text;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class CombatPanel : Control
{
    private readonly record struct Choice(string Name, string Text, Action Choose);

    private Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> _submit = null!;
    private Action<string> _report = null!;
    private HostBinding _binding;
    private ICoreReadView? _view;
    private IContentView? _content;
    private BattleReportLog? _reports;
    private bool _busy;
    private Label _state = null!;
    private Label _reportText = null!;
    private VBoxContainer _combats = null!;
    private VBoxContainer _participants = null!;
    private VBoxContainer _focus = null!;
    private VBoxContainer _trainRules = null!;
    private VBoxContainer _settlementRules = null!;
    private VBoxContainer _occupyTrains = null!;
    private OptionButton _trainPriority = null!;
    private OptionButton _trainScope = null!;
    private OptionButton _trainEncounter = null!;
    private SpinBox _trainRetreat = null!;
    private OptionButton _settlementPriority = null!;
    private OptionButton _settlementScope = null!;
    private Button _retreat = null!;
    private Button _focusFire = null!;
    private Button _applyTrain = null!;
    private Button _applySettlement = null!;
    private Button _declareWar = null!;
    private Button _occupy = null!;
    private TrainId? _rulesTrain;
    private SettlementId? _rulesSettlement;

    internal CombatId? SelectedCombat { get; private set; }
    internal CombatUnitId? SelectedParticipant { get; private set; }
    internal WorldObjectId? SelectedFocus { get; private set; }
    internal TrainId? ContextTrain { get; private set; }
    internal SettlementId? ContextSettlement { get; private set; }
    internal TrainId? OccupyTrain { get; private set; }

    internal void Initialize(Func<PanelTarget?, CoreCommand, CommandDecision.Accepted?> submit, Action<string> report)
    { _submit = submit; _report = report; }

    internal void Open()
    {
        Show();
        UiStyle.BringToFront(this);
        if (IsNodeReady() && _view is not null && _content is not null) Render();
    }

    internal void OpenTrain(TrainId train)
    {
        ContextTrain = train;
        _rulesTrain = null;
        if (_view is not null && CombatContaining(_view, train) is { } combat) SelectedCombat = combat;
        Show();
        UiStyle.BringToFront(this);
        if (IsNodeReady() && _view is not null && _content is not null) Render();
    }

    internal void Close()
    {
        SelectedCombat = null; SelectedParticipant = null; SelectedFocus = null;
        ContextTrain = null; ContextSettlement = null; OccupyTrain = null;
        _rulesTrain = null; _rulesSettlement = null;
        Hide();
    }

    internal void OpenSettlement(SettlementId settlement)
    {
        ContextSettlement = settlement;
        _rulesSettlement = null;
        Show();
        UiStyle.BringToFront(this);
        if (IsNodeReady() && _view is not null && _content is not null) Render();
    }

    internal void Refresh(HostBinding binding, ICoreReadView view, IContentView content, bool busy, BattleReportLog reports)
    {
        _binding = binding; _view = view; _content = content; _busy = busy; _reports = reports;
        if (IsNodeReady() && Visible) Render();
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        UiStyle.Panel(this, "Combat", "战斗", new(760, 68), 460, Hide, out var body);
        _state = UiStyle.Label("");
        _state.Name = "CombatState";
        _state.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_state);
        body.AddChild(UiStyle.Label("进行中的战斗", true));
        _combats = List(body);
        body.AddChild(UiStyle.Label("参战者", true));
        _participants = List(body);
        body.AddChild(UiStyle.Label("集中目标", true));
        _focus = List(body);
        _retreat = UiStyle.Button("Retreat", "撤退", SubmitRetreat);
        _focusFire = UiStyle.Button("FocusFire", "集中攻击", SubmitFocus, true);
        body.AddChild(_retreat);
        body.AddChild(_focusFire);
        _trainRules = new VBoxContainer { Name = "TrainRules" };
        _trainRules.AddChild(UiStyle.Label("列车交战规则", true));
        _trainPriority = Choices("TrainPriority", ["武装优先", "最低耐久", "最高耐久"]);
        _trainScope = Choices("TrainScope", ["仅武装", "全部"]);
        _trainEncounter = Choices("TrainEncounter", ["交战", "撤退"]);
        _trainRetreat = new SpinBox { Name = "TrainRetreatRatio", MinValue = 0, MaxValue = 1, Step = 0.05,
            CustomMinimumSize = new(280, 38) };
        AddRow(_trainRules, "目标优先级", _trainPriority);
        AddRow(_trainRules, "攻击范围", _trainScope);
        AddRow(_trainRules, "遇敌行为", _trainEncounter);
        AddRow(_trainRules, "撤退阈值（结构耐久比例）", _trainRetreat);
        _applyTrain = UiStyle.Button("ApplyTrainEngagement", "应用列车规则", SubmitTrainRules);
        _trainRules.AddChild(_applyTrain);
        body.AddChild(_trainRules);
        _settlementRules = new VBoxContainer { Name = "SettlementRules" };
        _settlementRules.AddChild(UiStyle.Label("聚居地交战规则", true));
        _settlementPriority = Choices("SettlementPriority", ["武装优先", "最低耐久", "最高耐久"]);
        _settlementScope = Choices("SettlementScope", ["仅武装", "全部"]);
        AddRow(_settlementRules, "目标优先级", _settlementPriority);
        AddRow(_settlementRules, "攻击范围", _settlementScope);
        _applySettlement = UiStyle.Button("ApplySettlementEngagement", "应用聚居地规则", SubmitSettlementRules);
        _declareWar = UiStyle.Button("DeclareWar", "宣战", SubmitDeclareWar);
        _occupy = UiStyle.Button("Occupy", "占领", SubmitOccupy, true);
        _settlementRules.AddChild(_applySettlement);
        _settlementRules.AddChild(_declareWar);
        _settlementRules.AddChild(UiStyle.Label("在场列车", true));
        _occupyTrains = new VBoxContainer();
        _settlementRules.AddChild(_occupyTrains);
        _settlementRules.AddChild(_occupy);
        body.AddChild(_settlementRules);
        body.AddChild(UiStyle.Label("战报", true));
        _reportText = UiStyle.Label("暂无战报。");
        _reportText.Name = "BattleReports";
        _reportText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_reportText);
        Hide();
        if (_view is not null && _content is not null) Render();
    }

    private void Render()
    {
        var view = _view!;
        var text = new StringBuilder();
        CombatView? combat = null;
        var combatListed = SelectedCombat is { } selected && view.World.Combats.Any(item => item.Id == selected);
        if (SelectedCombat is { } missingCombat && !combatListed)
            text.AppendLine($"战斗 {missingCombat.Value} 已不存在");
        else if (combatListed && view.FindCombat(SelectedCombat!.Value) is Lookup<CombatView>.Found found)
            combat = found.Value;
        else if (combatListed)
            text.AppendLine($"战斗 {SelectedCombat!.Value.Value} 已不存在");
        if (combat is { } current)
        {
            text.AppendLine($"战斗 {current.Id.Value}");
            text.AppendLine($"地点：{WorldPanels.LocationText(current.Location, view, _content!)}");
            text.AppendLine($"开始步：{current.StartedAtStep}");
        }
        var participantPresent = combat is not null && SelectedParticipant is { } participant &&
            combat.Participants.Any(item => item.Unit == participant);
        if (SelectedParticipant is not null && !participantPresent)
            text.AppendLine("参战者已不存在");
        var focusPresent = combat is not null && SelectedFocus is { } focus &&
            combat.Participants.Any(item => UnitObject(item.Unit) == focus && Focusable(view, item));
        if (SelectedFocus is not null && !focusPresent)
            text.AppendLine("集中目标已不存在");
        AppendContext(text, view);
        _state.Text = text.ToString().ReplaceLineEndings("\n");
        Sync(_combats, view.World.Combats.Select(item => new Choice($"Combat{item.Id.Value}",
            $"{BattleReportLog.Place(item.Location)}", () => SelectedCombat = item.Id)).ToArray());
        Sync(_participants, combat?.Participants.Select(item => new Choice(UnitName(item.Unit),
            UnitLabel(view, item), () => ChooseParticipant(item.Unit))).ToArray() ?? []);
        Sync(_focus, combat?.Participants.Where(item => Focusable(view, item))
            .Select(item => new Choice("Focus" + UnitName(item.Unit), UnitLabel(view, item), () => SelectedFocus = UnitObject(item.Unit)))
            .ToArray() ?? []);
        var settlement = Settlement(view);
        var trains = settlement is null ? [] : view.World.Trains.Where(item => item.Position is TrainPosition.Node node &&
            node.Id == settlement.Node).Select(item => new Choice($"OccupyTrain{item.Id.Value}", $"列车 {item.Id.Value}",
            () => OccupyTrain = item.Id)).ToArray();
        Sync(_occupyTrains, trains);
        if (OccupyTrain is { } occupy && !trains.Any(item => item.Name == $"OccupyTrain{occupy.Value}"))
            text.AppendLine("在场列车已不存在");
        _state.Text = text.ToString().ReplaceLineEndings("\n");
        var train = Train(view);
        if (train is not null && _rulesTrain != train.Id)
        {
            _rulesTrain = train.Id;
            _trainPriority.Select((int)train.Engagement.Priority);
            _trainScope.Select((int)train.Engagement.Scope);
            _trainEncounter.Select((int)train.Engagement.Encounter);
            _trainRetreat.SetValueNoSignal(train.Engagement.RetreatStructureRatio);
        }
        if (settlement is not null && _rulesSettlement != settlement.Id)
        {
            _rulesSettlement = settlement.Id;
            _settlementPriority.Select((int)settlement.Engagement.Priority);
            _settlementScope.Select((int)settlement.Engagement.Scope);
        }
        var controller = participantPresent ? Controller(view, SelectedParticipant!) : null;
        var ownSettlement = settlement?.Relation == SettlementRelation.PlayerOwned;
        _trainRules.Visible = ContextTrain is not null;
        _settlementRules.Visible = ContextSettlement is not null;
        _retreat.Disabled = _busy || !participantPresent || RetreatTrain(view) is null;
        _focusFire.Disabled = _busy || combat is null || !participantPresent || !focusPresent || controller is null;
        _applyTrain.Disabled = _busy || train is null || _trainPriority.Selected < 0;
        _settlementPriority.Disabled = _busy || !ownSettlement;
        _settlementScope.Disabled = _busy || !ownSettlement;
        _applySettlement.Disabled = _busy || !ownSettlement || _settlementPriority.Selected < 0;
        _declareWar.Disabled = _busy || settlement is not { Relation: SettlementRelation.NonHostile };
        _occupy.Disabled = _busy || settlement is not { Relation: SettlementRelation.Hostile } || OccupyTrain is not { } chosen ||
            !view.World.Trains.Any(item => item.Id == chosen && item.Position is TrainPosition.Node node && node.Id == settlement.Node);
        _reportText.Text = _reports is { Reports.Count: > 0 } log
            ? string.Join("\n", log.Reports.Select(BattleReportLog.Describe)) : "暂无战报。";
    }

    private void AppendContext(StringBuilder text, ICoreReadView view)
    {
        if (ContextTrain is { } trainId)
        {
            if (view.World.Trains.Any(item => item.Id == trainId) && view.FindTrain(trainId) is Lookup<TrainView>.Found train)
            {
                text.AppendLine($"列车 {trainId.Value}");
                foreach (var problem in train.Value.Blockers.Where(problem => problem.Code == "no_route"))
                    text.AppendLine(CommandDiagnostics.Describe(problem, _content));
            }
            else text.AppendLine($"列车 {trainId.Value} 已不存在");
        }
        if (ContextSettlement is { } settlementId)
        {
            if (Settlement(view) is { } settlement)
            {
                text.AppendLine($"聚居地 {settlementId.Value} · {WorldPanels.Relation(settlement.Relation)}");
                text.AppendLine($"士气：{settlement.MoraleRatio:0.00}");
                text.AppendLine(settlement.Surrendered ? "已投降" : "未投降");
            }
            else text.AppendLine($"聚居地 {settlementId.Value} 已不存在");
        }
    }

    private void ChooseParticipant(CombatUnitId unit)
    {
        SelectedParticipant = unit;
        if (_view is null) return;
        if (unit is CombatUnitId.Instance instance && _view.FindInstance(instance.Id) is Lookup<InstanceView>.Found found)
        {
            if (found.Value.Details is InstanceDetails.Vehicle { Train: { } train })
            { ContextTrain = train; _rulesTrain = null; }
            if (found.Value.Location is WorldLocation.Node node &&
                _view.World.Nodes.FirstOrDefault(item => item.Id == node.Id)?.Occupant is NodeOccupant.Settlement settlement)
            { ContextSettlement = settlement.Id; _rulesSettlement = null; }
        }
    }

    private void SubmitRetreat()
    {
        if (_view is null || RetreatTrain(_view) is not { } train) { _report("已不存在"); return; }
        _submit(Target(new WorldObjectId.Train(train)), new CoreCommand.Retreat(train));
    }

    private void SubmitFocus()
    {
        if (_view is null || SelectedCombat is not { } combat || SelectedParticipant is not { } participant ||
            SelectedFocus is not { } focus || Controller(_view, participant) is not { } controller)
        { _report("已不存在"); return; }
        WorldObjectId? target = controller switch
        {
            CombatController.Train train => new WorldObjectId.Train(train.Id),
            CombatController.Settlement settlement => new WorldObjectId.Settlement(settlement.Id),
            _ => null
        };
        _submit(target is null ? null : Target(target), new CoreCommand.FocusFire(combat, controller, focus));
    }

    private void SubmitTrainRules()
    {
        if (_view is null || Train(_view) is not { } train || _trainPriority.Selected < 0) { _report("已不存在"); return; }
        var rules = new TrainEngagementRules((TargetPriority)_trainPriority.Selected, (TargetScope)_trainScope.Selected,
            (EncounterAction)_trainEncounter.Selected, _trainRetreat.Value);
        _submit(Target(new WorldObjectId.Train(train.Id)), new CoreCommand.SetTrainEngagement(train.Id, rules));
    }

    private void SubmitSettlementRules()
    {
        if (Settlement(_view) is not { } settlement || _settlementPriority.Selected < 0) { _report("已不存在"); return; }
        var rules = new SettlementEngagementRules((TargetPriority)_settlementPriority.Selected, (TargetScope)_settlementScope.Selected);
        _submit(Target(new WorldObjectId.Settlement(settlement.Id)), new CoreCommand.SetSettlementEngagement(settlement.Id, rules));
    }

    private void SubmitDeclareWar()
    {
        if (Settlement(_view) is not { } settlement) { _report("已不存在"); return; }
        _submit(Target(new WorldObjectId.Settlement(settlement.Id)), new CoreCommand.DeclareWar(settlement.Id));
    }

    private void SubmitOccupy()
    {
        var settlement = Settlement(_view);
        if (settlement is null || OccupyTrain is not { } train || _view!.World.Trains.All(item => item.Id != train ||
            item.Position is not TrainPosition.Node node || node.Id != settlement.Node))
        { _report("已不存在"); return; }
        _submit(Target(new WorldObjectId.Train(train)), new CoreCommand.OccupySettlement(settlement.Id, train));
    }

    private PanelTarget? Target(WorldObjectId id) =>
        _content is null ? null : new(_binding, _content.Generation, id);

    private TrainView? Train(ICoreReadView? view) =>
        view is not null && ContextTrain is { } id && view.World.Trains.Any(item => item.Id == id) &&
        view.FindTrain(id) is Lookup<TrainView>.Found found ? found.Value : null;

    private SettlementView? Settlement(ICoreReadView? view)
    {
        if (view is null || ContextSettlement is not { } id) return null;
        var listed = view.World.Nodes.Any(item => item.Occupant is NodeOccupant.Settlement settlement && settlement.Id == id);
        return listed && view.FindSettlement(id) is Lookup<SettlementView>.Found found ? found.Value : null;
    }

    private TrainId? RetreatTrain(ICoreReadView view)
    {
        if (SelectedParticipant is not CombatUnitId.Instance { Id: InstanceId.Vehicle vehicle }) return null;
        if (view.FindInstance(vehicle) is not Lookup<InstanceView>.Found { Value.Details: InstanceDetails.Vehicle { Train: { } train } })
            return null;
        return view.World.Trains.Any(item => item.Id == train) ? train : null;
    }

    private CombatController? Controller(ICoreReadView view, CombatUnitId unit)
    {
        if (unit is not CombatUnitId.Instance instance ||
            view.FindInstance(instance.Id) is not Lookup<InstanceView>.Found found ||
            found.Value.Ownership != SettlementRelation.PlayerOwned)
            return null;
        if (RetreatTrain(view) is { } train && instance.Id is InstanceId.Vehicle)
            return new CombatController.Train(train);
        if (found.Value.Location is WorldLocation.Node node &&
            view.World.Nodes.FirstOrDefault(item => item.Id == node.Id)?.Occupant is NodeOccupant.Settlement occupied &&
            view.FindSettlement(occupied.Id) is Lookup<SettlementView>.Found settlement &&
            settlement.Value.Relation == SettlementRelation.PlayerOwned)
            return new CombatController.Settlement(occupied.Id);
        return null;
    }

    private CombatId? CombatContaining(ICoreReadView view, TrainId train)
    {
        if (view.FindTrain(train) is not Lookup<TrainView>.Found found) return null;
        foreach (var item in view.World.Combats)
        {
            if (view.FindCombat(item.Id) is not Lookup<CombatView>.Found combat) continue;
            if (combat.Value.Participants.Any(participant => participant.Unit is CombatUnitId.Instance
                { Id: InstanceId.Vehicle vehicle } && found.Value.Vehicles.Contains(vehicle.Id)))
                return item.Id;
        }
        return null;
    }

    private static void Sync(VBoxContainer box, IReadOnlyList<Choice> choices)
    {
        var existing = box.GetChildren().OfType<Button>().ToArray();
        if (existing.Length == choices.Count && existing.Zip(choices).All(pair => pair.First.Name == pair.Second.Name && pair.First.Text == pair.Second.Text))
            return;
        foreach (var child in existing)
        {
            box.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var choice in choices)
            box.AddChild(UiStyle.Button(choice.Name, choice.Text, choice.Choose));
    }

    private static VBoxContainer List(VBoxContainer body)
    {
        var list = new VBoxContainer();
        body.AddChild(list);
        return list;
    }

    private static void AddRow(VBoxContainer body, string title, Control input)
    {
        var row = new HBoxContainer();
        var label = UiStyle.Label(title);
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(label);
        row.AddChild(input);
        body.AddChild(row);
    }

    private static OptionButton Choices(string name, string[] values)
    {
        var options = new OptionButton { Name = name, CustomMinimumSize = new(280, 38) };
        foreach (var value in values) options.AddItem(value);
        return options;
    }

    private static string UnitName(CombatUnitId unit) => unit switch
    {
        CombatUnitId.Instance { Id: InstanceId.Vehicle vehicle } => $"Vehicle{vehicle.Id.Value}",
        CombatUnitId.Instance { Id: InstanceId.Building building } => $"Building{building.Id.Value}",
        CombatUnitId.CreatureGroup group => $"Group{group.Id.Value}",
        _ => "Unit"
    };

    private bool Focusable(ICoreReadView view, CombatParticipantView participant)
    {
        if (participant.Unit == SelectedParticipant) return false;
        return participant.Unit switch
        {
            CombatUnitId.CreatureGroup => true,
            CombatUnitId.Instance instance when view.FindInstance(instance.Id) is Lookup<InstanceView>.Found found =>
                found.Value.Ownership != SettlementRelation.PlayerOwned,
            _ => false
        };
    }

    private static string UnitLabel(ICoreReadView view, CombatParticipantView participant)
    {
        var name = participant.Unit switch
        {
            CombatUnitId.Instance { Id: InstanceId.Vehicle vehicle } => $"车辆 {vehicle.Id.Value}",
            CombatUnitId.Instance { Id: InstanceId.Building building } => $"建筑 {building.Id.Value}",
            CombatUnitId.CreatureGroup group => $"生物群 {group.Id.Value}",
            _ => "单位"
        };
        var side = participant.Unit switch
        {
            CombatUnitId.CreatureGroup => "生物",
            CombatUnitId.Instance instance when view.FindInstance(instance.Id) is Lookup<InstanceView>.Found
                { Value.Ownership: SettlementRelation.PlayerOwned } => "我方",
            _ => "敌方"
        };
        var text = $"{name} · {side}";
        return participant.Retreating ? $"{text} · 撤退中" : text;
    }

    private static WorldObjectId? UnitObject(CombatUnitId unit) => unit switch
    {
        CombatUnitId.Instance instance => new WorldObjectId.Instance(instance.Id),
        CombatUnitId.CreatureGroup group => new WorldObjectId.CreatureGroup(group.Id),
        _ => null
    };
}
