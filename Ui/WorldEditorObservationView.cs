using Godot;
using Train.Domain.Composites;
using Train.Domain.WorldEditor;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Presentation;

namespace Train;

/// <summary>What one world click resolved to, with both candidate hits so automation can compare them.</summary>
internal sealed record WorldEditorPick(
    Vector2 Screen,
    ObservedObject? Object,
    WorldEditorObservationStatus? Status,
    WorldEditorVehicleHit? VehicleHit,
    WorldVoxelCoord? WorldCell,
    double WorldDistance,
    Vector3? WorldPosition,
    Vector3? WorldNormal,
    WorldVoxelCoord? AdjacentCell);

/// <summary>
/// Godot adapter for <see cref="WorldEditorObserver"/>: drives the camera from its pose, draws the selection
/// highlight, and hosts the object list / inspection panel and observation input. It never touches the session.
/// </summary>
public partial class WorldEditorObservationView : Node3D
{
    public const double LookSensitivity = 0.005;
    public const double ZoomStep = 2;
    public const double FlySpeed = 12;
    public const double FastMultiplier = 4;
    public const float PickDistance = 500;

    private static readonly IReadOnlyDictionary<Key, ObservationVector> FlyDirections = new Dictionary<Key, ObservationVector>
    {
        [Key.D] = new(1, 0, 0), [Key.A] = new(-1, 0, 0),
        [Key.E] = new(0, 1, 0), [Key.Q] = new(0, -1, 0),
        [Key.W] = new(0, 0, 1), [Key.S] = new(0, 0, -1)
    };

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
    private Label _coordinates = null!;
    private ItemList _list = null!;
    private IReadOnlyList<WorldEditorListEntry> _entries = Array.Empty<WorldEditorListEntry>();
    private MeshInstance3D _highlight = null!;
    private Control _panel = null!;
    private Control _worldInput = null!;
    private readonly HashSet<Key> _flyKeys = new();
    private int _shownNotices;
    private bool _dragging;
    private Vector2? _pointerScreen;

    public WorldEditorObserver Observer { get; private set; } = null!;
    internal Func<Vector2, bool, bool>? CellPointer { get; set; }
    internal Action<Action>? DraftGuard { get; set; }
    internal WorldEditorPick? LastPick { get; private set; }
    internal WorldEditorVehicleHit? HoverVehicle { get; private set; }
    internal bool PointerCoordinatesVisible => _coordinates.IsVisibleInTree();
    internal bool HighlightVisible => _highlight.Visible;
    internal string ModeText => _mode.Text;
    internal string InspectionText => _inspection.Text;
    internal string NoticeText => _notice.Text;
    internal IReadOnlyList<WorldEditorListEntry> Entries => _entries;
    internal Rect2 PanelRect => _panel.GetGlobalRect();
    internal Rect2 WorldRect => _worldInput.GetGlobalRect();
    internal bool WorldHasInput => GetViewport().GuiGetFocusOwner() == _worldInput && !_consoleOpen();

    public void Initialize(
        VoxelDevelopmentSession session,
        Camera3D camera,
        VoxelWorldCollisionPresenter colliders,
        Func<bool> consoleOpen,
        ObservationCameraPose initial,
        Control worldInput,
        VBoxContainer inspector)
    {
        _session = session;
        _camera = camera;
        _colliders = colliders;
        _consoleOpen = consoleOpen;
        _worldInput = worldInput;
        _worldInput.GuiInput += HandleWorldInput;
        _worldInput.FocusExited += EndInput;
        _worldInput.MouseExited += () => { _pointerScreen = null; HoverVehicle = null; _coordinates.Text = ""; };
        Observer = new WorldEditorObserver(session, initial.Position, initial.Look);
        CreatePanel(inspector);
        CreateHighlight();
        ApplyCamera(Observer.Pose);
    }

    public override void _ExitTree() => Observer?.Dispose();

    /// <summary>Advances the camera by one display frame from the same sample the vehicle bodies were just drawn with.</summary>
    public void Present(double delta, TrainDisplaySample sample)
    {
        _sample = sample;
        if (_consoleOpen())
            _worldInput.ReleaseFocus();
        if (!Input.IsMouseButtonPressed(MouseButton.Right))
            _dragging = false;
        if (!WorldHasInput)
            EndInput();
        if (delta > 0)
            FlyFromKeys(delta);
        ApplyCamera(Observer.Present(delta, sample));
        if (_pointerScreen is { } screen && !_consoleOpen())
            ShowPointerCoordinates(screen);
        UpdateHighlight(sample);
        UpdatePanel();
    }

    internal void Press(string button)
    {
        if (_buttons[button].Disabled)
            throw new InvalidOperationException($"Observation action {button} is disabled.");
        _buttons[button].EmitSignal(BaseButton.SignalName.Pressed);
    }

    internal void SelectListIndex(int index)
    {
        _list.Select(index);
        _list.EmitSignal(ItemList.SignalName.ItemSelected, index);
    }

