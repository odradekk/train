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
    public void Node_free_camera_at_farthest_zoom_and_lowest_pitch_stays_inside_display_radius()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(bounds: new(new(-200, -200), new(200, 200))), null);
            camera.Orbit(90, -1000);
            camera.Zoom(-100);
            var offset = camera.Camera.Position - camera.Focus;
            Require(new Vector2(offset.X, offset.Z).Length() < 128,
                "节点自由相机最远最低时必须留在 64 米显示半径内。");
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Node_frames_pull_in_far_camera_from_railway_and_same_space_follow()
    {
        var camera = AttachCamera();
        try
        {
            var bounds = new ScenePanBounds(new(-200, -200), new(200, 200));
            camera.DisplayedFrame(Frame(), null, railwayPan: true);
            camera.Zoom(-100);
            Require(camera.Camera.Position.DistanceTo(camera.Focus) > 128, "必须先拉远线路相机。");
            camera.DisplayedFrame(Frame(space: 2, revision: 2, bounds: bounds), null);
            Require(camera.Camera.Position.DistanceTo(camera.Focus) < 128,
                "切入节点自由相机的第一帧必须收回已有远距离。");

            camera.DisplayedFrame(Frame(space: 2, revision: 3, bounds: bounds,
                poses: Poses((First, Pose(0, 0, 0)))), First);
            camera.Zoom(-100);
            Require(camera.Camera.Position.DistanceTo(camera.Focus) > 128, "节点内跟随必须仍能拉远。");
            camera.DisplayedFrame(Frame(space: 2, revision: 4, bounds: bounds), null);
            Require(camera.Camera.Position.DistanceTo(camera.Focus) < 128,
                "同一节点内从跟随转为自由相机也必须立即收回距离。");
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Reopening_a_closed_camera_applies_node_zoom_limit_on_the_first_frame()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(), null, railwayPan: true);
            camera.Zoom(-100);
            Require(camera.Camera.Position.DistanceTo(camera.Focus) > 128, "关闭前必须处于远距离视角。");
            var focus = camera.Focus;
            var transform = camera.Camera.Transform;
            camera.ReleaseFrame();
            camera.Update(1);
            camera.Pan(new(1000, 0, 1000));
            Near(camera.Focus, focus);
            Near(camera.Camera.Position, transform.Origin);
            Near(camera.Camera.Basis.Z, transform.Basis.Z);

            camera.DisplayedFrame(Frame(session: new(Guid.NewGuid()),
                bounds: new(new(-200, -200), new(200, 200))), null);
            Require(camera.Camera.Position.DistanceTo(camera.Focus) < 128,
                "新会话节点首帧必须重新应用自由相机缩放上限。");
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Railway_follow_and_frames_without_node_bounds_keep_existing_zoom_range()
    {
        var camera = AttachCamera();
        try
        {
            var frame = Frame(bounds: new(new(-200, -200), new(200, 200)),
                poses: Poses((First, Pose(0, 0, 0))));
            camera.DisplayedFrame(frame, null, railwayPan: true);
            camera.Zoom(-100);
            var farOffset = camera.Camera.Position - camera.Focus;
            Require(farOffset.Length() > 128, "线路平移不能受节点上限限制。");

            camera.DisplayedFrame(frame, First);
            camera.Zoom(-100);
            Near(camera.Camera.Position - camera.Focus, farOffset);
            camera.DisplayedFrame(Frame(revision: 2), null);
            camera.Zoom(-100);
            Near(camera.Camera.Position - camera.Focus, farOffset);
        }
        finally { camera.Free(); }
    }

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
            camera.DisplayedFrame(prepared, null);
            Require(camera.Camera.Position.Y > 200, "接入成功后必须使用新显示帧地形。");
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Orbit_state_changes_with_input_but_not_follow_smoothing_or_origin_shift()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(poses: Poses((First, Pose(0, 0, 0)))), First);
            camera.Orbit(40, 10);
            var yaw = camera.OrbitYawRadians;
            var pitch = camera.OrbitPitchRadians;
            camera.DisplayedFrame(Frame(revision: 2,
                poses: Poses((First, Pose(30, 0, 10, Mathf.Pi / 2)))), First);
            var beforeFollow = camera.Camera.Transform;
            camera.Update(0.05);
            Require(camera.Camera.Transform != beforeFollow, "跟随插值必须实际改变相机变换。");
            Require(camera.OrbitYawRadians == yaw && camera.OrbitPitchRadians == pitch,
                "跟随位置和车体朝向变化不能算作环绕输入响应。");

            var beforeShift = camera.Camera.Transform;
            camera.DisplayedFrame(Frame(revision: 3, origin: new(100, 0, -200),
                poses: Poses((First, Pose(-70, 0, 210, Mathf.Pi / 2)))), First);
            Require(camera.Camera.Transform != beforeShift, "原点平移必须实际改变相机变换。");
            Require(camera.OrbitYawRadians == yaw && camera.OrbitPitchRadians == pitch,
                "原点平移不能算作环绕输入响应。");

            camera.Orbit(20, 15);
            Require(camera.OrbitYawRadians != yaw && camera.OrbitPitchRadians != pitch,
                "真实环绕输入必须改变偏航和俯仰状态。");
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
    public void Space_transition_preserves_aligned_display_coordinates_and_follow_lag()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(poses: Poses((First, Pose(0, 0, 0)))), First);
            camera.DisplayedFrame(Frame(revision: 2, poses: Poses((First, Pose(30, 0, 10)))), First);
            camera.Update(0.05);
            camera.Orbit(90, 20);
            var focus = camera.Focus;
            var position = camera.Camera.Position;
            var forward = camera.Camera.Basis.Z;
            camera.DisplayedFrame(Frame(space: 2, revision: 3, origin: new(1000, 0, 0),
                poses: Poses((First, Pose(35, 0, 10)))), First);
            Near(camera.Focus, focus);
            Near(camera.Camera.Position, position);
            Near(camera.Camera.Basis.Z, forward);
            camera.Update(0.05);
            Require(camera.Focus.X > focus.X && camera.Focus.X < 35,
                "空间交接已保持显示坐标，后续运动必须继续平滑，不能在交接时重定位。");
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Closing_and_reopening_a_follow_camera_preserves_pose_and_smoothing()
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
            camera.ReleaseFrame();
            Near(camera.Focus, oldTarget + lag);
            Near(camera.Camera.Position - camera.Focus, offset);
            Near(camera.Camera.Basis.Z, forward);
            var newTarget = new Vector3(-700, 5, 900);
            camera.DisplayedFrame(Frame(revision: 3, session: new SceneSessionId(Guid.NewGuid()),
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
                poses: Poses((Second, Pose(0, 0, -100, Mathf.Pi / 2))),
                bounds: new(new(-2000, -2000), new(2000, 2000))), Second);
            camera.Update(0.05);
            Require(camera.Camera.Basis.Z.DistanceTo(initialForward) > 0.01,
                "相机必须跟随显示帧车体朝向变化。");
            var focus = camera.Focus;
            camera.Pan(new(1000, 0, 1000));
            Require(camera.Focus == focus, "跟随时不能进入节点自由平移。");
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

    [Test]
    public void Absolute_placement_is_immediate_and_clears_follow_smoothing()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(poses: Poses((First, Pose(0, 0, 0)))), First);
            camera.DisplayedFrame(Frame(revision: 2, poses: Poses((First, Pose(100, 0, 80, 1.2f)))), First);
            camera.Update(0.05);
            Require(camera.Focus.DistanceTo(new(100, 0, 80)) > 1, "测试必须先产生真实跟随残差。");
            camera.Orbit(50, 20);

            var focus = new Vector3(8, 12, -3);
            const float distance = 40, yaw = 0.7f, pitch = 0.4f;
            var expected = focus + new Vector3(Mathf.Sin(yaw) * distance * Mathf.Cos(pitch),
                distance * Mathf.Sin(pitch), Mathf.Cos(yaw) * distance * Mathf.Cos(pitch));
            camera.PlaceAbsolute(focus, distance, yaw, pitch);
            Near(camera.Focus, focus);
            Near(camera.Camera.Position, expected);
            Near(-camera.Camera.Basis.Z, (focus - expected).Normalized());
            camera.Update(0.5);
            Near(camera.Focus, focus);
            Near(camera.Camera.Position, expected);
            Near(-camera.Camera.Basis.Z, (focus - expected).Normalized());
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Minimap_focus_clamps_to_node_bounds_and_preserves_orbit_and_distance()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(bounds: new(new(-10, -20), new(30, 40))), null);
            camera.Orbit(70, 20);
            camera.Zoom(2);
            var offset = camera.Camera.Position - camera.Focus;
            var heading = camera.Camera.Basis.Z;
            camera.MoveFocus(new(1000, 999, -1000));
            Near(camera.Focus, new(30, 0, -20));
            Near(camera.Camera.Position - camera.Focus, offset);
            Near(camera.Camera.Basis.Z, heading);
            camera.MoveFocus(new(-1000, -999, 1000));
            Near(camera.Focus, new(-10, 0, 40));
            Near(camera.Camera.Position - camera.Focus, offset);
            camera.DisplayedFrame(Frame(revision: 2), null);
            var focus = camera.Focus;
            camera.MoveFocus(Vector3.Zero);
            Near(camera.Focus, focus);
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
