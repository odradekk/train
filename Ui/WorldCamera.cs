using Godot;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal partial class WorldCamera : Node3D
{
    internal const float DefaultDistance = 48f;
    internal const float MaximumDistance = 240f;
    internal const float NodeMaximumDistance = 96f;
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
    private ScenePanBounds? NodePanBounds => _follow is null && !_railwayPan ? _frame?.NodePanBounds : null;

    public Vector3 Focus { get; private set; }
    public Camera3D Camera { get; } = new() { Fov = 60f, Near = 0.1f, Far = 4000f };
    internal float OrbitYawRadians => _orbitYaw;
    internal float OrbitPitchRadians => _pitch;

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
        PlaceCamera();
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
        _distance = Mathf.Clamp(_distance * Mathf.Exp(-amount * 0.15f), 8f,
            NodePanBounds is null ? MaximumDistance : NodeMaximumDistance);
        PlaceCamera();
    }

    internal void PlaceAbsolute(Vector3 focus, float distance, float yaw, float pitch)
    {
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
        var yaw = _bodyYaw + _orbitYaw;
        var horizontalDistance = _distance * Mathf.Cos(_pitch);
        var position = Focus + new Vector3(Mathf.Sin(yaw) * horizontalDistance,
            _distance * Mathf.Sin(_pitch), Mathf.Cos(yaw) * horizontalDistance);
        if (_frame?.TerrainHeight(position.X, position.Z) is Lookup<double>.Found terrain)
            position.Y = Mathf.Max(position.Y, (float)terrain.Value + TerrainClearance);
        Camera.Transform = new Transform3D(Basis.LookingAt(Focus - position, Vector3.Up), position);
    }

    private static Vector3 InitialFocus(ISceneFrame frame) => frame.NodePanBounds is { } bounds
        ? new Vector3((bounds.Minimum.X + bounds.Maximum.X) / 2f, 0f,
            (bounds.Minimum.Y + bounds.Maximum.Y) / 2f)
        : Vector3.Zero;

    private static float Heading(ScenePose pose)
    {
        var front = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitZ, pose.Rotation);
        return Mathf.Atan2(front.X, front.Z);
    }

    private static Vector3 ToGodot(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
}
