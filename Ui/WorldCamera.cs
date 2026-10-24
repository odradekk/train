using Godot;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal partial class WorldCamera : Node3D
{
    internal const float DefaultDistance = 48f;
    internal const float MaximumDistance = 240f;
    internal const float NodeMaximumDistance = 96f;
    internal const float ZoomStep = 0.15f;
    private const float OrbitSensitivity = 0.006f;
    private const float MinimumPitch = 0.15f;
    private const float MaximumPitch = 1.35f;
    private const float FollowResponse = 8f;
    private const float TerrainClearance = 1f;
    private ISceneFrame? _frame;
    private WorldObjectId? _follow;
    private bool _railwayPan;
    private Vector3 _targetPosition;
    private float _targetYaw;
    private float _bodyYaw;
    private float _orbitYaw;
    private float _pitch = 0.55f;
    private float _distance = DefaultDistance;
    private bool _hasFollowPose;
    private bool _clearancePending;
    private ScenePanBounds? NodePanBounds => _follow is null && !_railwayPan ? _frame?.NodePanBounds : null;
    internal Func<SceneObjectId, float?>? ObjectTop { get; set; }

    public Vector3 Focus { get; private set; }
    internal float ZoomCeiling { get; set; } = MaximumDistance;
    public Camera3D Camera { get; } = new() { Fov = 60f, Near = 0.1f, Far = 4000f };
    internal bool ShakeEnabled { get; set; } = true;
    internal Vector3 ShakeOffset { get; private set; }
    internal float OrbitYawRadians => _orbitYaw;
    internal float OrbitPitchRadians => _pitch;
    internal ScenePoint FrameOrigin => _frame?.Origin ?? default;
    internal double TerrainViewRadiusCells => ProjectionRadiusCells(Camera.GetCameraProjection(), Camera.Far);

    internal static double ProjectionRadiusCells(Projection projection, double far)
    {
        var inverse = projection.Inverse();
        double cornerDistance = 0;
        for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            {
                var corner = inverse * new Vector4(x, y, 1, 1);
                // 使用实际轴向 Far；不以远平面上接近零的齐次 W 还原深度。
                var dx = corner.X / (double)corner.Z;
                var dy = corner.Y / (double)corner.Z;
                cornerDistance = Math.Max(cornerDistance, far * Math.Sqrt(1 + dx * dx + dy * dy));
            }
        // 三维角点距离包住任意朝向的水平投影；纯垂直地形避让不扩大水平偏移。
        return Math.Ceiling((cornerDistance + MaximumDistance + CombatEffects.ShakeDistance) / 256) * 256;
    }

    public WorldCamera()
    {
        AddChild(Camera);
    }

    public void DisplayedFrame(ISceneFrame frame, WorldObjectId? follow, bool railwayPan = false,
        Vector3? coordinateShift = null, bool preserveFocus = false)
    {
        var previous = _frame;
        var sameSpace = previous is not null && previous.Update.Frame.Session == frame.Update.Frame.Session
            && previous.Update.Frame.Space == frame.Update.Frame.Space;
        var moveWithTarget = sameSpace && _hasFollowPose && follow == _follow;
        var pose = follow is { } subject && frame.FindPose(subject) is Lookup<ScenePose>.Found found
            ? found.Value : null;
        if (sameSpace || previous is not null && previous.Update.Frame.Session == frame.Update.Frame.Session &&
            previous.Update.Frame.Request == frame.Update.Frame.Request &&
            (coordinateShift is not null || pose is not null && _hasFollowPose))
        {
            // 同会话的空间交接保持边界显示坐标；准备期间的平移由显示组给出。
            var shift = coordinateShift ?? (sameSpace ? new Vector3((float)(previous!.Origin.X - frame.Origin.X),
                (float)(previous.Origin.Y - frame.Origin.Y), (float)(previous.Origin.Z - frame.Origin.Z)) : Vector3.Zero);
            Focus += shift;
            _targetPosition += shift;
        }
        else if (pose is not null && _hasFollowPose)
        {
            // 新会话重开时，保留相对于目标的跟随余量和观察方位。
            var yawChange = Mathf.Wrap(Heading(pose) - _targetYaw, -Mathf.Pi, Mathf.Pi);
            Focus = ToGodot(pose.Position) + (Focus - _targetPosition).Rotated(Vector3.Up, yawChange);
            _bodyYaw += yawChange;
        }
        else if (!preserveFocus || previous is null)
        {
            Focus = pose is not null ? ToGodot(pose.Position) : InitialFocus(frame);
            if (pose is not null)
                _bodyYaw = Heading(pose);
        }

        _frame = frame;
        _follow = follow;
        _railwayPan = railwayPan;
        if (pose is not null)
        {
            if (moveWithTarget)
                Focus += ToGodot(pose.Position) - _targetPosition;
            _targetPosition = ToGodot(pose.Position);
            _targetYaw = Heading(pose);
            if (!_hasFollowPose && sameSpace)
                _bodyYaw = _targetYaw;
        }
        _hasFollowPose = pose is not null;
        ClampNodeView();
        if (_clearancePending && (NodePanBounds is null || ApplyClearance())) _clearancePending = false;
        PlaceCamera();
    }

    internal void SetShake(Vector3 offset)
    {
        ShakeOffset = offset;
        if (_frame is not null) PlaceCamera();
    }

    internal void ReleaseFrame() => _frame = null;

    public void Update(double realDelta)
    {
        if (_frame is null)
            return;
        if (_hasFollowPose)
        {
            var weight = (float)(1d - Math.Exp(-FollowResponse * Math.Max(0d, realDelta)));
            Focus = Focus.Lerp(_targetPosition, weight);
            _bodyYaw = Mathf.LerpAngle(_bodyYaw, _targetYaw, weight);
        }
        PlaceCamera();
    }

    public void Orbit(float dx, float dy)
    {
        _orbitYaw = Mathf.Wrap(_orbitYaw - dx * OrbitSensitivity, -Mathf.Pi, Mathf.Pi);
        _pitch = Mathf.Clamp(_pitch + dy * OrbitSensitivity, MinimumPitch, MaximumPitch);
        PlaceCamera();
    }

    public void Zoom(float amount)
    {
        _distance = Mathf.Clamp(_distance * Mathf.Exp(-amount * ZoomStep), 8f,
            NodePanBounds is null ? ZoomCeiling : NodeMaximumDistance);
        PlaceCamera();
    }

    internal void PlaceAbsolute(Vector3 focus, float distance, float yaw, float pitch)
    {
        _clearancePending = false;
        Focus = focus;
        _targetPosition = focus;
        _bodyYaw = _targetYaw = 0;
        _orbitYaw = yaw;
        _pitch = pitch;
        _distance = distance;
        _hasFollowPose = false;
        PlaceCamera();
    }

    public void Pan(Vector3 horizontal)
    {
        if (_frame is null || _follow is not null || !_railwayPan && _frame.NodePanBounds is null)
            return;
        Focus += new Vector3(horizontal.X, 0f, horizontal.Z);
        ClampNodeView();
        PlaceCamera();
    }

    internal void MoveFocus(Vector3 focus)
    {
        _clearancePending = false;
        if (NodePanBounds is null) return;
        Focus = new(focus.X, Focus.Y, focus.Z);
        ClampNodeView();
        if (!ApplyClearance()) _clearancePending = true;
        PlaceCamera();
    }

    private bool ApplyClearance()
    {
        if (_frame is null) return true;
        var aim = OrbitPosition();
        var focusPick = _frame.Pick(ClearanceRay(Focus.X, Focus.Z));
        var cameraPick = _frame.Pick(ClearanceRay(aim.X, aim.Z));
        if (!Surface(focusPick, Focus, out var focusTop) || !Surface(cameraPick, aim, out var cameraTop)) return false;
        if (focusTop is { } focusY) Focus = new(Focus.X, focusY, Focus.Z);
        aim = OrbitPosition();
        if (cameraTop is { } top && top + TerrainClearance > aim.Y)
            Focus = new(Focus.X, Focus.Y + top + TerrainClearance - aim.Y, Focus.Z);
        return true;
    }

    private bool Surface(ScenePick pick, Vector3 point, out float? height)
    {
        height = null;
        switch (pick)
        {
            case ScenePick.NotReady:
                height = (float?)_frame!.DisplayTerrainHeight(point.X, point.Z);
                return height is not null;
            case ScenePick.Miss:
                return true;
            case ScenePick.Hit hit when hit.Value.Subject is PickSubject.Terrain:
                height = hit.Value.Point.Y;
                return true;
            case ScenePick.Hit hit when hit.Value.Subject is PickSubject.WorldObject:
                if (hit.Value.Object is not { } id || ObjectTop is not { } top) return false;
                height = top(id);
                return height is not null;
            case ScenePick.Hit:
                return true;
            default:
                return false;
        }
    }

    private Vector3 OrbitPosition()
    {
        var yaw = _bodyYaw + _orbitYaw;
        var horizontal = _distance * Mathf.Cos(_pitch);
        return Focus + new Vector3(Mathf.Sin(yaw) * horizontal, _distance * Mathf.Sin(_pitch),
            Mathf.Cos(yaw) * horizontal);
    }

    internal static SceneRay ClearanceRay(float x, float z)
    {
        const float span = 8000f;
        return new(new(x, span, z), new(0f, -1f, 0f), span * 2f);
    }

    private void ClampNodeView()
    {
        if (NodePanBounds is { } bounds)
        {
            Focus = new Vector3(Mathf.Clamp(Focus.X, bounds.Minimum.X, bounds.Maximum.X),
                Focus.Y, Mathf.Clamp(Focus.Z, bounds.Minimum.Y, bounds.Maximum.Y));
            _distance = Mathf.Min(_distance, NodeMaximumDistance);
        }
    }

    private void PlaceCamera()
    {
        var position = OrbitPosition();
        if (_frame?.DisplayTerrainHeight(position.X, position.Z) is { } terrain)
            position.Y = Mathf.Max(position.Y, (float)terrain + TerrainClearance);
        var offset = ShakeEnabled ? ShakeOffset : Vector3.Zero;
        Camera.Transform = new Transform3D(Basis.LookingAt(Focus - position, Vector3.Up), position + offset);
    }

    // 节点场景首次打开时看节点原点（站点和巢穴所在处），与 Scenes 首帧预加载一致；显示坐标 = 节点坐标 − Origin。
    private static Vector3 InitialFocus(ISceneFrame frame) => frame.NodePanBounds is not null
        ? new Vector3((float)-frame.Origin.X, (float)-frame.Origin.Y, (float)-frame.Origin.Z)
        : Vector3.Zero;

    private static float Heading(ScenePose pose)
    {
        var front = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, pose.Rotation);
        return Mathf.Atan2(front.X, front.Z);
    }

    private static Vector3 ToGodot(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
}
