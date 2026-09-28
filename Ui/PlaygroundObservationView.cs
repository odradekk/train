using Godot;
using Train.Domain.Composites;
using Train.Domain.Playground;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Presentation;

namespace Train;

/// <summary>What one world click resolved to, with both candidate hits so automation can compare them.</summary>
internal sealed record PlaygroundPick(
    Vector2 Screen,
    ObservedObject? Object,
    PlaygroundObservationStatus? Status,
    PlaygroundVehicleHit? VehicleHit,
    WorldVoxelCoord? WorldCell,
    double WorldDistance);

/// <summary>
/// Godot adapter for <see cref="PlaygroundObserver"/>: drives the camera from its pose, draws the selection
/// highlight, and hosts the object list / inspection panel and observation input. It never touches the session.
/// </summary>
public partial class PlaygroundObservationView : Node3D
{
    public const double LookSensitivity = 0.005;
    public const double ZoomStep = 2;
    public const double FlySpeed = 12;
    public const double FastMultiplier = 4;
    public const float PickDistance = 500;

    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private VoxelDevelopmentSession _session = null!;
    private Camera3D _camera = null!;
    private VoxelWorldCollisionPresenter _colliders = null!;
    private Func<bool> _consoleOpen = null!;
    private TrainDisplaySample? _sample;
    private Label _mode = null!;
    private Label _action = null!;
    private Label _inspection = null!;
    private Label _notice = null!;
    private ItemList _list = null!;
    private IReadOnlyList<PlaygroundListEntry> _entries = Array.Empty<PlaygroundListEntry>();
    private MeshInstance3D _highlight = null!;
    private int _shownNotices;
    private bool _dragging;

    public PlaygroundObserver Observer { get; private set; } = null!;
    internal PlaygroundPick? LastPick { get; private set; }
    internal bool HighlightVisible => _highlight.Visible;
    internal string ModeText => _mode.Text;
    internal string InspectionText => _inspection.Text;
    internal string NoticeText => _notice.Text;
    internal IReadOnlyList<PlaygroundListEntry> Entries => _entries;

    public void Initialize(
        VoxelDevelopmentSession session,
        Camera3D camera,
        VoxelWorldCollisionPresenter colliders,
        Func<bool> consoleOpen,
        ObservationCameraPose initial)
    {
        _session = session;
        _camera = camera;
        _colliders = colliders;
        _consoleOpen = consoleOpen;
        Observer = new PlaygroundObserver(session, initial.Position, initial.Look);
        CreatePanel();
        CreateHighlight();
        ApplyCamera(Observer.Pose);
    }

    /// <summary>Advances the camera by one display frame from the same sample the vehicle bodies were just drawn with.</summary>
    public void Present(double delta, TrainDisplaySample sample)
    {
        _sample = sample;
        if (delta > 0)
            FlyFromKeys(delta);
        ApplyCamera(Observer.Present(delta, sample));
        UpdateHighlight(sample);
        UpdatePanel();
    }

    internal void Press(string button) => _buttons[button].EmitSignal(BaseButton.SignalName.Pressed);

    internal void SelectListIndex(int index)
    {
        _list.Select(index);
        _list.EmitSignal(ItemList.SignalName.ItemSelected, index);
    }