    /// <summary>Resolves a screen point to the nearest vehicle part or published world-voxel collider and selects it.</summary>
    internal WorldEditorPick Pick(Vector2 screen)
    {
        var raw = QueryPick(screen);
        var vehicle = raw.VehicleHit;
        var item = raw.Object;
        if (Observer.PartVehicleId is { } owner)
        {
            item = item == ObservedObject.Vehicle(owner) && vehicle is not null
                ? ObservedObject.VehiclePart(owner, vehicle.PartId)
                : null;
        }
        var status = item is { } picked ? Observer.Select(picked) : (WorldEditorObservationStatus?)null;
        if (status == WorldEditorObservationStatus.DraftRequired && item is { } blocked)
            DraftGuard?.Invoke(() => Observer.Select(blocked));
        LastPick = raw with { Object = item, Status = status };
        _action.Text = item is null ? "点选：未命中对象，选择不变" : $"点选：{item.Value.DescribeZh()} → {status}";
        UpdatePanel();
        return LastPick;
    }

    /// <summary>Raw nearest hits without selection, camera changes or tool snapping.</summary>
    internal WorldEditorPick QueryPick(Vector2 screen)
    {
        var origin = _camera.ProjectRayOrigin(screen);
        var direction = _camera.ProjectRayNormal(screen);
        var vehicle = WorldEditorInspector.RaycastVehicles(_session, _sample ?? _session.QueryDisplayPoses(), From(origin), From(direction), PickDistance);
        var world = _colliders.QueryPublishedRay(GetWorld3D(), origin, origin + direction * PickDistance);
        var worldDistance = world is { } hit ? origin.DistanceTo(hit.Position) : double.PositiveInfinity;
        ObservedObject? item = vehicle is not null && vehicle.Distance <= worldDistance
            ? ObservedObject.Vehicle(vehicle.VehicleId)
            : world is { } cell ? WorldEditorInspector.WorldTarget(_session, cell.Cell) : null;
        return new WorldEditorPick(screen, item, null, vehicle, world?.Cell, worldDistance,
            world?.Position, world?.Normal, world?.AdjacentCell);
    }

    private void ShowPointerCoordinates(Vector2 screen)
    {
        var pick = QueryPick(screen);
        HoverVehicle = pick.VehicleHit is { } hit && hit.Distance <= pick.WorldDistance ? hit : null;
        _coordinates.Text = HoverVehicle is { } vehicle
            ? $"光标 · 车辆 {vehicle.VehicleId.Value}\n世界坐标 {Coordinates(vehicle.WorldPosition)}\n车辆局部坐标 {Coordinates(vehicle.LocalPosition)}"
            : pick.WorldPosition is { } point ? $"光标 · 世界坐标 {Coordinates(From(point))}" : "光标：未命中表面";
    }

    private static string Coordinates(ObservationVector point) => FormattableString.Invariant($"({point.X:0.###}, {point.Y:0.###}, {point.Z:0.###})");

    public override void _Notification(int what)
    {
        if (what is (int)NotificationWMWindowFocusOut or (int)NotificationApplicationFocusOut)
            EndInput();
    }

    private void EndInput()
    {
        _dragging = false;
        _flyKeys.Clear();
    }

    public override void _Input(InputEvent @event)
    {
        // Non-focusable panel buttons still take input away from the world. GUI delivery remains Godot's job.
        if (Observer is not null && @event is InputEventMouseButton { Pressed: true } mouse && !WorldRect.HasPoint(mouse.Position))
            _worldInput.ReleaseFocus();
    }

