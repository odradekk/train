using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class MacroMap
{
    internal Rect2? OverlayArea { get; set; }
    private Rect2 MapArea => OverlayArea ?? new Rect2(Vector2.Zero, Size);
    private readonly Dictionary<TrainId, Vector2> _trainOffsets = [];

    internal void PositionTools()
    {
        if (_tools is null) return;
        var area = MapArea;
        _tools.Position = new(area.Position.X, Math.Max(area.Position.Y, area.End.Y - _tools.Size.Y));
    }

    internal Vector2? ObjectPoint(WorldObjectId id) => id switch
    {
        WorldObjectId.Node node when _nodes.TryGetValue(node.Id, out var map) => MapToScreen(map.Position),
        WorldObjectId.Settlement settlement when _world?.Nodes.FirstOrDefault(item =>
            item.Occupant is NodeOccupant.Settlement occupied && occupied.Id == settlement.Id) is { } map => MapToScreen(map.Position),
        WorldObjectId.Edge edge when _edges.TryGetValue(edge.Id, out var map) => EdgeDistancePoint(edge.Id, map.LengthMeters / 2),
        WorldObjectId.Train train when _world?.Trains.FirstOrDefault(item => item.Id == train.Id) is { } map => TrainPoint(map),
        WorldObjectId.Combat combat when _world?.Combats.FirstOrDefault(item => item.Id == combat.Id) is { } map => CombatPoint(map),
        _ => null
    };

    internal void CenterOn(WorldObjectId id)
    {
        if (ObjectPoint(id) is not { } point) return;
        Offset += MapArea.GetCenter() - point;
        QueueRedraw();
    }

    private void ArrangeTrains()
    {
        _trainOffsets.Clear();
        foreach (var group in _world!.Trains.Where(train => train.Position is TrainPosition.Node)
                     .GroupBy(train => ((TrainPosition.Node)train.Position).Id))
        {
            var remaining = group.OrderBy(train => train.Id.Value).ToArray();
            var index = 0;
            // 标记的点击矩形为 28×14；环间距和同环弦长均大于其对角线。
            for (var radius = 40f; index < remaining.Length; radius += 36)
            {
                var capacity = (int)MathF.Floor(MathF.PI / MathF.Asin(18 / radius));
                var count = Math.Min(capacity, remaining.Length - index);
                for (var slot = 0; slot < count; slot++)
                    _trainOffsets[remaining[index++].Id] = Vector2.FromAngle(-MathF.PI / 4 + MathF.Tau * slot / count) * radius;
            }
        }
    }
    private PopupPanel _layers = null!;
    private PanelContainer _tools = null!;
    private WorldObjectId? _clickedObject;
    internal Rect2 ToolsBounds => _tools.GetGlobalRect();

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        Resized += OnResized;
        VisibilityChanged += () => { if (!IsVisibleInTree()) { CloseLayers(); _clickedObject = null; } };
        _layers = new PopupPanel { Name = "InformationLayers", WrapControls = true };
        var body = new VBoxContainer { Name = "Body" };
        body.AddThemeConstantOverride("separation", 6);
        body.AddChild(UiStyle.Label("地图信息层", true));
        _resources = new CheckBox { Name = "Resources", Text = "节点资源" };
        _platforms = new CheckBox { Name = "Platforms", Text = "站台占用" };
        _creatures = new CheckBox { Name = "Creatures", Text = "世界生物" };
        AddLayer(_resources, "货物图标 · 名称与剩余储量");
        AddLayer(_platforms, "站台 占用/总数 · 排队列车数");
        AddLayer(_creatures, "群活动 · 聚落类型与群数");
        _layers.AddChild(body);
        AddChild(_layers);
        _tools = new PanelContainer { Name = "MapTools", MouseForcePassScrollEvents = false };
        var buttons = new HBoxContainer { Name = "Buttons" };
        buttons.AddChild(UiStyle.Button("ZoomOut", "−", () => ChangeZoom(1 / 1.2f, MapArea.GetCenter())));
        buttons.AddChild(UiStyle.Button("ZoomIn", "+", () => ChangeZoom(1.2f, MapArea.GetCenter())));
        buttons.AddChild(UiStyle.Button("FitMap", "全图", () => { _fitPending = true; FitCamera(); }));
        var layersButton = UiStyle.Button("Layers", "图层", ToggleLayers);
        // 原生弹窗失焦后延迟隐藏；与 MenuButton 一样在按下时切换，避免释放时重新打开。
        layersButton.ActionMode = BaseButton.ActionModeEnum.Press;
        buttons.AddChild(layersButton);
        buttons.GetChild<Button>(0).TooltipText = "缩小地图";
        buttons.GetChild<Button>(1).TooltipText = "放大地图";
        _tools.AddChild(buttons);
        AddChild(_tools);
        _tools.Resized += PositionTools;

        void AddLayer(CheckBox toggle, string legend)
        {
            body.AddChild(toggle);
            var label = UiStyle.Label(legend);
            label.AddThemeFontSizeOverride("font_size", 12);
            label.AddThemeColorOverride("font_color", new Color("#9aa3ad"));
            body.AddChild(label);
            toggle.Toggled += _ => QueueRedraw();
        }
    }

    internal bool CloseLayers()
    {
        if (_layers?.Visible != true) return false;
        _layers.Hide();
        return true;
    }

    private void ToggleLayers()
    {
        if (CloseLayers()) return;
        var minimum = _layers.GetContentsMinimumSize().Ceil();
        // 嵌入弹窗使用父视口的逻辑坐标，由父窗口统一缩放。
        var position = _tools.GlobalPosition.Ceil() - new Vector2(0, minimum.Y + 8);
        _layers.PopupOnParent(new((Vector2I)position, (Vector2I)minimum));
    }

    private void OnResized()
    {
        FitCamera();
        PositionTools();
        QueueRedraw();
    }

    private void FitCamera()
    {
        var area = MapArea;
        if (!_fitPending || _world is null || area.Size.X <= 0 || area.Size.Y <= 0) return;
        Zoom = Math.Min(Math.Max(1, area.Size.X - 120) / (float)Math.Max(1, _world.WidthMeters),
            Math.Max(1, area.Size.Y - 120) / (float)Math.Max(1, _world.HeightMeters));
        _fitZoom = Zoom;
        Offset = area.GetCenter() - new Vector2((float)_world.WidthMeters, (float)_world.HeightMeters) * Zoom / 2;
        _fitPending = false;
        QueueRedraw();
    }

    private void ChangeZoom(float factor, Vector2 center)
    {
        var next = Math.Clamp(Zoom * factor, _fitZoom / 8, _fitZoom * 8);
        Offset = center - (center - Offset) * (next / Zoom);
        Zoom = next;
        QueueRedraw();
    }

    internal Vector2 MapToScreen(MapPoint point) => new Vector2((float)point.X, (float)point.Y) * Zoom + Offset;

    public override void _Input(InputEvent input)
    {
        if (!IsVisibleInTree() || input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } button) return;
        var previous = _clickedObject;
        _clickedObject = null;
        // 标记可能移动或被新检查器遮挡；双击仍属于第一次地图选中的对象。
        if (button.DoubleClick && previous is not null)
        {
            RaiseObjectActivated(previous);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton button)
        {
            if (button.ButtonIndex == MouseButton.Middle) _dragging = button.Pressed;
            else if (button.Pressed && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
                ChangeZoom(button.ButtonIndex == MouseButton.WheelUp ? 1.2f : 1 / 1.2f, button.Position);
            else if (button.Pressed && button.ButtonIndex == MouseButton.Left && HitTest(button.Position) is { } hit)
            {
                _clickedObject = hit;
                RaiseObjectClicked(hit);
            }
            AcceptEvent();
        }
        else if (input is InputEventMouseMotion motion && _dragging)
        {
            if ((motion.ButtonMask & MouseButtonMask.Middle) == 0) _dragging = false;
            else { Offset += motion.Relative; QueueRedraw(); }
            AcceptEvent();
        }
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        if (View is null || _content is null || HitTest(atPosition) is not { } id) return "";
        var text = id switch
        {
            WorldObjectId.Node node => WorldPanels.NodeName(node.Id, View, _content) +
                (_nodes[node.Id].Occupant switch
                { NodeOccupant.Settlement settlement => "\n" + WorldPanels.Relation(settlement.Relation),
                    NodeOccupant.Lair => "", _ => "\n未建立聚居地" }),
            WorldObjectId.Edge edge => WorldPanels.NodeName(_edges[edge.Id].Start, View, _content) + "—" +
                WorldPanels.NodeName(_edges[edge.Id].End, View, _content) + "\n" + WorldListDrawer.EdgeStatus(_edges[edge.Id]),
            WorldObjectId.Train train => $"列车 {train.Id.Value}\n" + WorldPanels.Activity(_world!.Trains.First(item => item.Id == train.Id).Activity),
            WorldObjectId.Combat combat => "战斗\n" + WorldPanels.LocationText(_world!.Combats.First(item => item.Id == combat.Id).Location, View, _content) + " · 交战中",
            _ => ""
        };
        return text + "\n单击查看，双击进入场景";
    }
}