    /// <summary>Resolves a screen point to the nearest vehicle part or published world-voxel collider and selects it.</summary>
    internal PlaygroundPick Pick(Vector2 screen)
    {
        var origin = _camera.ProjectRayOrigin(screen);
        var direction = _camera.ProjectRayNormal(screen);
        var vehicle = PlaygroundInspector.RaycastVehicles(_session, _sample ?? _session.QueryDisplayPoses(), From(origin), From(direction), PickDistance);
        var world = _colliders.QueryPublishedRay(GetWorld3D(), origin, origin + direction * PickDistance);
        var worldDistance = world is { } hit ? origin.DistanceTo(hit.Position) : double.PositiveInfinity;
        ObservedObject? item = vehicle is not null && vehicle.Distance <= worldDistance
            ? ObservedObject.Vehicle(vehicle.VehicleId)
            : world is { } cell ? ObservedObject.WorldCell(cell.Cell) : null;
        var status = item is { } picked ? Observer.Select(picked) : (PlaygroundObservationStatus?)null;
        LastPick = new PlaygroundPick(screen, item, status, vehicle, world?.Cell, worldDistance);
        _action.Text = item is null ? "点选：未命中对象，选择不变" : $"点选：{item.Value.DescribeZh()} → {status}";
        UpdatePanel();
        return LastPick;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Observer is null || _consoleOpen())
            return;
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click:
                Pick(click.Position);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } right:
                _dragging = right.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                Observer.Zoom(ZoomStep);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                Observer.Zoom(-ZoomStep);
                break;
            case InputEventMouseMotion motion when _dragging:
                Observer.Look(motion.Relative.X * LookSensitivity, -motion.Relative.Y * LookSensitivity);
                break;
            case InputEventKey { Pressed: true, Echo: false } key when key.Keycode == Key.F:
                Press("focus");
                break;
            case InputEventKey { Pressed: true, Echo: false } key when key.Keycode == Key.V:
                Press("follow");
                break;
            case InputEventKey { Pressed: true, Echo: false } key when key.Keycode == Key.C:
                Press("free");
                break;
            default:
                return;
        }

        GetViewport().SetInputAsHandled();
    }

    private void FlyFromKeys(double delta)
    {
        if (_consoleOpen() || GetViewport().GuiGetFocusOwner() is LineEdit)
            return;
        double Axis(Key positive, Key negative) =>
            (Input.IsPhysicalKeyPressed(positive) ? 1 : 0) - (Input.IsPhysicalKeyPressed(negative) ? 1 : 0);
        var right = Axis(Key.D, Key.A);
        var up = Axis(Key.E, Key.Q);
        var forward = Axis(Key.W, Key.S);
        if (right == 0 && up == 0 && forward == 0)
            return;
        var speed = FlySpeed * delta * (Input.IsPhysicalKeyPressed(Key.Shift) ? FastMultiplier : 1);
        Observer.Fly(right * speed, up * speed, forward * speed);
    }

    private void ApplyCamera(ObservationCameraPose pose)
    {
        var position = To(pose.Position);
        var look = To(pose.Look);
        _camera.Position = position;
        if (_camera.IsInsideTree() && (look - position).LengthSquared() > 1e-8f)
            _camera.LookAt(look, Vector3.Up);
    }

    private void CreatePanel()
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        var panel = new PanelContainer
        {
            AnchorLeft = 1,
            AnchorRight = 1,
            OffsetLeft = -372,
            OffsetRight = -12,
            OffsetTop = 12,
            CustomMinimumSize = new Vector2(360, 0)
        };
        layer.AddChild(panel);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);

        box.AddChild(new Label { Text = "观察" });
        _mode = new Label();
        box.AddChild(_mode);
        var controls = new HBoxContainer();
        controls.AddThemeConstantOverride("separation", 6);
        box.AddChild(controls);
        AddButton(controls, "focus", "聚焦选中", () => Report("聚焦", Observer.FocusSelection(_sample)));
        AddButton(controls, "follow", "跟随选中", () => Report("跟随", Observer.FollowSelection(_sample)));
        AddButton(controls, "free", "自由观察", () => Report("自由观察", Observer.Free()));
        AddButton(controls, "clear", "清除选择", () => Report("清除选择", Observer.ClearSelection()));
        _action = new Label { Text = "左键点选 · 右键拖动转向 · 滚轮推拉 · WASD/QE 飞行 · F 聚焦 · V 跟随 · C 自由" };
        _action.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_action);

        _list = new ItemList
        {
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 150),
            SelectMode = ItemList.SelectModeEnum.Single
        };
        _list.ItemSelected += index =>
        {
            if (index >= 0 && index < _entries.Count)
                Report(_entries[(int)index].Object.DescribeZh(), Observer.Select(_entries[(int)index].Object));
        };
        box.AddChild(_list);

        _inspection = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_inspection);
        _notice = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _notice.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.3f));
        box.AddChild(_notice);
    }

    private void AddButton(Container parent, string id, string text, Action action)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.Pressed += action;
        parent.AddChild(button);
        _buttons[id] = button;
    }

    private void Report(string action, PlaygroundObservationStatus status)
    {
        _action.Text = $"{action} → {status}";
        UpdatePanel();
    }

    private void UpdatePanel()
    {
        var mode = Observer.Mode switch
        {
            PlaygroundCameraMode.Focus => "聚焦 " + Observer.Target!.Value.DescribeZh(),
            PlaygroundCameraMode.Follow => "跟随 " + Observer.Target!.Value.DescribeZh(),
            _ => "自由观察"
        };
        SetText(_mode, $"镜头：{mode}{(Observer.Transitioning ? "（过渡中）" : "")}");

        var entries = PlaygroundInspector.List(_session);
        if (!entries.Select(entry => entry.Label).SequenceEqual(_entries.Select(entry => entry.Label)))
        {
            _entries = entries;
            _list.Clear();
            foreach (var entry in entries)
                _list.AddItem(entry.Label);
        }

        var selectedIndex = -1;
        for (var index = 0; index < _entries.Count; index++)
        {
            if (Observer.Selection == _entries[index].Object)
                selectedIndex = index;
        }

        if (selectedIndex < 0)
            _list.DeselectAll();
        else if (!_list.IsSelected(selectedIndex))
            _list.Select(selectedIndex);

        var inspection = Observer.Selection is { } selected && PlaygroundInspector.Inspect(_session, selected) is { } facts
            ? "选中：" + selected.DescribeZh() + "\n" + string.Join('\n', facts.LinesZh())
            : "选中：无";
        SetText(_inspection, inspection);

        if (Observer.Notices.Count != _shownNotices)
        {
            _shownNotices = Observer.Notices.Count;
            SetText(_notice, Observer.LastNotice!.DescribeZh());
        }
    }

    private static void SetText(Label label, string text)
    {
        if (label.Text != text)
            label.Text = text;
    }

    private void CreateHighlight()
    {
        _highlight = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = Vector3.One },
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1f, 0.85f, 0.2f, 0.28f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            }
        };
        AddChild(_highlight);
    }

    private void UpdateHighlight(TrainDisplaySample sample)
    {
        if (Observer.Selection is not { } selected || !TryHighlightBox(selected, sample, out var transform))
        {
            _highlight.Visible = false;
            return;
        }

        _highlight.Transform = transform;
        _highlight.Visible = true;
    }

    private bool TryHighlightBox(ObservedObject selected, TrainDisplaySample sample, out Transform3D transform)
    {
        transform = Transform3D.Identity;
        switch (selected.Kind)
        {
            case ObservedObjectKind.Vehicle:
                var vehicle = _session.Vehicles.FirstOrDefault(item => item.Id == selected.Id);
                if (vehicle is null || vehicle.Parts.Count == 0 || PlaygroundObserver.VehicleFrame(_session, sample, selected.Id) is not { } frame)
                    return false;
                var min = new Vector3(vehicle.Parts.Min(part => part.Cell.X), vehicle.Parts.Min(part => part.Cell.Y), vehicle.Parts.Min(part => part.Cell.Z));
                var max = new Vector3(vehicle.Parts.Max(part => part.Cell.X), vehicle.Parts.Max(part => part.Cell.Y), vehicle.Parts.Max(part => part.Cell.Z)) + Vector3.One;
                var body = ToGodot(frame.Transform);
                transform = body * new Transform3D(Basis.Identity.Scaled(max - min + new Vector3(0.2f, 0.2f, 0.2f)), (min + max) * 0.5f);
                return true;
            case ObservedObjectKind.RailSegment:
                var cells = _session.Rails.OccupancyCells
                    .Where(cell => cell.Supports.Any(support => support.SegmentId == selected.Id))
                    .Select(cell => cell.Cell)
                    .ToArray();
                if (cells.Length == 0)
                    return false;
                var low = new Vector3(cells.Min(cell => cell.X), cells.Min(cell => cell.Y), cells.Min(cell => cell.Z));
                var high = new Vector3(cells.Max(cell => cell.X), cells.Max(cell => cell.Y), cells.Max(cell => cell.Z)) + Vector3.One;
                transform = new Transform3D(Basis.Identity.Scaled(high - low + new Vector3(0.2f, 0.2f, 0.2f)), (low + high) * 0.5f);
                return true;
            default:
                transform = new Transform3D(
                    Basis.Identity.Scaled(new Vector3(1.1f, 1.1f, 1.1f)),
                    new Vector3(selected.Cell.X + 0.5f, selected.Cell.Y + 0.5f, selected.Cell.Z + 0.5f));
                return true;
        }
    }

    internal static Transform3D ToGodot(CompositeTransform transform) =>
        new(
            new Basis(
                new Vector3((float)transform.RightX, (float)transform.RightY, (float)transform.RightZ),
                new Vector3((float)transform.UpX, (float)transform.UpY, (float)transform.UpZ),
                new Vector3((float)transform.ForwardX, (float)transform.ForwardY, (float)transform.ForwardZ)),
            new Vector3((float)transform.OriginX, (float)transform.OriginY, (float)transform.OriginZ));

    internal static Vector3 To(ObservationVector value) => new((float)value.X, (float)value.Y, (float)value.Z);

    internal static ObservationVector From(Vector3 value) => new(value.X, value.Y, value.Z);
}
