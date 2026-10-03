using Godot;
using Train.Domain.Composites;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Presentation;

namespace Train;

internal enum WorldPointerAction { Move, LeftPressed, LeftReleased }

internal sealed record WorldEditorWorldHit(WorldVoxelCoord Cell, double Distance, Vector3 Position, Vector3 Normal, WorldVoxelCoord AdjacentCell);

internal sealed record WorldEditorRay(Vector2 Screen, Vector3 Origin, Vector3 Direction);

internal delegate bool WorldEditorCellPointer(WorldEditorPick pick, WorldPointerAction action, out ObservationVector? worldPoint);

/// <summary>The visible vehicle or world surface resolved at one screen point.</summary>
internal sealed record WorldEditorPick(
    WorldEditorRay Ray,
    ObservedObject? Object,
    WorldEditorObservationStatus? Status,
    WorldEditorVehicleHit? VehicleHit,
    WorldEditorWorldHit? WorldHit);

internal sealed record WorldEditorBoxCandidate(ObservedObject Object, Vector3 Point, Vector2 Screen);

internal sealed record WorldEditorBoxSelection(IReadOnlyList<ObservedObject> Objects, int Candidates, int Rays,
    bool Truncated, double ElapsedMilliseconds, int Samples = 0);

/// <summary>
/// Godot adapter for <see cref="WorldEditorObserver"/>: drives the camera from its pose, draws the selection
/// highlight, and hosts the object list / inspection panel and observation input. Batch actions use the shared voxel draft.
/// </summary>
public partial class WorldEditorObservationView : Node3D
{
    public const double LookSensitivity = 0.005;
    public const double ZoomStep = 2;
    public const double FlySpeed = 12;
    public const double FastMultiplier = 4;
    public const float PickDistance = 500;
    // Provisional synchronous work budget; candidates after this limit are not queried or selected.
    public const int BoxSelectionCandidateLimit = 4096;
    private const float BoxDragThreshold = 6;

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
    private MultiMeshInstance3D _highlight = null!;
    private Panel _boxRectangle = null!;
    private Vector2? _selectionStart;
    private Control _panel = null!;
    private Control _worldInput = null!;
    private readonly HashSet<Key> _flyKeys = new();
    private int _shownNotices;
    private bool _dragging;
    private bool _extendListSelection;
    private Vector2? _pointerScreen;
    private readonly List<(WorldEditorRay Ray, WorldPointerAction Action)> _pointerActions = new();
    private ulong _pointerPickFrame = ulong.MaxValue;

