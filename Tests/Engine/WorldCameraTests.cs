using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Train.Engine.Tests;

public class WorldCameraTests(Node testScene) : TestClass(testScene)
{
    private static readonly SceneSessionId Session = new(Guid.Parse("1af281b2-66c7-4212-a34b-3eb3e4172506"));
    private static readonly WorldObjectId First = new WorldObjectId.Instance(new InstanceId.Vehicle(new(1)));
    private static readonly WorldObjectId Second = new WorldObjectId.Instance(new InstanceId.Vehicle(new(2)));

    [Test]
    public void Node_pan_clamps_to_declared_bounds_and_missing_bounds_disable_pan()
    {
        var camera = AttachCamera();
        try
        {
            var bounds = new ScenePanBounds(new(-10, -20), new(30, 40));
            camera.DisplayedFrame(Frame(bounds: bounds), null);
            camera.Pan(new(1000, 2000, -1000));
            Near(camera.Focus, new(30, 0, -20));
            camera.Pan(new(-1000, -2000, 1000));
            Near(camera.Focus, new(-10, 0, 40));
            camera.DisplayedFrame(Frame(revision: 2), null);
            var focus = camera.Focus;
            var position = camera.Camera.Position;
            camera.Pan(new(500, 500, 500));
            Near(camera.Focus, focus);
            Near(camera.Camera.Position, position);
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Terrain_clearance_queries_only_the_successfully_displayed_frame()
    {
        var camera = AttachCamera();
        try
        {
            var displayed = Frame(height: new Lookup<double>.Found(100));
            camera.DisplayedFrame(displayed, null);
            Require(camera.Camera.Position.Y > 100, "相机必须略高于已显示帧地形。");
            var prepared = Frame(revision: 2, height: new Lookup<double>.Found(200));
            camera.Update(0.1);
            Require(camera.Camera.Position.Y < 200, "未显示候选帧不得改变地形约束。");
            camera.DisplayedFrame(prepared, null);
            Require(camera.Camera.Position.Y > 200, "接入成功后必须使用新显示帧地形。");
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Origin_shift_moves_focus_camera_and_follow_lag_together()
    {
        var camera = AttachCamera();
        try
        {
            var first = Frame(poses: Poses((First, Pose(10, 0, 20))));
            camera.DisplayedFrame(first, First);
            var focus = camera.Focus;
            var transform = camera.Camera.Transform;
            var shift = new NVector3(-100, 5, 200);
            camera.DisplayedFrame(Frame(revision: 2, @base: first.Update.Frame,
                shift: shift, origin: new(100, -5, -200),
                poses: Poses((First, Pose(-90, 5, 220)))), First);
            var offset = new Vector3(shift.X, shift.Y, shift.Z);
            Near(camera.Focus, focus + offset);
            Near(camera.Camera.Position, transform.Origin + offset);
            Near(camera.Camera.Basis.Z, transform.Basis.Z);
            camera.Update(0.2);
            Near(camera.Focus, focus + offset);
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Space_transition_maps_follow_lag_and_preserves_relative_view()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(poses: Poses((First, Pose(0, 0, 0)))), First);
            var oldTarget = new Vector3(30, 0, 10);
            camera.DisplayedFrame(Frame(revision: 2, poses: Poses((First, Pose(30, 0, 10)))), First);
            camera.Update(0.05);
            camera.Orbit(90, 20);
            var lag = camera.Focus - oldTarget;
            var offset = camera.Camera.Position - camera.Focus;
            var forward = camera.Camera.Basis.Z;
            var newTarget = new Vector3(-700, 5, 900);
            camera.DisplayedFrame(Frame(space: 2, revision: 3,
                poses: Poses((First, Pose(-700, 5, 900, Mathf.Pi / 2)))), First);
            Near(camera.Focus, newTarget + lag.Rotated(Vector3.Up, Mathf.Pi / 2));
            Near(camera.Camera.Position - camera.Focus, offset.Rotated(Vector3.Up, Mathf.Pi / 2));
            Near(camera.Camera.Basis.Z, forward.Rotated(Vector3.Up, Mathf.Pi / 2));
            Require(Math.Abs(camera.Camera.Position.DistanceTo(camera.Focus) - offset.Length()) < 0.001,
                "跨空间不能改变观察距离。");
            var before = camera.Focus.DistanceTo(newTarget);
            camera.Update(0.05);
            Require(camera.Focus.DistanceTo(newTarget) < before && camera.Focus.DistanceTo(newTarget) > 0,
                "跨空间应延续已有平滑残差。");
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Target_switch_is_smooth_and_backward_motion_keeps_body_heading()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(poses: Poses((First, Pose(0, 0, 0)))), First);
            var initialForward = camera.Camera.Basis.Z;
            camera.DisplayedFrame(Frame(revision: 2, poses: Poses((Second, Pose(0, 0, -50)))), Second);
            Near(camera.Focus, Vector3.Zero);
            camera.Update(0.05);
            Require(camera.Focus.Z < 0 && camera.Focus.Z > -50, "换车辆必须平滑而不是瞬移。");
            Near(camera.Camera.Basis.Z, initialForward);
            camera.DisplayedFrame(Frame(revision: 3,
                poses: Poses((Second, Pose(0, 0, -100, Mathf.Pi / 2)))), Second);
            camera.Update(0.05);
            Require(camera.Camera.Basis.Z.DistanceTo(initialForward) > 0.01,
                "相机必须跟随显示帧车体朝向变化。");
            camera.Pan(new(1000, 0, 1000));
            Require(camera.Focus.X == 0, "跟随时不能进入节点自由平移。");
        }
        finally { camera.Free(); }
    }

    [Test]
    public void New_session_reusing_space_id_does_not_subtract_unrelated_origins()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(origin: new(100000, 200000, 300000)), null);
            camera.DisplayedFrame(Frame(session: new(Guid.NewGuid()), origin: new(-100000, -200000, -300000)), null);
            Near(camera.Focus, Vector3.Zero);
        }
        finally { camera.Free(); }
    }

    private WorldCamera AttachCamera()
    {
        var camera = new WorldCamera();
        TestScene.AddChild(camera);
        return camera;
    }

    private static FixedSceneFrame Frame(ulong space = 1, ulong revision = 1,
        SceneFrameStamp? @base = null, NVector3 shift = default, ScenePoint origin = default,
        ScenePanBounds? bounds = null, ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>? poses = null,
        Lookup<double>? height = null, SceneSessionId? session = null) => new()
    {
        Update = new(@base, new(session ?? Session, new(1), new(space), revision), shift,
            [], [], new([], []), []),
        Origin = origin,
        NodePanBounds = bounds,
        Poses = poses ?? ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty,
        TerrainHeightResult = height ?? new Lookup<double>.Missing()
    };

    private static ScenePose Pose(float x, float y, float z, float yaw = 0) => new(new(x, y, z),
        NQuaternion.CreateFromAxisAngle(NVector3.UnitY, yaw));

    private static ImmutableDictionary<WorldObjectId, Lookup<ScenePose>> Poses(
        params (WorldObjectId Subject, ScenePose Pose)[] poses) => poses.ToImmutableDictionary(
            value => value.Subject, value => (Lookup<ScenePose>)new Lookup<ScenePose>.Found(value.Pose));

    private static void Near(Vector3 actual, Vector3 expected) => Require(actual.DistanceTo(expected) < 0.001,
        $"预期 {expected}，实际 {actual}。");
}
