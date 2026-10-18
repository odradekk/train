using Godot;
using Train.App;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal partial class NodeMinimap : Control
{
    private const float HeadingLength = 16;
    private readonly Control _background = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Control _marker = new() { MouseFilter = MouseFilterEnum.Ignore, ClipContents = true };
    private SceneNodeOverview? _overview;
    private ScenePanBounds? _bounds;
    private ISceneFrame? _frame;
    private WorldSceneController? _scenes;
    private NodeId? _node;
    private HostBinding _binding;
    private ICoreReadView? _occupancyView;
    private readonly Dictionary<int, PlotOccupant> _occupants = [];
    private double _occupancyDelay;
    private bool _dragging;
    private Vector2 _focus, _camera, _forward;
    internal Vector2 FocusOnMap => NodeToMap(_focus);
    internal Vector2 HeadingTipOnMap => FocusOnMap + _forward * HeadingLength;

    internal NodeMinimap()
    {
        Name = "NodeMinimap";
        Size = new(240, 200);
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        MouseForcePassScrollEvents = false;
        TooltipText = "单击或拖动定位；跟随列车时切换为自由相机";
        AddChild(_background);
        AddChild(_marker);
        _background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _marker.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _background.Draw += DrawMap;
        _marker.Draw += DrawCamera;
        var title = UiStyle.Label("节点小地图");
        title.Position = new(8, 1);
        title.AddThemeFontSizeOverride("font_size", 14);
        AddChild(title);
        Resized += Redraw;
    }

    internal void Refresh(WorldSceneController? scenes, ICoreReadView? core, HostBinding binding, double delta)
    {
        if (core is null || scenes?.View.Visible != true || scenes.ViewedNode(core) is not { } node ||
            scenes.MinimapFrame is not { NodeOverview: { } overview, NodePanBounds: { } bounds } frame)
        { Clear(); return; }
        if (_node != node || _binding != binding) Clear();
        _scenes = scenes; _node = node; _binding = binding; _frame = frame;
        Visible = true;
        if (!ReferenceEquals(_overview, overview) || _bounds != bounds)
        { _overview = overview; _bounds = bounds; Redraw(); }
        _occupancyDelay -= delta;
        if (_occupancyView is null || _occupancyDelay <= 0 && !ReferenceEquals(core, _occupancyView))
        {
            var owner = core.World.Nodes.FirstOrDefault(item => item.Id == node)?.Occupant;
            var plots = owner is NodeOccupant.Settlement settlement &&
                core.FindSettlement(settlement.Id) is Lookup<SettlementView>.Found found ? found.Value.Plots : [];
            if (_occupants.Count != plots.Length || plots.Any(plot =>
                !_occupants.TryGetValue(plot.TemplatePlotNumber, out var previous) || previous != plot.Occupant))
            {
                _occupants.Clear();
                foreach (var plot in plots) _occupants[plot.TemplatePlotNumber] = plot.Occupant;
                _background.QueueRedraw();
            }
            _occupancyView = core;
            _occupancyDelay = .5;
        }
        var rig = scenes.View.CameraRig;
        var shift = new Vector2((float)(rig.FrameOrigin.X - frame.Origin.X),
            (float)(rig.FrameOrigin.Z - frame.Origin.Z));
        var focus = new Vector2(rig.Focus.X, rig.Focus.Z) + shift;
        var camera = new Vector2(rig.Camera.Position.X, rig.Camera.Position.Z) + shift;
        var forward = new Vector2(-rig.Camera.Basis.Z.X, -rig.Camera.Basis.Z.Z).Normalized();
        if (_focus != focus || _camera != camera || _forward != forward)
        { _focus = focus; _camera = camera; _forward = forward; _marker.QueueRedraw(); }
    }

    internal void Clear()
    {
        Visible = false;
        _dragging = false;
        _overview = null; _bounds = null; _frame = null; _node = null;
        _occupancyView = null;
        _occupants.Clear();
        _occupancyDelay = 0;
    }

    private Rect2 MapRect
    {
        get
        {
            if (_bounds is not { } bounds) return new();
            var extent = new Vector2(bounds.Maximum.X - bounds.Minimum.X, bounds.Maximum.Y - bounds.Minimum.Y);
            var available = new Vector2(Size.X - HeadingLength * 2, Size.Y - HeadingLength * 2 - 24);
            var scale = Mathf.Min(available.X / extent.X, available.Y / extent.Y);
            var size = extent * scale;
            return new(new Vector2(HeadingLength, HeadingLength + 24) + (available - size) / 2, size);
        }
    }

    internal Vector2 NodeToMap(Vector2 point)
    {
        var bounds = _bounds!;
        var minimum = new Vector2(bounds.Minimum.X, bounds.Minimum.Y);
        var extent = new Vector2(bounds.Maximum.X, bounds.Maximum.Y) - minimum;
        return MapRect.Position + (point - minimum) / extent * MapRect.Size;
    }

    internal Vector2 MapToNode(Vector2 point)
    {
        var bounds = _bounds!;
        var fraction = ((point - MapRect.Position) / MapRect.Size).Clamp(Vector2.Zero, Vector2.One);
        return new Vector2(bounds.Minimum.X, bounds.Minimum.Y) + fraction *
            new Vector2(bounds.Maximum.X - bounds.Minimum.X, bounds.Maximum.Y - bounds.Minimum.Y);
    }

    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed) _dragging = true;
            if (_dragging) Navigate(button.Position);
            if (!button.Pressed) _dragging = false;
        }
        else if (input is InputEventMouseMotion motion && _dragging && motion.ButtonMask.HasFlag(MouseButtonMask.Left))
            Navigate(motion.Position);
        AcceptEvent();
    }

    private void Navigate(Vector2 pixel)
    {
        if (_frame is null || _node is not { } node) return;
        var point = MapToNode(pixel);
        _scenes!.FocusNode(node, new(_frame.Origin.X + point.X, 0, _frame.Origin.Z + point.Y));
    }

    private void Redraw()
    { _background.QueueRedraw(); _marker.QueueRedraw(); }

    private void DrawMap()
    {
        _background.DrawRect(new(Vector2.Zero, Size), new Color("17242d"));
        _background.DrawRect(new(Vector2.Zero, Size), new Color("6d8088"), false);
        if (_overview is null) return;
        foreach (var plot in _overview.Plots)
        {
            var corners = plot.Corners.Select(point => NodeToMap(new(point.X, point.Y))).ToArray();
            var station = plot.Size is PlotSize.SmallStation or PlotSize.LargeStation;
            _background.DrawColoredPolygon(corners, new Color(station ? "63583a" : "304139"));
            _background.DrawPolyline([.. corners, corners[0]], new Color(station ? "e3bd69" : "82988c"), 1, true);
            if (_occupants.GetValueOrDefault(plot.TemplatePlotNumber) is not (null or PlotOccupant.Empty))
            {
                var center = corners.Aggregate(Vector2.Zero, (total, point) => total + point) / corners.Length;
                var inner = corners.Select(point => center + (point - center) * .65f).ToArray();
                _background.DrawColoredPolygon(inner, new Color(
                    _occupants[plot.TemplatePlotNumber] is PlotOccupant.Construction ? "dc9556" : "8bb4c7"));
            }
        }
        foreach (var track in _overview.Tracks)
        {
            if (track.Length < 2) continue;
            List<Vector2> pixels = [NodeToMap(new(track[0].X, track[0].Y))];
            for (var i = 1; i < track.Length - 1; i++)
            {
                var point = NodeToMap(new(track[i].X, track[i].Y));
                if (point.DistanceSquaredTo(pixels[^1]) >= 1) pixels.Add(point);
            }
            pixels.Add(NodeToMap(new(track[^1].X, track[^1].Y)));
            _background.DrawPolyline(pixels.ToArray(), new Color("dbd6bf"), 1, true);
        }
    }

    private void DrawCamera()
    {
        if (_bounds is null) return;
        var focus = FocusOnMap;
        var camera = NodeToMap(_camera).Clamp(MapRect.Position, MapRect.End);
        var side = _forward.Orthogonal();
        _marker.DrawLine(camera, focus, new Color("f1c863"), 1, true);
        _marker.DrawCircle(focus, 3, Colors.White);
        _marker.DrawArc(focus, 5, 0, Mathf.Tau, 16, new Color("f1c863"), 1, true);
        var tip = HeadingTipOnMap;
        _marker.DrawLine(focus, tip, new Color("f1c863"), 2, true);
        _marker.DrawColoredPolygon([tip, tip - _forward * 6 + side * 4,
            tip - _forward * 6 - side * 4], new Color("f1c863"));
    }
}