    /// <summary>
    /// A right-drag turns the camera only while it started in the world and the motion still reports the button:
    /// the console, a panel holding mouse focus or a focus loss can swallow the release, so each frame drops a drag
    /// whose button is up or whose input the console took, instead of resuming it later.
    /// </summary>
    private void HandleWorldInput(InputEvent @event)
    {
        if (Observer is null || _consoleOpen())
            return;
        if (@event is InputEventMouseMotion pointer)
        {
            _pointerScreen = _worldInput.GetGlobalTransformWithCanvas() * pointer.Position;
            ShowPointerCoordinates(_pointerScreen.Value);
        }
        if (@event is InputEventMouseMotion hover && !_dragging &&
            CellPointer?.Invoke(_worldInput.GetGlobalTransformWithCanvas() * hover.Position, false) == true)
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left or MouseButton.Right })
            _worldInput.GrabFocus();
        if (!WorldHasInput)
            return;
        // Physical movement keys and logical shortcuts can overlap on layouts such as Colemak.
        if (@event is InputEventKey movement && (movement.PhysicalKeycode == Key.Shift || FlyDirections.ContainsKey(movement.PhysicalKeycode)))
        {
            if (movement.Pressed)
                _flyKeys.Add(movement.PhysicalKeycode);
            else
                _flyKeys.Remove(movement.PhysicalKeycode);
        }
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click:
                var screen = _worldInput.GetGlobalTransformWithCanvas() * click.Position;
                if (CellPointer?.Invoke(screen, true) != true)
                    Pick(screen);
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
            case InputEventMouseMotion motion when _dragging && motion.ButtonMask.HasFlag(MouseButtonMask.Right):
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
        if (!WorldHasInput)
            return;
        var direction = default(ObservationVector);
        foreach (var key in _flyKeys)
        {
            if (FlyDirections.TryGetValue(key, out var axis))
                direction += axis;
        }
        if (direction == default)
            return;
        var speed = FlySpeed * delta * (_flyKeys.Contains(Key.Shift) ? FastMultiplier : 1);
        Observer.Fly(direction.X * speed, direction.Y * speed, direction.Z * speed);
    }

    private void ApplyCamera(ObservationCameraPose pose)
    {
        var position = To(pose.Position);
        var look = To(pose.Look);
        _camera.Position = position;
        if (_camera.IsInsideTree() && (look - position).LengthSquared() > 1e-8f)
            _camera.LookAt(look, Vector3.Up);
    }

    private void CreatePanel(VBoxContainer box)
    {
        _panel = box;
        box.AddThemeConstantOverride("separation", 6);

        box.AddChild(new Label { Text = "对象检查器" });
        _coordinates = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_coordinates);
        _mode = new Label();
        box.AddChild(_mode);
        var controls = new HFlowContainer();
        controls.AddThemeConstantOverride("separation", 6);
        box.AddChild(controls);
        AddButton(controls, "focus", "聚焦选中", () => Report("聚焦", Observer.FocusSelection(_sample)));
        AddButton(controls, "follow", "跟随选中", () => Report("跟随", Observer.FollowSelection(_sample)));
        AddButton(controls, "free", "自由观察", () => Report("自由观察", Observer.Free()));
        AddButton(controls, "clear", "清除选择", () => ChangeTarget(() => Report("清除选择", Observer.ClearSelection())));
        AddButton(controls, "parts", "进入车辆零件", () => ChangeTarget(() => Report("进入零件层级", Observer.EnterVehicleParts())));
        AddButton(controls, "whole", "返回整体", () => ChangeTarget(() => Report("返回整体", Observer.ReturnToWhole())));
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
            {
                var item = _entries[(int)index].Object;
                ChangeTarget(() => Report(item.DescribeZh(), Observer.Select(item)));
            }
        };
        box.AddChild(_list);

        _inspection = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_inspection);
        _notice = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _notice.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.3f));
        box.AddChild(_notice);
    }

    internal Button ActionButton(string action) => _buttons[action];

    private void ChangeTarget(Action action)
    {
        if (_session.HasEditorDraft)
            DraftGuard?.Invoke(action);
        else
            action();
    }

    private void AddButton(Container parent, string id, string text, Action action)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.Pressed += action;
        parent.AddChild(button);
        _buttons[id] = button;
    }

    private void Report(string action, WorldEditorObservationStatus status)
    {
        _action.Text = $"{action} → {status}";
        UpdatePanel();
    }

    private void UpdatePanel()
    {
        var mode = Observer.Mode switch
        {
            WorldEditorCameraMode.Focus => "聚焦 " + Observer.Target!.Value.DescribeZh(),
            WorldEditorCameraMode.Follow => "跟随 " + Observer.Target!.Value.DescribeZh(),
            _ => "自由观察"
        };
        SetText(_mode, $"镜头：{mode}{(Observer.Transitioning ? "（过渡中）" : "")}");

        _buttons["parts"].Disabled = Observer.Selection?.Kind != ObservedObjectKind.Vehicle;
        _buttons["whole"].Disabled = Observer.PartVehicleId is null;
        var entries = Observer.PartVehicleId is { } owner
            ? WorldEditorInspector.ListParts(_session, owner) : WorldEditorInspector.List(_session);
        if (!entries.SequenceEqual(_entries))
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

        var inspection = Observer.Selection is { } selected && WorldEditorInspector.Inspect(_session, selected) is { } facts
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
            case ObservedObjectKind.VehiclePart:
                if (WorldEditorObserver.FindVehiclePart(_session, selected) is not { } found ||
                    WorldEditorObserver.VehicleFrame(_session, sample, selected.Id) is not { } partFrame)
                    return false;
                transform = new Transform3D(ToGodot(partFrame.Transform).Basis.Scaled(new Vector3(1.1f, 1.1f, 1.1f)),
                    To(WorldEditorObserver.PartCenter(found.Part, partFrame.Transform)));
                return true;
            case ObservedObjectKind.Vehicle:
                var vehicle = _session.Vehicles.FirstOrDefault(item => item.Id == selected.Id);
                if (vehicle is null || vehicle.Parts.Count == 0 || WorldEditorObserver.VehicleFrame(_session, sample, selected.Id) is not { } frame)
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
