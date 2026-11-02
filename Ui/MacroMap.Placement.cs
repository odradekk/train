using Godot;

namespace Train.Ui;

public partial class MacroMap
{
    internal Rect2? OverlayArea { get; set; }

    internal void PositionLayerPanel(float top)
    {
        _layersTop = top;
        var area = OverlayArea ?? new Rect2(Vector2.Zero, Size);
        _layers.Position = new(Math.Max(area.Position.X, area.End.X - _layers.Size.X - 12), top);
    }
}
