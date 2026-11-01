using Godot;
using Train.App;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class TrainPlanMap : Control
{
    internal event Action<NodeId, bool>? PlaceClicked;
    private WorldSummary? _world;
    private Func<NodeId, string> _name = _ => "";
    private NodeId[] _stops = [];
    private IReadOnlyList<PlanLeg> _legs = [];
    private HashSet<EdgeId> _route = [];
    private float _zoom = 1, _fitZoom = 1;
    private Vector2 _offset;
    private bool _dragging, _fitPending;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop; ClipContents = true; FocusMode = FocusModeEnum.All;
        Resized += Fit;
    }
    internal void SetWorld(WorldSummary world, Func<NodeId, string> name)
    {
        _fitPending |= _world is null || _world.WorldSeed != world.WorldSeed ||
            _world.WidthMeters != world.WidthMeters || _world.HeightMeters != world.HeightMeters ||
            !_world.Nodes.Select(node => (node.Id, node.Position)).SequenceEqual(world.Nodes.Select(node => (node.Id, node.Position)));
        _world = world; _name = name; Fit(); QueueRedraw();
    }
    internal void SetPlan(NodeId[] stops, IReadOnlyList<PlanLeg> legs)
    {
        _stops = stops; _legs = legs;
        _route = legs.Where(leg => leg.Edges is not null).SelectMany(leg => leg.Edges!.Value).ToHashSet();
        QueueRedraw();
    }
    private void Fit()
    {
        if (_world is null || Size.X <= 0 || Size.Y <= 0) return;
        if (_fitPending)
        {
            _zoom = Math.Min(Math.Max(1, Size.X - 60) / (float)Math.Max(1, _world.WidthMeters),
                Math.Max(1, Size.Y - 60) / (float)Math.Max(1, _world.HeightMeters));
            _fitZoom = _zoom;
            _offset = (Size - new Vector2((float)_world.WidthMeters, (float)_world.HeightMeters) * _zoom) / 2;
            _fitPending = false;
        }
        QueueRedraw();
    }
    internal Vector2 MapToScreen(MapPoint point) => new Vector2((float)point.X, (float)point.Y) * _zoom + _offset;
    public override void _GuiInput(InputEvent input)
    {
        if (_world is null) return;
        if (input is InputEventMouseButton button)
        {
            if (button.ButtonIndex == MouseButton.Middle) _dragging = button.Pressed;
            else if (button.Pressed && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                var zoom = Math.Clamp(_zoom * (button.ButtonIndex == MouseButton.WheelUp ? 1.2f : 1 / 1.2f), _fitZoom / 4, _fitZoom * 8);
                _offset = button.Position - (button.Position - _offset) * (zoom / _zoom); _zoom = zoom; QueueRedraw();
            }
            else if (button.Pressed && button.ButtonIndex == MouseButton.Left)
            {
                GrabFocus();
                if (At(button.Position) is { } node) PlaceClicked?.Invoke(node.Id, button.ShiftPressed);
            }
            AcceptEvent();
        }
        else if (input is InputEventMouseMotion motion)
        {
            TooltipText = At(motion.Position) is { } node ? _name(node.Id) : "滚轮缩放；中键拖动地图";
            if (_dragging && (motion.ButtonMask & MouseButtonMask.Middle) != 0)
            { _offset += motion.Relative; QueueRedraw(); AcceptEvent(); }
            else _dragging = false;
        }
    }
    private NodeMapItem? At(Vector2 point) => _world?.Nodes.Reverse().FirstOrDefault(node => MapToScreen(node.Position).DistanceTo(point) <= 16);
    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), new Color("#5f6b55"));
        if (_world is null) return;
        var nodes = _world.Nodes.ToDictionary(node => node.Id);
        foreach (var edge in _world.Edges)
        {
            if (!nodes.TryGetValue(edge.Start, out var start) || !nodes.TryGetValue(edge.End, out var end)) continue;
            var from = MapToScreen(start.Position); var to = MapToScreen(end.Position);
            var intact = Enumerable.Range(1, edge.Tracks).Any(track => !edge.Breaks.Any(broken => broken.Track == track));
            if (intact) DrawLine(from, to, new Color("#252b29"), 2, true);
            else DrawDashedLine(from, to, new Color("#30352d88"), 1, 5, true);
            if (_route.Contains(edge.Id)) DrawLine(from, to, UiStyle.Warning, 4, true);
        }
        foreach (var leg in _legs.Where(leg => leg.Edges is null))
            if (nodes.TryGetValue(leg.From, out var start) && nodes.TryGetValue(leg.To, out var end))
                DrawDashedLine(MapToScreen(start.Position), MapToScreen(end.Position), UiStyle.Error, 3, 7, true);
        var font = UiStyle.NumericFont();
        foreach (var node in _world.Nodes)
        {
            var point = MapToScreen(node.Position);
            var color = node.Occupant switch
            {
                NodeOccupant.Settlement settlement => UiStyle.RelationColor(settlement.Relation),
                NodeOccupant.Lair => UiStyle.Error,
                _ => UiStyle.Foreground
            };
            DrawCircle(point, 8, color); DrawArc(point, 8, 0, Mathf.Tau, 24, new Color("#252b29"), 2, true);
            var text = _name(node.Id); var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, 14).X;
            DrawStringOutline(font, point + new Vector2(-width / 2, 27), text, HorizontalAlignment.Left, -1, 14, 3, new Color("#182021"));
            DrawString(font, point + new Vector2(-width / 2, 27), text, HorizontalAlignment.Left, -1, 14, UiStyle.Foreground);
            var orders = _stops.Select((id, index) => (id, index)).Where(item => item.id == node.Id).Select(item => item.index + 1).ToArray();
            if (orders.Length > 0)
            {
                var order = orders[0].ToString() + (orders.Length > 1 ? "+" : "");
                DrawCircle(point + new Vector2(13, -13), 11, UiStyle.Warning);
                DrawString(font, point + new Vector2(8, -8), order, HorizontalAlignment.Left, -1, 13, new Color("#182021"));
            }
        }
    }
}
