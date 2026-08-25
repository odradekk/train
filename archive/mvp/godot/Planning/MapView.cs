using Godot;
using TrainMvp.App;
using TrainMvp.Domain;

namespace TrainMvp.Planning;

public partial class MapView : Control
{
    private GameSession _session = null!;
    private readonly Dictionary<string, PanelContainer> _cards = new(StringComparer.Ordinal);
    private GridContainer _grid = null!;

    public event Action<string>? StationSelected;

    public void Bind(GameSession session)
    {
        _session = session;
    }

    public override void _Ready()
    {
        Name = "MapView";
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(520, 300);

        var box = new VBoxContainer();
        box.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        box.AddThemeConstantOverride("separation", 8);
        AddChild(box);
        box.AddChild(UiTheme.MakeLabel("铁路地图", 18, true, wrap: false));

        _grid = new GridContainer
        {
            Columns = 3,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _grid.AddThemeConstantOverride("h_separation", 10);
        _grid.AddThemeConstantOverride("v_separation", 10);
        box.AddChild(_grid);

        foreach (var station in _session.Scenario.Stations.Values.OrderBy(item => item.GridY).ThenBy(item => item.GridX))
        {
            var card = CreateCard(station);
            _cards[station.Id] = card;
            _grid.AddChild(card);
        }

        QueueRedraw();
    }

    public void Refresh()
    {
        foreach (var (id, card) in _cards)
        {
            ApplyCardStyle(card, _session.Scenario.Stations[id]);
        }

        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_session is null || _cards.Count == 0)
        {
            return;
        }

        foreach (var edge in _session.Scenario.Edges)
        {
            if (!_cards.TryGetValue(edge.A, out var a) || !_cards.TryGetValue(edge.B, out var b))
            {
                continue;
            }

            var from = GetGlobalTransform().AffineInverse() * a.GetGlobalRect().GetCenter();
            var to = GetGlobalTransform().AffineInverse() * b.GetGlobalRect().GetCenter();
            DrawLine(from, to, new Color(0.45f, 0.38f, 0.30f, 0.85f), 3.5f);
        }
    }

    private PanelContainer CreateCard(StationSpec station)
    {
        var card = new PanelContainer
        {
            Name = $"StationCard_{station.Id}",
            CustomMinimumSize = new Vector2(156, 86),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop
        };
        var box = new VBoxContainer();
        card.AddChild(box);
        var name = UiTheme.MakeLabel(station.Name, 18, true, wrap: false);
        name.Name = $"StationName_{station.Id}";
        box.AddChild(name);
        var role = UiTheme.MakeLabel(station.Role, 12, wrap: true);
        role.Name = $"StationRole_{station.Id}";
        box.AddChild(role);
        card.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                StationSelected?.Invoke(station.Id);
            }
        };
        ApplyCardStyle(card, station);
        return card;
    }

    private void ApplyCardStyle(PanelContainer card, StationSpec station)
    {
        var isStop = _session.Plan.Stops.Contains(station.Id);
        var selected = _session.SelectedStationId == station.Id;
        var style = new StyleBoxFlat
        {
            BgColor = selected
                ? new Color(0.93f, 0.78f, 0.48f)
                : isStop
                    ? new Color(0.965f, 0.831f, 0.647f)
                    : new Color(0.937f, 0.902f, 0.847f),
            BorderColor = selected ? UiTheme.Accent : new Color(0.72f, 0.64f, 0.52f),
            BorderWidthBottom = selected ? 3 : 1,
            BorderWidthLeft = selected ? 3 : 1,
            BorderWidthRight = selected ? 3 : 1,
            BorderWidthTop = selected ? 3 : 1,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        card.AddThemeStyleboxOverride("panel", style);
        if (card.GetChildCount() > 0 && card.GetChild(0) is VBoxContainer box && box.GetChildCount() > 1 && box.GetChild(1) is Label role)
        {
            role.Text = isStop ? $"交易停靠站 · {station.Role}" : station.Role;
        }
    }
}
