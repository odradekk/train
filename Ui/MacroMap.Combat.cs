using Godot;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class MacroMap
{
    private static readonly Color CombatFill = new(0.0941176f, 0.1098039f, 0.1294118f);
    private static readonly Color CombatBorder = new("c9423a");
    private static readonly Texture2D CombatSymbol = GD.Load<Texture2D>("res://Ui/Symbols/train-combat.png");

    internal event Action<CombatId>? CombatClicked;

    private void DrawCombats()
    {
        if (_world is null) return;
        foreach (var combat in _world.Combats)
        {
            if (CombatPoint(combat) is not { } point) continue;
            DrawCircle(point, 14, CombatFill);
            DrawArc(point, 14, 0, Mathf.Tau, 32, CombatBorder, 3, true);
            DrawTextureRect(CombatSymbol, new Rect2(point - new Vector2(10, 10), new Vector2(20, 20)), false, UiStyle.Foreground);
        }
    }

    private CombatId? CombatAt(Vector2 point)
    {
        if (_world is null) return null;
        foreach (var combat in _world.Combats.Reverse())
            if (CombatPoint(combat) is { } center && center.DistanceTo(point) <= 18)
                return combat.Id;
        return null;
    }

    private Vector2? CombatPoint(CombatMapItem combat) => combat.Location switch
    {
        WorldLocation.Node node when _nodes.TryGetValue(node.Id, out var map) => MapToScreen(map.Position),
        WorldLocation.Edge edge when _edges.TryGetValue(edge.Id, out var map) && EdgePoints(map) is { } ends =>
            ends.Start.Lerp(ends.End, (float)Math.Clamp(map.LengthMeters <= 0 ? 0 : edge.DistanceMeters / map.LengthMeters, 0, 1)),
        _ => null
    };
}
