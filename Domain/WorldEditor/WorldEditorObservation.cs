using System.Globalization;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

public enum ObservedObjectKind
{
    Vehicle,
    RailSegment,
    WorldCell,
    VehiclePart
}

/// <summary>
/// A reference to one formal object. An ordinary world voxel has no entity identity, so a cell reference remembers
/// the definition and any detached-fragment runtime id it was selected with and is lost when either changes, when an
/// executed edit empties the cell, or when the whole world is replaced.
/// </summary>
public readonly record struct ObservedObject(
    ObservedObjectKind Kind,
    EntityId Id,
    WorldVoxelCoord Cell,
    EntityId? RuntimeId,
    VoxelDefinitionId? Definition = null,
    EntityId? PartId = null)
{
    public static ObservedObject Vehicle(EntityId id) => new(ObservedObjectKind.Vehicle, id, default, null);
    public static ObservedObject Segment(EntityId id) => new(ObservedObjectKind.RailSegment, id, default, null);
    public static ObservedObject VehiclePart(EntityId vehicleId, EntityId partId) =>
        new(ObservedObjectKind.VehiclePart, vehicleId, default, null, PartId: partId);
    public static ObservedObject WorldCell(WorldVoxelCoord cell, EntityId? runtimeId = null, VoxelDefinitionId? definition = null) =>
        new(ObservedObjectKind.WorldCell, default, cell, runtimeId, definition);

    public string Describe() => Kind switch
    {
        ObservedObjectKind.Vehicle => "vehicle " + Id.Value.ToString(CultureInfo.InvariantCulture),
        ObservedObjectKind.RailSegment => "segment " + Id.Value.ToString(CultureInfo.InvariantCulture),
        ObservedObjectKind.VehiclePart => $"vehicle {Id.Value} part {PartId!.Value.Value}",
        _ => string.Create(CultureInfo.InvariantCulture, $"cell {Cell.X},{Cell.Y},{Cell.Z}")
    };

    public string DescribeZh() => Kind switch
    {
        ObservedObjectKind.Vehicle => "车辆 " + Id.Value.ToString(CultureInfo.InvariantCulture),
        ObservedObjectKind.RailSegment => "轨段 " + Id.Value.ToString(CultureInfo.InvariantCulture),
        ObservedObjectKind.VehiclePart => $"车辆 {Id.Value} 零件 {PartId!.Value.Value}",
        _ => string.Create(CultureInfo.InvariantCulture, $"世界格 {Cell.X},{Cell.Y},{Cell.Z}")
    };
}

public enum WorldEditorCameraMode
{
    Free,
    Focus,
    Follow
}

public enum WorldEditorObservationStatus
{
    Selected,
    Cleared,
    Focused,
    Following,
    Free,
    NoSelection,
    NotVehicle,
    UnknownObject,
    NoParts,
    NotInPartLevel
}

public enum WorldEditorNoticeKind
{
    SelectionLost,
    TargetLost
}

public sealed record WorldEditorObservationNotice(
    WorldEditorNoticeKind Kind,
    ObservedObject Object,
    WorldEditorCameraMode Mode,
    WorldReplacement? Replacement = null)
{
    public string Describe() =>
        (Kind == WorldEditorNoticeKind.SelectionLost
            ? $"selection-lost {Object.Describe()}"
            : $"target-lost {Object.Describe()} mode {Mode.ToString().ToLowerInvariant()} camera free-in-place") +
        (Replacement is { } replacement ? " world-replaced " + replacement.ToString().ToLowerInvariant() : "");

    public string DescribeZh()
    {
        var target = $"{(Mode == WorldEditorCameraMode.Follow ? "跟随" : "聚焦")}目标{Object.DescribeZh()}";
        if (Replacement is { } replacement)
        {
            var cause = replacement == WorldReplacement.Reset ? "世界已重置" : "世界已由读档替换";
            return Kind == WorldEditorNoticeKind.SelectionLost
                ? $"{cause}：选中的{Object.DescribeZh()} 不再指向原对象，已清除选择"
                : $"{cause}：{target} 不再指向原对象，已清除引用，镜头停在原位并转为自由观察";
        }

        return Kind == WorldEditorNoticeKind.SelectionLost
            ? $"选中的{Object.DescribeZh()} 已不存在，已清除选择"
            : $"{target} 已彻底销毁：已清除引用，镜头停在原位并转为自由观察";
    }
}

