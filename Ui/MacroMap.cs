using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class MacroMap : Control
{
    private static readonly Color Ink = new("#2a2a2a");
    private IContentView? _content;
    private WorldSummary? _world;
    private readonly Dictionary<NodeId, NodeMapItem> _nodes = [];
    private readonly Dictionary<EdgeId, EdgeMapItem> _edges = [];
    private readonly Dictionary<AssetRevision, ImageTexture?> _symbols = [];
    private readonly HashSet<string> _failures = [];
    private readonly List<(Vector2[] Points, Color Color)> _regions = [];
    private bool _dragging;
    private bool _fitPending;

    internal ICoreReadView? View { get; private set; }
    internal Vector2 Offset { get; private set; }
    internal float Zoom { get; private set; } = 1;
    internal event Action<WorldObjectId>? ObjectClicked;
    internal event Action<string>? DisplayFailed;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        Resized += OnResized;
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
            _dragging = false;
            _fitPending = true;
            FitCamera();
        }
        QueueRedraw();
    }

    private void OnResized()
    {
        FitCamera();
        QueueRedraw();
    }

    private void FitCamera()
    {
        if (!_fitPending || _world is null || Size.X <= 0 || Size.Y <= 0) return;
        Zoom = Math.Min(Math.Max(1, Size.X - 120) / (float)Math.Max(1, _world.WidthMeters),
            Math.Max(1, Size.Y - 120) / (float)Math.Max(1, _world.HeightMeters));
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
                var next = Math.Clamp(Zoom * (button.ButtonIndex == MouseButton.WheelUp ? 1.2f : 1 / 1.2f), .01f, 8);
                Offset = button.Position - (button.Position - Offset) * (next / Zoom);
                Zoom = next;
                QueueRedraw();
            }
            else if (button.Pressed && button.ButtonIndex == MouseButton.Left && HitTest(button.Position) is { } hit)
                ObjectClicked?.Invoke(hit);
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
            if (MapToScreen(node.Position).DistanceTo(point) <= (node.Occupant is NodeOccupant.Empty ? 6 : 16))
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
        foreach (var train in _world.Trains) DrawTrain(train);
    }

    private (Vector2 Start, Vector2 End)? EdgePoints(EdgeMapItem edge) =>
        _nodes.TryGetValue(edge.Start, out var start) && _nodes.TryGetValue(edge.End, out var end)
            ? (MapToScreen(start.Position), MapToScreen(end.Position)) : null;

    private void DrawEdge(EdgeMapItem edge)
    {
        if (EdgePoints(edge) is not { } ends) return;
        if (edge.Tracks == 0)
        {
            DrawDashedLine(ends.Start, ends.End, new Color(Ink, .5f), 1, 4);
            return;
        }
        var normal = (ends.End - ends.Start).Normalized().Orthogonal();
        DrawLine(ends.Start, ends.End, Colors.White, edge.Tracks == 1 ? 7 : 5 * (edge.Tracks - 1) + 6, true);
        for (var track = 0; track < edge.Tracks; track++)
        {
            var offset = normal * ((track - (edge.Tracks - 1) / 2f) * 5);
            DrawLine(ends.Start + offset, ends.End + offset, Ink, 2, true);
        }
    }

    private void DrawNode(NodeMapItem node)
    {
        var point = MapToScreen(node.Position);
        if (node.Occupant is NodeOccupant.Empty)
        {
            DrawCircle(point, 6, Colors.White);
            DrawArc(point, 6, 0, Mathf.Tau, 32, Ink, 2, true);
            DrawMapText($"节点 {node.Id.Value}", point + new Vector2(0, 27), new Color("#1e1e1e"));
            return;
        }
        var color = node.Occupant is NodeOccupant.Settlement settlement ? settlement.Relation switch
        {
            SettlementRelation.PlayerOwned => new Color("#2b77d0"),
            SettlementRelation.Hostile => new Color("#c9423a"),
            _ => new Color("#7f8893")
        } : new Color("#7f8893");
        DrawCircle(point, 16, Colors.White);
        DrawArc(point, 16, 0, Mathf.Tau, 48, color, 3, true);
        if (node.Occupant is NodeOccupant.Settlement { Relation: SettlementRelation.Hostile })
            for (var i = 0; i < 12; i++) DrawArc(point, 22, i * Mathf.Tau / 12, (i + .55f) * Mathf.Tau / 12, 4, color, 2, true);
        var name = $"节点 {node.Id.Value}";
        ImageTexture? symbol = null;
        if (node.Occupant is NodeOccupant.Settlement occupied && _content is not null)
        {
            if (_content.FindSettlementLayout(occupied.Id) is ContentLookup<SettlementLayoutProjection>.Found layout)
            {
                name = layout.Value.Display.Name;
                var template = _content.Core.SettlementTemplates.FirstOrDefault(t => t.Id == layout.Value.SourceTemplate);
                if (template is not null && _content.FindDefinitionDisplay(new CoreDefinitionReference.SettlementType(template.Type)) is ContentLookup<ContentDisplay>.Found display && display.Value.MapSymbol is { } revision)
                    symbol = FindSymbol(revision);
            }
        }
        if (symbol is not null) DrawTextureRect(symbol, new Rect2(point - new Vector2(12, 12), new Vector2(24, 24)), false, color);
        else
        {
            DrawMapText("?", point + new Vector2(0, 6), color);
            Fail($"节点 {node.Id.Value} 缺少可显示的地图符号。");
        }
        DrawMapText(name, point + new Vector2(0, 37), new Color("#1e1e1e"));
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
            TrainActivity.Stopped or TrainActivity.Waiting => new Color("#d8962a"),
            _ => new Color("#c9423a")
        };
        DrawCapsule(point, Ink, 7);
        DrawCapsule(point, color, 6);
        if (train.Activity == TrainActivity.Running)
        {
            var direction = Vector2.Right;
            if (train.Position is TrainPosition.Edge moving && _edges.TryGetValue(moving.Id, out var edge) && EdgePoints(edge) is { } ends)
                direction = (ends.End - ends.Start).Normalized() * (moving.Direction == TravelDirection.Forward ? 1 : -1);
            var normal = direction.Orthogonal();
            DrawColoredPolygon([point + direction * 5, point - direction * 4 + normal * 4, point - direction * 4 - normal * 4], Colors.White);
        }
        else if (train.Activity == TrainActivity.Servicing) DrawRect(new Rect2(point - new Vector2(5, 3), new Vector2(10, 6)), Colors.White, false, 2);
        else if (train.Activity is TrainActivity.Stopped or TrainActivity.Waiting)
        {
            DrawLine(point + new Vector2(-3, -4), point + new Vector2(-3, 4), Colors.White, 2);
            DrawLine(point + new Vector2(3, -4), point + new Vector2(3, 4), Colors.White, 2);
        }
        else
        {
            DrawLine(point + new Vector2(0, -4), point + new Vector2(0, 1), Colors.White, 2);
            DrawCircle(point + new Vector2(0, 4), 1, Colors.White);
        }
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
