using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class SaveMapPreview : Control
{
    private WorldSummary? _world;
    internal WorldSummary? World
    {
        get => _world;
        set { _world = value; QueueRedraw(); }
    }

    public override void _Ready() => Resized += QueueRedraw;

    public override void _Draw()
    {
        DrawRect(new(Vector2.Zero, Size), new("303b35"));
        if (_world is not { } world) return;
        var extent = new Vector2((float)world.WidthMeters, (float)world.HeightMeters);
        var scale = Math.Max(0, Math.Min((Size.X - 24) / extent.X, (Size.Y - 24) / extent.Y));
        var origin = (Size - extent * scale) / 2;
        Vector2 Point(MapPoint point) => origin + new Vector2((float)point.X, (float)point.Y) * scale;
        var nodes = world.Nodes.ToDictionary(node => node.Id);
        foreach (var edge in world.Edges)
            DrawLine(Point(nodes[edge.Start].Position), Point(nodes[edge.End].Position), UiStyle.Info, 2, true);
        foreach (var node in world.Nodes)
            DrawCircle(Point(node.Position), 4, node.Occupant is NodeOccupant.Settlement settlement
                ? UiStyle.RelationColor(settlement.Relation) : UiStyle.Info);
    }
}
