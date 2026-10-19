using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class MacroMap : Control
{
    private static readonly Color Ink = new("#2a2a2a");
    private readonly Texture2D _lairSymbol = GD.Load<Texture2D>("res://Ui/Symbols/lair.png");
    private readonly Dictionary<TrainActivity, Texture2D> _trainSymbols = new()
    {
        [TrainActivity.Running] = GD.Load<Texture2D>("res://Ui/Symbols/train-running.png"),
        [TrainActivity.Servicing] = GD.Load<Texture2D>("res://Ui/Symbols/train-servicing.png"),
        [TrainActivity.Waiting] = GD.Load<Texture2D>("res://Ui/Symbols/train-waiting.png"),
        [TrainActivity.NoRoute] = GD.Load<Texture2D>("res://Ui/Symbols/warning.png"),
        [TrainActivity.Combat] = GD.Load<Texture2D>("res://Ui/Symbols/train-combat.png"),
        [TrainActivity.Retreating] = GD.Load<Texture2D>("res://Ui/Symbols/train-retreating.png")
    };
    private IContentView? _content;
    private WorldSummary? _world;
    private readonly Dictionary<NodeId, NodeMapItem> _nodes = [];
    private readonly Dictionary<EdgeId, EdgeMapItem> _edges = [];
    private readonly Dictionary<AssetRevision, ImageTexture?> _symbols = [];
    private readonly HashSet<string> _failures = [];
    private readonly List<(Vector2[] Points, Color Color)> _regions = [];
    private bool _dragging;
    private bool _fitPending;
    private float _fitZoom = 1;
    private PanelContainer _layers = null!;
    private CheckBox _resources = null!, _platforms = null!;
    private float _layersTop;
    private readonly Dictionary<NodeId, int> _platformQueues = [];
    private ICoreReadView? _queueView;
    private double _queueDelay;

    internal ICoreReadView? View { get; private set; }
    internal Vector2 Offset { get; private set; }
    internal float Zoom { get; private set; } = 1;
    internal event Action<WorldObjectId>? ObjectClicked;
    internal event Action<string>? DisplayFailed;

    internal void RaiseObjectClicked(WorldObjectId target) => ObjectClicked?.Invoke(target);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        Resized += OnResized;
        _layers = new PanelContainer { Name = "InformationLayers", CustomMinimumSize = new(280, 0),
            MouseFilter = MouseFilterEnum.Stop, MouseForcePassScrollEvents = false };
        var body = new VBoxContainer { Name = "Body", MouseFilter = MouseFilterEnum.Ignore };
        body.AddThemeConstantOverride("separation", 6);
        body.AddChild(UiStyle.Label("地图信息层", true));
        _resources = new CheckBox { Name = "Resources", Text = "节点资源" };
        _platforms = new CheckBox { Name = "Platforms", Text = "站台占用" };
        AddLayer(_resources, "货物图标 · 名称与剩余储量");
        AddLayer(_platforms, "站台 占用/总数 · 排队列车数");
        AddLayer(new CheckBox { Name = "Creatures", Text = "世界生物", Disabled = true,
            TooltipText = "阶段 5 接入世界生物信息层" }, "阶段 5 接入，图例待开放");
        _layers.AddChild(body);
        AddChild(_layers);
        _layers.Resized += () => PositionLayerPanel(_layersTop);

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

    internal void PositionLayerPanel(float top)
    {
        _layersTop = top;
        _layers.Position = new(Math.Max(8, Size.X - _layers.Size.X - 12), top);
    }

    internal void RefreshPlatformQueues(double delta)
    {
        if (!IsVisibleInTree() || !_platforms.ButtonPressed || View is null)
        { _queueView = null; _platformQueues.Clear(); _queueDelay = 0; return; }
        _queueDelay -= delta;
        if (_queueView is not null && (_queueDelay > 0 || ReferenceEquals(View, _queueView))) return;
        _platformQueues.Clear();
        foreach (var train in View.World.Trains)
            if (train.Position is TrainPosition.Node { PlatformNumber: null } node &&
                View.FindTrain(train.Id) is Lookup<TrainView>.Found { Value.PlatformQueueEnteredStep: not null })
                _platformQueues[node.Id] = _platformQueues.GetValueOrDefault(node.Id) + 1;
        _queueView = View;
        _queueDelay = .5;
        QueueRedraw();
    }

    internal void SetView(ICoreReadView view, IContentView content, bool resetCamera = false)
    {
        var world = view.World;
        var geometryChanged = _world is null || _world.WorldSeed != world.WorldSeed ||
            _world.WidthMeters != world.WidthMeters || _world.HeightMeters != world.HeightMeters ||
            _world.Nodes.Length != world.Nodes.Length ||
            world.Nodes.Any(n => !_nodes.TryGetValue(n.Id, out var old) ||
                old.Position != n.Position || old.Terrain.TypeId != n.Terrain.TypeId);
        if (_content?.Generation != content.Generation || !ReferenceEquals(_content, content))
            ReleaseSymbols();
        View = view;
        _content = content;
        _world = world;
        _nodes.Clear();
        foreach (var node in world.Nodes) _nodes.Add(node.Id, node);
        _edges.Clear();
        foreach (var edge in world.Edges) _edges.Add(edge.Id, edge);
        if (geometryChanged) BuildRegions();
        if (geometryChanged || resetCamera)
        {
            _queueView = null;
            _platformQueues.Clear();
            _queueDelay = 0;
            _dragging = false;
            _fitPending = true;
            FitCamera();
        }
        QueueRedraw();
    }

    private void OnResized()
    {
        FitCamera();
        PositionLayerPanel(_layersTop);
        QueueRedraw();
    }

    private void FitCamera()
    {
        if (!_fitPending || _world is null || Size.X <= 0 || Size.Y <= 0) return;
        Zoom = Math.Min(Math.Max(1, Size.X - 120) / (float)Math.Max(1, _world.WidthMeters),
            Math.Max(1, Size.Y - 120) / (float)Math.Max(1, _world.HeightMeters));
        _fitZoom = Zoom;
        Offset = (Size - new Vector2((float)_world.WidthMeters, (float)_world.HeightMeters) * Zoom) / 2;
        _fitPending = false;
    }

    internal Vector2 MapToScreen(MapPoint point) => new Vector2((float)point.X, (float)point.Y) * Zoom + Offset;

    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton button)
        {
            if (button.ButtonIndex == MouseButton.Middle) _dragging = button.Pressed;
            else if (button.Pressed && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                var next = Math.Clamp(Zoom * (button.ButtonIndex == MouseButton.WheelUp ? 1.2f : 1 / 1.2f),
                    _fitZoom / 8, _fitZoom * 8);
                Offset = button.Position - (button.Position - Offset) * (next / Zoom);
                Zoom = next;
                QueueRedraw();
            }
            else if (button.Pressed && button.ButtonIndex == MouseButton.Left && HitTest(button.Position) is { } hit)
                RaiseObjectClicked(hit);
            AcceptEvent();
        }
        else if (input is InputEventMouseMotion motion && _dragging)
        {
            if ((motion.ButtonMask & MouseButtonMask.Middle) == 0) _dragging = false;
            else { Offset += motion.Relative; QueueRedraw(); }
            AcceptEvent();
        }
    }

    internal WorldObjectId? HitTest(Vector2 point)
    {
        if (_world is null) return null;
        foreach (var train in _world.Trains.Reverse())
            if (TrainPoint(train) is { } p && new Rect2(p - new Vector2(14, 7), new Vector2(28, 14)).HasPoint(point))
                return new WorldObjectId.Train(train.Id);
        foreach (var node in _world.Nodes.Reverse())
            if (MapToScreen(node.Position).DistanceTo(point) <= NodeDisplay(node).Radius)
                return new WorldObjectId.Node(node.Id);
        foreach (var edge in _world.Edges)
            if (EdgePoints(edge) is { } ends && Geometry2D.GetClosestPointToSegment(point, ends.Start, ends.End).DistanceTo(point) <= 6)
                return new WorldObjectId.Edge(edge.Id);
        return null;
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("#b7c4a6"));
        if (_world is null) return;
        foreach (var region in _regions)
        {
            var points = region.Points.Select(p => p * Zoom + Offset).ToArray();
            DrawColoredPolygon(points, region.Color);
            DrawPolyline([.. points, points[0]], new Color("#00000022"), 1, true);
        }
        foreach (var edge in _world.Edges) DrawEdge(edge);
        foreach (var node in _world.Nodes) DrawNode(node);
        if (_resources.ButtonPressed || _platforms.ButtonPressed)
            foreach (var node in _world.Nodes) DrawInformation(node);
        foreach (var train in _world.Trains) DrawTrain(train);
    }

    internal string ResourceText(NodeResourceView resource) =>
        $"{WorldPanels.GoodName(resource.Good, _content!)}：{resource.Quantity}";

    internal AssetRevision? ResourceIcon(NodeResourceView resource) =>
        _content?.FindGood(resource.Good) is ContentLookup<GoodPresentation>.Found good ? good.Value.Display.Icon : null;

    internal string PlatformText(NodeMapItem node) =>
        $"站台 {node.OccupiedPlatforms}/{node.Platforms} · 排队 {_platformQueues.GetValueOrDefault(node.Id)}";

    private void DrawInformation(NodeMapItem node)
    {
        var font = UiStyle.NumericFont();
        var point = MapToScreen(node.Position) + new Vector2(0, NodeDisplay(node).Radius + 32);
        if (_resources.ButtonPressed)
            foreach (var resource in node.Resources)
            {
                var rectangle = DrawAnnotation(ResourceText(resource), point, 24);
                if (ResourceIcon(resource) is { } revision && FindSymbol(revision) is { } texture)
                    DrawTextureRect(texture, new(rectangle.Position + new Vector2(3, 2), new Vector2(20, 20)), false);
                point.Y += 26;
            }
        if (_platforms.ButtonPressed && (node.Platforms > 0 || _platformQueues.GetValueOrDefault(node.Id) > 0))
            DrawAnnotation(PlatformText(node), point, 0);

        Rect2 DrawAnnotation(string text, Vector2 center, int iconWidth)
        {
            var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, 14).X + iconWidth + 12;
            var rectangle = new Rect2(center - new Vector2(width / 2, 0), new(width, 24));
            DrawRect(rectangle, new Color(24 / 255f, 28 / 255f, 33 / 255f, .9f));
            DrawString(font, rectangle.Position + new Vector2(iconWidth + 6, 17), text,
                HorizontalAlignment.Left, -1, 14, UiStyle.Foreground);
            return rectangle;
        }
    }

    private (Vector2 Start, Vector2 End)? EdgePoints(EdgeMapItem edge) =>
        _nodes.TryGetValue(edge.Start, out var start) && _nodes.TryGetValue(edge.End, out var end)
            ? (MapToScreen(start.Position), MapToScreen(end.Position)) : null;

    private void DrawEdge(EdgeMapItem edge)
    {
        if (EdgePoints(edge) is not { } ends) return;
        if (edge.Tracks == 0)
            DrawDashedLine(ends.Start, ends.End, new Color(Ink, .5f), 1, 4);
        else
        {
            var trackNormal = (ends.End - ends.Start).Normalized().Orthogonal();
            DrawLine(ends.Start, ends.End, Colors.White, edge.Tracks == 1 ? 7 : 5 * (edge.Tracks - 1) + 6, true);
            for (var track = 0; track < edge.Tracks; track++)
            {
                var offset = trackNormal * ((track - (edge.Tracks - 1) / 2f) * 5);
                DrawLine(ends.Start + offset, ends.End + offset, Ink, 2, true);
            }
        }
        var delta = ends.End - ends.Start;
        if (delta.LengthSquared() < 1) return;
        var normal = delta.Normalized().Orthogonal();
        var midpoint = (ends.Start + ends.End) / 2;
        if (!edge.Works.IsDefaultOrEmpty && edge.Works.Any(work => !work.InvestedGoods.IsDefaultOrEmpty))
        {
            DrawDashedLine(ends.Start, ends.End, UiStyle.Warning, 2, 4);
            DrawConstructionMark(midpoint - normal * 16);
        }
        if (!edge.Breaks.IsDefaultOrEmpty) DrawBreakMark(midpoint + normal * 16);
    }

    private void DrawConstructionMark(Vector2 point)
    {
        DrawRect(new Rect2(point - new Vector2(6, 6), new Vector2(12, 12)), UiStyle.Warning);
        DrawLine(point + new Vector2(-4, 3), point + new Vector2(0, -4), Colors.White, 2);
        DrawLine(point + new Vector2(0, -4), point + new Vector2(4, 3), Colors.White, 2);
    }

    private void DrawBreakMark(Vector2 point)
    {
        DrawCircle(point, 7, UiStyle.Error);
        DrawLine(point + new Vector2(-5, -2), point + new Vector2(-1, -2), Colors.White, 2);
        DrawLine(point + new Vector2(1, 2), point + new Vector2(5, 2), Colors.White, 2);
    }

    private void DrawNode(NodeMapItem node)
    {
        var point = MapToScreen(node.Position);
        var (radius, name, revision) = NodeDisplay(node);
        if (node.Occupant is NodeOccupant.Empty)
        {
            DrawCircle(point, radius, Colors.White);
            DrawArc(point, radius, 0, Mathf.Tau, 32, Ink, 2, true);
            DrawMapText(name, point + new Vector2(0, radius + 21), new Color("#1e1e1e"));
            return;
        }
        var color = node.Occupant switch
        {
            NodeOccupant.Settlement { Relation: SettlementRelation.PlayerOwned } => new Color("#2b77d0"),
            NodeOccupant.Lair or NodeOccupant.Settlement { Relation: SettlementRelation.Hostile } => new Color("#c9423a"),
            _ => new Color("#7f8893")
        };
        DrawCircle(point, radius, Colors.White);
        DrawArc(point, radius, 0, Mathf.Tau, 48, color, 3, true);
        if (node.Occupant is NodeOccupant.Lair or NodeOccupant.Settlement { Relation: SettlementRelation.Hostile })
            for (var i = 0; i < 12; i++) DrawArc(point, radius + 6, i * Mathf.Tau / 12, (i + .55f) * Mathf.Tau / 12, 4, color, 2, true);
        var symbol = node.Occupant is NodeOccupant.Lair ? _lairSymbol : revision is { } asset ? FindSymbol(asset) : null;
        if (symbol is not null) DrawTextureRect(symbol, new Rect2(point - new Vector2(12, 12), new Vector2(24, 24)), false, color);
        else
        {
            DrawMapText("?", point + new Vector2(0, 6), color);
            Fail($"节点 {node.Id.Value} 缺少可显示的地图符号。");
        }
        DrawMapText(name, point + new Vector2(0, radius + 21), new Color("#1e1e1e"));
    }

    private (float Radius, string Name, AssetRevision? Symbol) NodeDisplay(NodeMapItem node)
    {
        var radius = node.Occupant is NodeOccupant.Empty ? 6f : 16f;
        var name = $"节点 {node.Id.Value}";
        AssetRevision? symbol = null;
        if (node.Occupant is NodeOccupant.Settlement occupied && _content is not null)
        {
            if (_content.FindSettlementLayout(occupied.Id) is ContentLookup<SettlementLayoutProjection>.Found layout)
            {
                name = layout.Value.Display.Name;
                var template = _content.Core.SettlementTemplates.FirstOrDefault(t => t.Id == layout.Value.SourceTemplate);
                if (template is not null && _content.FindDefinitionDisplay(new CoreDefinitionReference.SettlementType(template.Type)) is ContentLookup<ContentDisplay>.Found display)
                {
                    radius = display.Value.Major ? 20 : 16;
                    symbol = display.Value.MapSymbol;
                }
            }
        }
        return (radius, name, symbol);
    }

    private void DrawMapText(string text, Vector2 center, Color color)
    {
        var font = GetThemeFont("font", "Title");
        const int size = 15;
        var position = center - new Vector2(font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X / 2, 0);
        DrawStringOutline(font, position, text, HorizontalAlignment.Left, -1, size, 4, Colors.White);
        DrawString(font, position, text, HorizontalAlignment.Left, -1, size, color);
    }

    private Vector2? TrainPoint(TrainMapItem train)
    {
        if (train.Position is TrainPosition.Node node && _nodes.TryGetValue(node.Id, out var at))
            return MapToScreen(at.Position) + new Vector2(25, -22);
        if (train.Position is TrainPosition.Edge moving && _edges.TryGetValue(moving.Id, out var edge) && EdgePoints(edge) is { } ends)
            return ends.Start.Lerp(ends.End, edge.LengthMeters > 0
                ? (float)Math.Clamp(moving.DistanceMeters / edge.LengthMeters, 0, 1) : 0);
        return null;
    }

    private void DrawTrain(TrainMapItem train)
    {
        if (TrainPoint(train) is not { } point) return;
        var color = train.Activity switch
        {
            TrainActivity.Running => new Color("#2f9e5c"),
            TrainActivity.Servicing => new Color("#2aa3b3"),
            TrainActivity.Stopped or TrainActivity.Waiting or TrainActivity.Retreating => new Color("#d8962a"),
            _ => new Color("#c9423a")
        };
        DrawCapsule(point, Ink, 7);
        DrawCapsule(point, color, 6);
        var rotation = 0f;
        if (train.Activity == TrainActivity.Running)
        {
            var direction = Vector2.Right;
            if (train.Position is TrainPosition.Edge moving && _edges.TryGetValue(moving.Id, out var edge) && EdgePoints(edge) is { } ends)
                direction = (ends.End - ends.Start).Normalized() * (moving.Direction == TravelDirection.Forward ? 1 : -1);
            rotation = direction.Angle();
        }
        var activity = train.Activity == TrainActivity.Stopped ? TrainActivity.Waiting : train.Activity;
        DrawSetTransform(point, rotation);
        DrawTextureRect(_trainSymbols[activity], new Rect2(-7, -7, 14, 14), false);
        DrawSetTransform(Vector2.Zero);
    }

    private void DrawCapsule(Vector2 point, Color color, float radius)
    {
        DrawRect(new Rect2(point - new Vector2(7, radius), new Vector2(14, radius * 2)), color);
        DrawCircle(point + new Vector2(-7, 0), radius, color);
        DrawCircle(point + new Vector2(7, 0), radius, color);
    }

    private ImageTexture? FindSymbol(AssetRevision revision)
    {
        if (_symbols.TryGetValue(revision, out var cached)) return cached;
        ImageTexture? texture = null;
        if (_content!.FindAsset(revision) is ContentLookup<ContentAsset>.Found asset && asset.Value.Metadata is AssetMetadata.Png)
        {
            using var image = new Image();
            if (image.LoadPngFromBuffer(asset.Value.Bytes.ToArray()) == Error.Ok)
                texture = ImageTexture.CreateFromImage(image);
        }
        _symbols.Add(revision, texture);
        if (texture is null) Fail($"地图符号资产 {revision.Value} 缺失或无法解码。");
        return texture;
    }

    private void Fail(string message)
    {
        if (_failures.Add(message)) DisplayFailed?.Invoke(message);
    }

    private void ReleaseSymbols()
    {
        foreach (var texture in _symbols.Values) texture?.Dispose();
        _symbols.Clear();
        _failures.Clear();
    }

    public override void _ExitTree()
    {
        Resized -= OnResized;
        _dragging = false;
        ReleaseSymbols();
    }

    private void BuildRegions()
    {
        _regions.Clear();
        if (_world is null) return;
        foreach (var node in _world.Nodes)
        {
            List<Vector2> polygon = [Vector2.Zero, new((float)_world.WidthMeters, 0),
                new((float)_world.WidthMeters, (float)_world.HeightMeters), new(0, (float)_world.HeightMeters)];
            var origin = new Vector2((float)node.Position.X, (float)node.Position.Y);
            foreach (var other in _world.Nodes)
            {
                if (node.Id == other.Id) continue;
                var neighbor = new Vector2((float)other.Position.X, (float)other.Position.Y);
                var normal = neighbor - origin;
                var midpoint = (neighbor + origin) / 2;
                List<Vector2> clipped = [];
                for (var i = 0; i < polygon.Count; i++)
                {
                    var a = polygon[i];
                    var b = polygon[(i + 1) % polygon.Count];
                    var da = (a - midpoint).Dot(normal);
                    var db = (b - midpoint).Dot(normal);
                    if (da <= 0) clipped.Add(a);
                    if ((da < 0 && db > 0) || (da > 0 && db < 0)) clipped.Add(a.Lerp(b, da / (da - db)));
                }
                polygon = clipped;
                if (polygon.Count < 3) break;
            }
            if (polygon.Count >= 3) _regions.Add((polygon.ToArray(), TerrainColor(node.Terrain.TypeId)));
        }
    }

    private static Color TerrainColor(TerrainTypeId id) => new(id.Value switch
    {
        "core:plains" => "#bccf93",
        "core:hills" => "#cbb37e",
        "core:forest" => "#7aa06a",
        "core:desert" => "#e5cf95",
        "core:mountains" => "#a69d92",
        _ => "#b7c4a6"
    });
}