public readonly record struct ObservationVector(double X, double Y, double Z)
{
    public static ObservationVector operator +(ObservationVector a, ObservationVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static ObservationVector operator -(ObservationVector a, ObservationVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static ObservationVector operator *(ObservationVector a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public static ObservationVector Lerp(ObservationVector from, ObservationVector to, double t) => from + (to - from) * t;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{X:0.###},{Y:0.###},{Z:0.###}");
}

/// <summary>Camera position and the point it looks at; up is always world +Y.</summary>
public readonly record struct ObservationCameraPose(ObservationVector Position, ObservationVector Look);

/// <summary>
/// The displayed frame of one vehicle: the same transform its body is drawn with, the body centre, and the body
/// heading taken from its rear-to-front contact anchors through that transform.
/// </summary>
public sealed record ObservedVehicleFrame(
    EntityId VehicleId,
    CompositeTransform Transform,
    ObservationVector Center,
    ObservationVector Heading)
{
    public double HeadingYaw => Math.Atan2(Heading.Z, Heading.X);
}

/// <summary>
/// WorldEditor-only observation: selection, free / focus / follow camera and target-loss handling. It reads the
/// session but never changes it, is not saved, and takes no edit id, sequence or simulation step.
/// </summary>
public sealed class WorldEditorObserver : IDisposable
{
    public const double TransitionSeconds = 0.6;
    public const double MinDistance = 4;
    public const double MaxDistance = 80;
    public const double MaxPitch = 85 * Math.PI / 180;

    private readonly VoxelDevelopmentSession _session;
    private readonly List<WorldEditorObservationNotice> _notices = new();
    private ObservationVector _position;
    private double _yaw;
    private double _pitch;
    private ObservationVector _pivot;
    private double _distance;
    private double _yawOffset;
    private ObservationCameraPose _transitionFrom;
    private double _transitionElapsed;

    public WorldEditorObserver(VoxelDevelopmentSession session, ObservationVector position, ObservationVector look)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        SetFreePose(new ObservationCameraPose(position, look));
        Pose = FreePose();
        _session.EditExecuted += OnEditExecuted;
        _session.WorldReplaced += OnWorldReplaced;
    }

    public ObservedObject? Selection { get; private set; }
    public EntityId? PartVehicleId { get; private set; }
    public WorldEditorCameraMode Mode { get; private set; } = WorldEditorCameraMode.Free;
    public ObservedObject? Target { get; private set; }
    public bool Transitioning { get; private set; }
    public ObservationCameraPose Pose { get; private set; }
    public IReadOnlyList<WorldEditorObservationNotice> Notices => _notices;
    public WorldEditorObservationNotice? LastNotice => _notices.Count == 0 ? null : _notices[^1];
    public double Distance => _distance;

    public void Dispose()
    {
        _session.EditExecuted -= OnEditExecuted;
        _session.WorldReplaced -= OnWorldReplaced;
    }

    public bool Exists(ObservedObject item) => item.Kind switch
    {
        ObservedObjectKind.Vehicle => _session.Vehicles.Any(vehicle => vehicle.Id == item.Id),
        ObservedObjectKind.VehiclePart => FindVehiclePart(_session, item) is not null,
        ObservedObjectKind.RailSegment => _session.Rails.Segments.Any(segment => segment.Id == item.Id),
        _ => _session.Query(item.Cell).Voxel is { } voxel &&
             voxel.RuntimeId == item.RuntimeId &&
             (item.Definition is null || voxel.DefinitionId == item.Definition)
    };

    /// <summary>Selecting only changes what the panel inspects; the camera does not move.</summary>
    public WorldEditorObservationStatus Select(ObservedObject item)
    {
        if (item.Kind == ObservedObjectKind.WorldCell && _session.Query(item.Cell).Voxel is { } voxel)
            item = ObservedObject.WorldCell(item.Cell, voxel.RuntimeId, voxel.DefinitionId);
        if (!Exists(item))
            return WorldEditorObservationStatus.UnknownObject;
        Selection = item;
        PartVehicleId = item.Kind == ObservedObjectKind.VehiclePart ? item.Id : null;
        return WorldEditorObservationStatus.Selected;
    }

    /// <summary>Entering local selection is explicit and never changes the camera or the authoritative world.</summary>
    public WorldEditorObservationStatus EnterVehicleParts()
    {
        Reconcile();
        if (Selection is not { } selected)
            return WorldEditorObservationStatus.NoSelection;
        if (selected.Kind != ObservedObjectKind.Vehicle)
            return WorldEditorObservationStatus.NotVehicle;
        var part = _session.Vehicles.Single(vehicle => vehicle.Id == selected.Id).Parts.OrderBy(part => part.Id.Value).FirstOrDefault();
        return part is null ? WorldEditorObservationStatus.NoParts : Select(ObservedObject.VehiclePart(selected.Id, part.Id));
    }

    public WorldEditorObservationStatus ReturnToWhole() => PartVehicleId is { } id
        ? Select(ObservedObject.Vehicle(id)) : WorldEditorObservationStatus.NotInPartLevel;

    public WorldEditorObservationStatus ClearSelection()
    {
        Selection = null;
        PartVehicleId = null;
        return WorldEditorObservationStatus.Cleared;
    }

    /// <summary>Frames the selection once from the current view direction; the pivot does not track later motion.</summary>
    public WorldEditorObservationStatus FocusSelection(TrainDisplaySample? sample = null)
    {
        Reconcile();
        if (Selection is not { } selected)
            return WorldEditorObservationStatus.NoSelection;
        var center = Center(selected, sample ?? _session.QueryDisplayPoses());
        BeginTransition();
        Mode = WorldEditorCameraMode.Focus;
        Target = selected;
        _pivot = center;
        DeriveOrbit(center, out _yaw, out _pitch, out _distance);
        return WorldEditorObservationStatus.Focused;
    }

    /// <summary>Follows the selected vehicle, keeping the current view offset relative to its displayed heading.</summary>
    public WorldEditorObservationStatus FollowSelection(TrainDisplaySample? sample = null)
    {
        Reconcile();
        if (Selection is not { } selected)
            return WorldEditorObservationStatus.NoSelection;
        if (selected.Kind is not (ObservedObjectKind.Vehicle or ObservedObjectKind.VehiclePart))
            return WorldEditorObservationStatus.NotVehicle;
        selected = ObservedObject.Vehicle(selected.Id);
        var frame = VehicleFrame(_session, sample ?? _session.QueryDisplayPoses(), selected.Id)!;
        BeginTransition();
        Mode = WorldEditorCameraMode.Follow;
        Target = selected;
        DeriveOrbit(frame.Center, out var yaw, out _pitch, out _distance);
        _yawOffset = yaw - frame.HeadingYaw;
        return WorldEditorObservationStatus.Following;
    }

    /// <summary>Returns to free observation exactly where the camera currently is.</summary>
    public WorldEditorObservationStatus Free()
    {
        EnterFreeInPlace();
        return WorldEditorObservationStatus.Free;
    }

    /// <summary>Free mode turns the view; focus and follow orbit around their target.</summary>
    public void Look(double deltaYaw, double deltaPitch)
    {
        if (!double.IsFinite(deltaYaw) || !double.IsFinite(deltaPitch))
            return;
        if (Mode == WorldEditorCameraMode.Follow)
            _yawOffset += deltaYaw;
        else
            _yaw += deltaYaw;
        _pitch = ClampPitch(_pitch + deltaPitch);
    }

    /// <summary>Free mode dollies along the view; focus and follow change the orbit distance.</summary>
    public void Zoom(double amount)
    {
        if (!double.IsFinite(amount))
            return;
        if (Mode == WorldEditorCameraMode.Free)
            _position += Forward(_yaw, _pitch) * amount;
        else
            _distance = Math.Clamp(_distance - amount, MinDistance, MaxDistance);
    }

    /// <summary>Moves the free camera in its own right / up / forward axes. Ignored while focusing or following.</summary>
    public bool Fly(double right, double up, double forward)
    {
        if (Mode != WorldEditorCameraMode.Free || !double.IsFinite(right) || !double.IsFinite(up) || !double.IsFinite(forward))
            return false;
        var horizontal = new ObservationVector(Math.Cos(_yaw), 0, Math.Sin(_yaw));
        var side = new ObservationVector(-Math.Sin(_yaw), 0, Math.Cos(_yaw));
        _position += horizontal * forward + side * right + new ObservationVector(0, up, 0);
        return true;
    }

    /// <summary>
    /// Drops references to objects that no longer exist. A lost focus/follow target leaves the camera where it is,
    /// in free mode; it never jumps to another vehicle. Also runs after every executed edit, so a cell removed and
    /// rebuilt with the same definition before the next frame still counts as lost.
    /// </summary>
    public IReadOnlyList<WorldEditorObservationNotice> Reconcile() => Release(null);

    private void OnEditExecuted(WorldEditRecord record) => Reconcile();

    private void OnWorldReplaced(WorldReplacement replacement) => Release(replacement);

    /// <summary>A replaced world may reuse every id and cell, so after a replacement every reference is lost.</summary>
    private IReadOnlyList<WorldEditorObservationNotice> Release(WorldReplacement? replacement)
    {
        bool Lost(ObservedObject item) => replacement is not null || !Exists(item);
        var raised = new List<WorldEditorObservationNotice>();
        if (Selection is { } selected && Lost(selected))
        {
            Selection = null;
            raised.Add(new WorldEditorObservationNotice(WorldEditorNoticeKind.SelectionLost, selected, Mode, replacement));
        }

        if (Target is { } target && Lost(target))
        {
            var mode = Mode;
            EnterFreeInPlace();
            raised.Add(new WorldEditorObservationNotice(WorldEditorNoticeKind.TargetLost, target, mode, replacement));
        }

        _notices.AddRange(raised);
        if (PartVehicleId is { } owner && (replacement is not null || !Exists(ObservedObject.Vehicle(owner))))
            PartVehicleId = null;
        return raised;
    }

    /// <summary>Advances the camera by one display frame, reading the same display sample the vehicle bodies use.</summary>
    public ObservationCameraPose Present(double displayDelta, TrainDisplaySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        Reconcile();
        var goal = Goal(sample);
        if (Transitioning)
        {
            if (double.IsFinite(displayDelta) && displayDelta > 0)
                _transitionElapsed += displayDelta;
            var t = Math.Clamp(_transitionElapsed / TransitionSeconds, 0, 1);
            if (t >= 1)
                Transitioning = false;
            else
            {
                var s = t * t * (3 - 2 * t);
                goal = new ObservationCameraPose(
                    ObservationVector.Lerp(_transitionFrom.Position, goal.Position, s),
                    ObservationVector.Lerp(_transitionFrom.Look, goal.Look, s));
            }
        }

        Pose = goal;
        return goal;
    }

    public ObservationVector Center(ObservedObject item, TrainDisplaySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        switch (item.Kind)
        {
            case ObservedObjectKind.VehiclePart:
                if (FindVehiclePart(_session, item) is not { } found || VehicleFrame(_session, sample, item.Id) is not { } frame)
                    return Pose.Look;
                return PartCenter(found.Part, frame.Transform);
            case ObservedObjectKind.Vehicle:
                return VehicleFrame(_session, sample, item.Id)?.Center ?? Pose.Look;
            case ObservedObjectKind.RailSegment:
                var segment = _session.Rails.Segments.FirstOrDefault(value => value.Id == item.Id);
                if (segment is null)
                    return Pose.Look;
                var alignment = segment.Alignment;
                return new ObservationVector(
                    (alignment.StartX + alignment.EndX) * 0.5,
                    (alignment.StartY + alignment.EndY) * 0.5,
                    (alignment.StartZ + alignment.EndZ) * 0.5);
            default:
                return new ObservationVector(item.Cell.X + 0.5, item.Cell.Y + 0.5, item.Cell.Z + 0.5);
        }
    }

    /// <summary>Resolves a part's stable identity within its owning vehicle; a missing owner or part returns null.</summary>
    public static (CompositeInstance Vehicle, CompositePart Part)? FindVehiclePart(VoxelDevelopmentSession session, ObservedObject item)
    {
        var vehicle = session.Vehicles.FirstOrDefault(vehicle => vehicle.Id == item.Id);
        return item.Kind == ObservedObjectKind.VehiclePart && item.PartId is { } partId && vehicle?.PartById(partId) is { } part
            ? (vehicle, part) : null;
    }

    /// <summary>The centre of a local part cell in the vehicle's displayed world transform.</summary>
    public static ObservationVector PartCenter(CompositePart part, CompositeTransform transform)
    {
        var point = transform.TransformPoint(part.Cell.X + 0.5, part.Cell.Y + 0.5, part.Cell.Z + 0.5);
        return new ObservationVector(point.X, point.Y, point.Z);
    }

    /// <summary>
    /// The frame a vehicle body is displayed with: its interpolated display pose when it belongs to an assembly,
    /// otherwise its authoritative transform.
    /// </summary>
    public static ObservedVehicleFrame? VehicleFrame(VoxelDevelopmentSession session, TrainDisplaySample sample, EntityId vehicleId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sample);
        var vehicle = session.Vehicles.FirstOrDefault(item => item.Id == vehicleId);
        if (vehicle is null)
            return null;
        var transform = sample.Vehicles.FirstOrDefault(item => item.VehicleId == vehicleId)?.Transform ?? vehicle.Transform;
        var local = LocalCenter(vehicle);
        var center = transform.TransformPoint(local.X, local.Y, local.Z);
        var heading = transform.Right;
        if (vehicle.Contacts.Count >= 2)
        {
            var front = transform.TransformPoint(vehicle.Contacts[0].X, vehicle.Contacts[0].Y, vehicle.Contacts[0].Z);
            var rear = transform.TransformPoint(vehicle.Contacts[1].X, vehicle.Contacts[1].Y, vehicle.Contacts[1].Z);
            heading = (front.X - rear.X, front.Y - rear.Y, front.Z - rear.Z);
        }

        var length = Math.Sqrt(heading.X * heading.X + heading.Y * heading.Y + heading.Z * heading.Z);
        var unit = length > 0 && double.IsFinite(length)
            ? new ObservationVector(heading.X / length, heading.Y / length, heading.Z / length)
            : new ObservationVector(1, 0, 0);
        return new ObservedVehicleFrame(vehicleId, transform, new ObservationVector(center.X, center.Y, center.Z), unit);
    }

    public static ObservationVector LocalCenter(CompositeInstance vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (vehicle.Parts.Count == 0)
            return default;
        return new ObservationVector(
            (vehicle.Parts.Min(part => part.Cell.X) + vehicle.Parts.Max(part => part.Cell.X) + 1) * 0.5,
            (vehicle.Parts.Min(part => part.Cell.Y) + vehicle.Parts.Max(part => part.Cell.Y) + 1) * 0.5,
            (vehicle.Parts.Min(part => part.Cell.Z) + vehicle.Parts.Max(part => part.Cell.Z) + 1) * 0.5);
    }

    public static ObservationVector Forward(double yaw, double pitch) =>
        new(Math.Cos(pitch) * Math.Cos(yaw), Math.Sin(pitch), Math.Cos(pitch) * Math.Sin(yaw));

    public string Describe()
    {
        var target = Target is { } value ? " target " + value.Describe() : "";
        var selection = Selection is { } selected ? selected.Describe() : "none";
        return $"mode {Mode.ToString().ToLowerInvariant()}{target} selection {selection} transitioning {Transitioning.ToString().ToLowerInvariant()}";
    }

    private ObservationCameraPose Goal(TrainDisplaySample sample)
    {
        switch (Mode)
        {
            case WorldEditorCameraMode.Focus:
                return Orbit(_pivot, _yaw, _pitch, _distance);
            case WorldEditorCameraMode.Follow:
                var frame = VehicleFrame(_session, sample, Target!.Value.Id)!;
                return Orbit(frame.Center, frame.HeadingYaw + _yawOffset, _pitch, _distance);
            default:
                return FreePose();
        }
    }

    private ObservationCameraPose FreePose() => new(_position, _position + Forward(_yaw, _pitch));

    private static ObservationCameraPose Orbit(ObservationVector pivot, double yaw, double pitch, double distance) =>
        new(pivot - Forward(yaw, pitch) * distance, pivot);

    private void BeginTransition()
    {
        _transitionFrom = Pose;
        _transitionElapsed = 0;
        Transitioning = true;
    }

    private void EnterFreeInPlace()
    {
        SetFreePose(Pose);
        Mode = WorldEditorCameraMode.Free;
        Target = null;
        Transitioning = false;
    }

    private void SetFreePose(ObservationCameraPose pose)
    {
        _position = pose.Position;
        var direction = pose.Look - pose.Position;
        if (direction.Length <= 1e-12 || !double.IsFinite(direction.Length))
            return;
        _yaw = Math.Atan2(direction.Z, direction.X);
        _pitch = ClampPitch(Math.Asin(Math.Clamp(direction.Y / direction.Length, -1, 1)));
    }

    private void DeriveOrbit(ObservationVector pivot, out double yaw, out double pitch, out double distance)
    {
        var toPivot = pivot - Pose.Position;
        var length = toPivot.Length;
        if (length <= 1e-9 || !double.IsFinite(length))
        {
            var look = Pose.Look - Pose.Position;
            yaw = Math.Atan2(look.Z, look.X);
            pitch = ClampPitch(look.Length > 0 ? Math.Asin(Math.Clamp(look.Y / look.Length, -1, 1)) : 0);
            distance = MinDistance;
            return;
        }

        yaw = Math.Atan2(toPivot.Z, toPivot.X);
        pitch = ClampPitch(Math.Asin(Math.Clamp(toPivot.Y / length, -1, 1)));
        distance = Math.Clamp(length, MinDistance, MaxDistance);
    }

    private static double ClampPitch(double pitch) => Math.Clamp(pitch, -MaxPitch, MaxPitch);
}