    public WorldEditorObserver Observer { get; private set; } = null!;
    internal WorldEditorCellPointer? CellPointer { get; set; }
    internal Action? PointerInputInterrupted { get; set; }
    internal Action<Action>? DraftGuard { get; set; }
    internal WorldEditorPick? LastPick { get; private set; }
    internal WorldEditorPick? LastPointerPick { get; private set; }
    internal WorldEditorVehicleHit? HoverVehicle { get; private set; }
    internal ObservationVector? PointerWorldPosition { get; private set; }
    internal bool PointerCoordinatesVisible => _coordinates.IsVisibleInTree();
    internal WorldEditorBoxSelection? LastBoxSelection { get; private set; }
    internal ItemList ListControl => _list;
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
        _worldInput.MouseExited += ClearHover;
        session.WorldReplaced += OnWorldReplaced;
        _boxRectangle = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _boxRectangle.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(1, 0.85f, 0.2f, 0.1f), BorderColor = new Color(1, 0.85f, 0.2f),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1
        });
        _worldInput.AddChild(_boxRectangle);
        Observer = new WorldEditorObserver(session, initial.Position, initial.Look);
        CreatePanel(inspector);
        ClearPointerCoordinates();
        CreateHighlight();
        ApplyCamera(Observer.Pose);
    }

    public override void _ExitTree()
    {
        _pointerActions.Clear();
        _worldInput.GuiInput -= HandleWorldInput;
        _worldInput.FocusExited -= EndInput;
        _worldInput.MouseExited -= ClearHover;
        if (_session is not null)
            _session.WorldReplaced -= OnWorldReplaced;
        Observer?.Dispose();
    }

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
        ProcessPointerActions();
        UpdateHighlight(sample);
        UpdatePanel();
    }

    private void ProcessPointerActions()
    {
        if ((_pointerActions.Count > 0 || _pointerScreen is not null) && !_consoleOpen())
        {
            var actions = _pointerActions.ToArray();
            _pointerActions.Clear();
            // A tool press owns its original ray; inspector point selection resolves the release instead.
            var ray = actions.Length == 0 ? PointerRay(_pointerScreen!.Value)
                : _session.VoxelTool?.Active == true
                    ? actions.FirstOrDefault(action => action.Action == WorldPointerAction.LeftPressed).Ray ?? actions[^1].Ray
                    : actions[^1].Ray;
            var pick = LastPointerPick;
            if (_pointerPickFrame != Engine.GetProcessFrames())
            {
                _pointerPickFrame = Engine.GetProcessFrames();
                pick = QueryPick(ray);
            }
            else if (actions.Length == 0)
                return;
            pick ??= new WorldEditorPick(ray, null, null, null, null);
            LastPointerPick = pick;
            ObservationVector? toolPoint = null;
            var consumed = false;
            foreach (var (actionRay, action) in actions)
            {
                if (action == WorldPointerAction.Move && (_dragging || !WorldRect.HasPoint(actionRay.Screen)))
                    continue;
                ObservationVector? point = null;
                // Only the queried ray owns the raw hit. Other events may still intersect an analytic plane.
                var actionPick = actionRay == pick.Ray ? pick : new WorldEditorPick(actionRay, null, null, null, null);
                var handled = CellPointer?.Invoke(actionPick, action, out point) == true;
                if (point is not null) toolPoint = point;
                consumed |= handled;
                if (handled)
                {
                    _selectionStart = null;
                    _boxRectangle.Hide();
                }
                else if (action == WorldPointerAction.LeftPressed)
                    _selectionStart = actionRay.Screen;
                else if (action == WorldPointerAction.LeftReleased && _selectionStart is { } start)
                {
                    _selectionStart = null;
                    _boxRectangle.Hide();
                    if (start.DistanceTo(actionRay.Screen) >= BoxDragThreshold)
                        BoxSelect(start, actionRay.Screen);
                    else
                        Pick(pick);
                }
            }
            if (actions.Length == 0 && !_dragging && WorldRect.HasPoint(ray.Screen))
                consumed = CellPointer?.Invoke(pick, WorldPointerAction.Move, out toolPoint) == true;
            if (_pointerScreen is null)
                ClearPointerCoordinates();
            else
                ShowPointerCoordinates(pick, consumed ? toolPoint : pick.WorldHit is { } hit ? From(hit.Position) : null);
        }
    }

    internal void Press(string button)
    {
        if (_buttons[button].Disabled)
            throw new InvalidOperationException($"Observation action {button} is disabled.");
        _buttons[button].EmitSignal(BaseButton.SignalName.Pressed);
    }

    internal void SelectListIndex(int index)
    {
        _extendListSelection = false;
        _list.Select(index);
        ApplyListSelection();
    }

    /// <summary>Selects the picked object at the current inspection level, respecting an existing draft.</summary>
    private void Pick(WorldEditorPick raw)
    {
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
    }

    internal WorldEditorBoxSelection BoxSelect(Vector2 start, Vector2 end)
    {
        if (Observer.PartVehicleId is not null)
        {
            _action.Text = "零件层级不支持框选，请返回整体层级";
            return LastBoxSelection = new(Array.Empty<ObservedObject>(), 0, 0, false, 0);
        }
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var rectangle = new Rect2(start, end - start).Abs().Intersection(WorldRect);
        if (rectangle.Size.X <= 0 || rectangle.Size.Y <= 0)
        {
            _action.Text = "框选：空矩形，选择不变";
            return LastBoxSelection = new(Observer.OrderedSelection.ToArray(), 0, 0, false, timer.Elapsed.TotalMilliseconds);
        }
        var projected = ProjectBoxCandidates(_session, _sample ?? _session.QueryDisplayPoses(), _camera, rectangle, out var samples);
        var selected = new HashSet<ObservedObject>();
        var candidates = Math.Min(projected.Length, BoxSelectionCandidateLimit);
        var rays = 0;
        var truncated = projected.Length > candidates;
        // A whole object is visible iff a sample projects into the rectangle and the existing nearest-hit ray
        // reaches that object first: world/rail voxels sample their cell centre, vehicles sample each part centre.
        // This is deliberately a sample rule, not selection of every object whose silhouette touches the box.
        foreach (var candidate in projected.Take(candidates))
        {
            if (selected.Contains(candidate.Object))
                continue;
            rays++;
            if (QueryPick(PointerRay(candidate.Screen)).Object == candidate.Object)
                selected.Add(candidate.Object);
        }
        var targets = selected.ToArray();
        ChangeSelection(() => Observer.SelectMany(targets));
        LastBoxSelection = new(targets, candidates, rays, truncated, timer.Elapsed.TotalMilliseconds, samples);
        _action.Text = $"框选：{targets.Length} 个对象 · 矩形内 {candidates} 个候选 · {rays} 条射线" +
            (truncated ? $"；达到暂定上限 {BoxSelectionCandidateLimit}，后续候选未选择" : "");
        UpdatePanel();
        return LastBoxSelection;
    }

    internal static WorldEditorBoxCandidate[] ProjectBoxCandidates(VoxelDevelopmentSession session,
        TrainDisplaySample sample, Camera3D camera, Rect2 rectangle, out int samples)
    {
        IEnumerable<(ObservedObject? Object, WorldVoxelCoord? Cell, Vector3 Point)> Samples()
        {
            foreach (var vehicle in session.Vehicles)
            {
                var frame = WorldEditorObserver.VehicleFrame(session, sample, vehicle.Id)!;
                foreach (var part in vehicle.Parts)
                    yield return (ObservedObject.Vehicle(vehicle.Id), null, To(WorldEditorObserver.PartCenter(part, frame.Transform)));
            }
            foreach (var (cell, _) in session.World.OccupiedVoxels())
                yield return (null, cell, new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f));
        }
        var projected = new List<WorldEditorBoxCandidate>();
        samples = 0;
        foreach (var (item, cell, point) in Samples())
        {
            samples++;
            if (camera.IsPositionBehind(point))
                continue;
            var screen = camera.UnprojectPosition(point);
            if (rectangle.HasPoint(screen))
                projected.Add(new(item ?? WorldEditorInspector.WorldTarget(session, cell!.Value), point, screen));
        }
        // Filter before applying the ray budget. Distance, coordinates and identity break ties independently of
        // world dictionary insertion/load order, so an oversized rectangle always truncates the same candidates.
        return projected.OrderBy(candidate => camera.GlobalPosition.DistanceSquaredTo(candidate.Point))
            .ThenBy(candidate => candidate.Point.X).ThenBy(candidate => candidate.Point.Y).ThenBy(candidate => candidate.Point.Z)
            .ThenBy(candidate => candidate.Object.Kind).ThenBy(candidate => candidate.Object.Id.Value).ToArray();
    }

    private WorldEditorRay PointerRay(Vector2 screen) => new(screen, _camera.ProjectRayOrigin(screen), _camera.ProjectRayNormal(screen));

    private WorldEditorPick QueryPick(WorldEditorRay ray)
    {
        var (_, origin, direction) = ray;
        var vehicle = WorldEditorInspector.RaycastVehicles(_session, _sample ?? _session.QueryDisplayPoses(), From(origin), From(direction), PickDistance);
        var world = _colliders.QueryPublishedRay(GetWorld3D(), origin, origin + direction * PickDistance);
        WorldEditorWorldHit? worldHit = world is { } hit
            ? new(hit.Cell, origin.DistanceTo(hit.Position), hit.Position, hit.Normal, hit.AdjacentCell) : null;
        if (vehicle is not null && (worldHit is null || vehicle.Distance <= worldHit.Distance))
            worldHit = null;
        else
            vehicle = null;
        ObservedObject? item = vehicle is not null ? ObservedObject.Vehicle(vehicle.VehicleId)
            : worldHit is { } cell ? WorldEditorInspector.WorldTarget(_session, cell.Cell) : null;
        return new WorldEditorPick(ray, item, null, vehicle, worldHit);
    }

    private void ShowPointerCoordinates(WorldEditorPick pick, ObservationVector? worldPoint)
    {
        HoverVehicle = pick.VehicleHit;
        PointerWorldPosition = HoverVehicle?.WorldPosition ?? worldPoint;
        _coordinates.Visible = PointerWorldPosition is not null;
        _coordinates.Text = HoverVehicle is { } vehicle
            ? $"光标 · 车辆 {vehicle.VehicleId.Value}\n世界坐标 {Coordinates(vehicle.WorldPosition)}\n车辆局部坐标 {Coordinates(vehicle.LocalPosition)}"
            : PointerWorldPosition is { } point ? $"光标 · 世界坐标 {Coordinates(point)}" : "";
    }

    private void ClearPointerCoordinates()
    {
        HoverVehicle = null;
        PointerWorldPosition = null;
        _coordinates.Text = "";
        _coordinates.Hide();
    }

    private void ClearHover()
    {
        _pointerScreen = null;
        LastPointerPick = null;
        ClearPointerCoordinates();
    }

    private void OnWorldReplaced(WorldReplacement _)
    {
        _pointerActions.Clear();
        ClearHover();
    }

    private static string Coordinates(ObservationVector point) => FormattableString.Invariant($"({point.X:0.###}, {point.Y:0.###}, {point.Z:0.###})");

    public override void _Notification(int what)
    {
        if (what is (int)NotificationWMWindowFocusOut or (int)NotificationApplicationFocusOut)
            EndInput();
    }

    private void EndInput()
    {
        // Complete earlier world input before a panel press changes tools or interrupts a stroke.
        if (_pointerActions.Count > 0 && !_consoleOpen())
            ProcessPointerActions();
        _dragging = false;
        _pointerActions.Clear();
        _selectionStart = null;
        if (_boxRectangle is not null)
            _boxRectangle.Visible = false;
        _flyKeys.Clear();
        PointerInputInterrupted?.Invoke();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } listClick &&
            _list.GetGlobalRect().HasPoint(listClick.Position))
            _extendListSelection = listClick.CtrlPressed || listClick.MetaPressed || listClick.ShiftPressed;
        if (Observer is not null && !_consoleOpen() &&
            @event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release &&
            !WorldRect.HasPoint(release.Position) && _session.VoxelTool is { Active: true, Mode: WorldEditorVoxelMode.Brush } tool &&
            (tool.StrokeInProgress || _pointerActions.Any(action => action.Action == WorldPointerAction.LeftPressed)))
        {
            // A stroke can be released over the inspector, outside World.GuiInput's delivery area.
            _pointerActions.Add((PointerRay(release.Position), WorldPointerAction.LeftReleased));
            GetViewport().SetInputAsHandled();
            return;
        }
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
            var screen = _worldInput.GetGlobalTransformWithCanvas() * pointer.Position;
            _pointerScreen = WorldRect.HasPoint(screen) ? screen : null;
            if (!_dragging && WorldRect.HasPoint(screen))
                _pointerActions.Add((PointerRay(screen), WorldPointerAction.Move));
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
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click:
                var screen = _worldInput.GetGlobalTransformWithCanvas() * click.Position;
                _pointerScreen = WorldRect.HasPoint(screen) ? screen : null;
                if (_session.VoxelTool?.Active != true)
                {
                    if (click.Pressed)
                    {
                        _selectionStart = screen;
                        break;
                    }
                    if (_selectionStart is { } selectionStart && selectionStart.DistanceTo(screen) >= BoxDragThreshold)
                    {
                        _selectionStart = null;
                        _boxRectangle.Hide();
                        BoxSelect(selectionStart, screen);
                        break;
                    }
                }
                _pointerActions.Add((PointerRay(screen), click.Pressed ? WorldPointerAction.LeftPressed : WorldPointerAction.LeftReleased));
                break;
            case InputEventMouseMotion motion when _selectionStart is { } start && motion.ButtonMask.HasFlag(MouseButtonMask.Left):
                var localStart = _worldInput.GetGlobalTransformWithCanvas().AffineInverse() * start;
                var box = new Rect2(localStart, motion.Position - localStart).Abs();
                _boxRectangle.Position = box.Position;
                _boxRectangle.Size = box.Size;
                _boxRectangle.Visible = box.Size.Length() >= BoxDragThreshold;
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
            case InputEventKey { Pressed: true, Echo: false } key when key.Keycode == Key.F && !_buttons["focus"].Disabled:
                Press("focus");
                break;
            case InputEventKey { Pressed: true, Echo: false } key when key.Keycode == Key.V && !_buttons["follow"].Disabled:
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
        AddButton(controls, "clear", "清除选择", () => ChangeSelection(() =>
        {
            var status = Observer.ClearSelection();
            Report("清除选择", status);
            return status;
        }));
        AddButton(controls, "parts", "进入车辆零件", () => ChangeTarget(() => Report("进入零件层级", Observer.EnterVehicleParts())));
        AddButton(controls, "whole", "返回整体", () => ChangeTarget(() => Report("返回整体", Observer.ReturnToWhole())));
        AddButton(controls, "batch-remove", "批量移除", () => StartBatch(VoxelCellOperation.Remove));
        AddButton(controls, "batch-replace", "批量替换", () => StartBatch(VoxelCellOperation.Replace));
        _action = new Label { Text = "左键点选／拖动框选 · 列表 Ctrl 增减／Shift 连选 · 右键转向 · 滚轮推拉 · WASD/QE 飞行" };
        _action.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_action);

        _list = new ItemList
        {
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 150),
            SelectMode = ItemList.SelectModeEnum.Multi
        };
        _list.ItemSelected += _ => ApplyListSelection();
        // Godot updates the selection before emitting this signal, including Ctrl removal and Shift ranges.
        _list.MultiSelected += (_, _) => ApplyListSelection();
        box.AddChild(_list);

        _inspection = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_inspection);
        _notice = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _notice.AddThemeColorOverride("font_color", new Color(1f, 0.75f, 0.3f));
        box.AddChild(_notice);
    }

    internal Button ActionButton(string action) => _buttons[action];

    private void ApplyListSelection()
    {
        var selected = _list.GetSelectedItems().Where(index => index < _entries.Count).Select(index => _entries[index].Object).ToArray();
        if (_extendListSelection)
        {
            var listed = _entries.Select(entry => entry.Object).ToHashSet();
            selected = selected.Concat(Observer.SelectedObjects.Where(item => !listed.Contains(item))).ToArray();
        }
        ChangeSelection(() => Observer.SelectMany(selected));
        UpdatePanel();
    }

    private void ChangeSelection(Func<WorldEditorObservationStatus> action)
    {
        if (action() == WorldEditorObservationStatus.DraftRequired)
            DraftGuard?.Invoke(() => { action(); UpdatePanel(); });
    }

    private void StartBatch(VoxelCellOperation operation)
    {
        if (_session.VoxelTool is not { } tool)
            return;
        var result = tool.StartBatch(Observer.SelectedObjects, operation);
        _action.Text = $"批量{(operation == VoxelCellOperation.Remove ? "移除" : "替换")}：{tool.BatchTargets.Count} 格 · {WorldEditorVoxelToolView.Describe(result)}";
        UpdatePanel();
    }

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
        _buttons["focus"].Disabled = Observer.Selection is null;
        _buttons["follow"].Disabled = Observer.Selection is null;
        _buttons["batch-remove"].Disabled = !Observer.CanBatchEdit || _session.HasEditorDraft || _session.IsChangingWorld;
        _buttons["batch-replace"].Disabled = !Observer.CanBatchEdit || _session.HasEditorDraft || _session.IsChangingWorld;
        _list.SelectMode = Observer.PartVehicleId is null ? ItemList.SelectModeEnum.Multi : ItemList.SelectModeEnum.Single;
        var entries = Observer.PartVehicleId is { } owner
            ? WorldEditorInspector.ListParts(_session, owner) : WorldEditorInspector.List(_session);
        if (!entries.SequenceEqual(_entries))
        {
            _entries = entries;
            _list.Clear();
            foreach (var entry in entries)
                _list.AddItem(entry.Label);
        }

        for (var index = 0; index < _entries.Count; index++)
        {
            if (Observer.SelectedObjects.Contains(_entries[index].Object))
            {
                if (!_list.IsSelected(index))
                    _list.Select(index, false);
            }
            else
                _list.Deselect(index);
        }

        var inspection = Observer.Selection is { } selected && WorldEditorInspector.Inspect(_session, selected) is { } facts
            ? "选中：1 个 · " + selected.DescribeZh() + "\n" + string.Join('\n', facts.LinesZh())
            : "选中：" + Observer.SelectedObjects.Count + " 个\n" + string.Join('\n', Observer.OrderedSelection
                .Select(item => item.DescribeZh() + (item.Kind == ObservedObjectKind.WorldCell ? $" · {item.Definition} · RuntimeId {item.RuntimeId?.Value.ToString() ?? "无"}" : "")));
        if (Observer.PartVehicleId is not null)
            inspection = "零件层级不支持框选，请返回整体层级\n" + inspection;
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
        _highlight = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new BoxMesh { Size = Vector3.One } },
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
        var boxes = new List<Transform3D>();
        foreach (var selected in Observer.SelectedObjects)
            if (TryHighlightBox(selected, sample, out var transform))
                boxes.Add(transform);
        _highlight.Multimesh.InstanceCount = boxes.Count;
        for (var index = 0; index < boxes.Count; index++)
            _highlight.Multimesh.SetInstanceTransform(index, boxes[index]);
        _highlight.Visible = boxes.Count > 0;
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
