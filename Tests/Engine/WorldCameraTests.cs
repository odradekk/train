using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.App.Measurement;
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
    public async Task Projection_envelope_recomputes_after_resize_and_contains_actual_far_corners()
    {
        var viewport = new SubViewport { Size = new(1600, 900) };
        TestScene.AddChild(viewport);
        var camera = new WorldCamera();
        viewport.AddChild(camera);
        try
        {
            double normalRadius = 0;
            foreach (var size in new Vector2I[] { new(1600, 900), new(3200, 900) })
            {
                viewport.Size = size;
                await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
                foreach (var keep in new[] { Camera3D.KeepAspectEnum.Height, Camera3D.KeepAspectEnum.Width })
                {
                    camera.Camera.KeepAspect = keep;
                    var radius = camera.TerrainViewRadiusCells;
                    if (keep == Camera3D.KeepAspectEnum.Height)
                    {
                        if (size.X == 1600) normalRadius = radius;
                        else Require(radius > normalRadius, "视口变宽后必须重算并扩大完整投影包络。");
                    }
                    foreach (var pitch in new[] { .15f, .55f, 1.35f })
                        foreach (var yaw in new[] { 0f, 1.5f, 3f, 4.5f })
                        {
                            camera.SetShake(new(.15f, 0, 0));
                            camera.PlaceAbsolute(Vector3.Zero, WorldCamera.MaximumDistance, yaw, pitch);
                            foreach (var x in new[] { 0, size.X })
                                foreach (var y in new[] { 0, size.Y })
                                {
                                    var corner = camera.Camera.ProjectPosition(new(x, y), camera.Camera.Far);
                                    Require(new Vector2(corner.X - camera.Focus.X, corner.Z - camera.Focus.Z).Length() <= radius,
                                        "实际相机的整个远平面必须落在地形水平包络内。");
                                }
                        }
                }
            }
        }
        finally { viewport.Free(); }
    }

    [Test]
    public void Measurement_low_orbit_uses_node_and_railway_limits_and_can_restore_default_distance()
    {
        var camera = AttachCamera();
        try
        {
            foreach (var nodeFree in new[] { true, false })
            {
                camera.DisplayedFrame(Frame(bounds: nodeFree ? NodeBounds : null), null, railwayPan: !nodeFree);
                foreach (var farthest in new[] { true, false })
                {
                    MeasurementRun.ConfigureLowOrbitCamera(camera, farthest);
                    var expectedDistance = farthest ? nodeFree ? 96 : 240 : 48;
                    var initialYaw = camera.OrbitYawRadians;
                    for (var step = 0; step < 8; step++)
                    {
                        camera.Orbit(100, 0);
                        Require(Math.Abs(camera.Camera.Position.DistanceTo(camera.Focus) - expectedDistance) < .001,
                            "测量环绕必须保持节点96格/线路240格限幅，并能恢复默认48格。");
                        Require(Math.Abs(camera.OrbitPitchRadians - .15f) < .0001,
                            "最低俯仰请求必须在持续环绕期间保持0.15。");
                    }
                    Require(Math.Abs(camera.OrbitYawRadians - initialYaw) > .1,
                        "环绕输入必须实际改变相对偏航。");
                }
            }
        }
        finally { camera.Free(); }
    }

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
    public void Distant_terrain_clearance_uses_only_the_current_displayed_frame()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(displayHeight: 100), null);
            Require(Math.Abs(camera.Camera.Position.Y - 101) < 0.001,
                "精确地形缺失时，相机必须位于已显示远景地形上方一格。");
            var prepared = Frame(revision: 2, displayHeight: 200);
            camera.Update(0.1);
            Require(Math.Abs(camera.Camera.Position.Y - 101) < 0.001,
                "尚未显示的帧不能改变相机净空。");
            camera.DisplayedFrame(prepared, null);
            Require(Math.Abs(camera.Camera.Position.Y - 201) < 0.001,
                "新显示帧必须立即更新远景地形净空。");
            camera.DisplayedFrame(Frame(revision: 3, displayHeight: 50), null);
            Require(Math.Abs(camera.Camera.Position.Y - 51) < 0.001,
                "远景地形降低后不能保留先前帧的高度。");
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
    public void Continuous_follow_tracks_displayed_translation_without_lag_and_keeps_turn_smoothing()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(poses: Poses((First, Pose(0, 0, 0)))), First);
            var forward = camera.Camera.Basis.Z;
            var position = new Vector3(300, 2, -100);
            camera.DisplayedFrame(Frame(revision: 2,
                poses: Poses((First, Pose(position.X, position.Y, position.Z, Mathf.Pi / 2)))), First);
            Near(camera.Focus, position);
            Near(camera.Camera.Basis.Z, forward);
            camera.Update(1d / 60);
            Near(camera.Focus, position);
            Require(camera.Camera.IsPositionInFrustum(camera.ToGlobal(position)),
                "车辆显示位置快速推进后，跟随目标必须仍在相机视锥内。");
            var horizontalForward = new Vector2(camera.Camera.Basis.Z.X, camera.Camera.Basis.Z.Z).Normalized();
            Require(camera.Camera.Basis.Z.DistanceTo(forward) > 0.01 &&
                horizontalForward.DistanceTo(Vector2.Right) > 0.01,
                "平移随车不能取消车体转向的平滑。");
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Continuous_follow_stays_aligned_through_a_space_switch_and_origin_shift()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(poses: Poses((First, Pose(20, 0, 10)))), First);
            camera.DisplayedFrame(Frame(revision: 2, poses: Poses((First, Pose(30, 0, 10)))), First);
            camera.Update(1d / 60);
            Near(camera.Focus, new(30, 0, 10));
            var transform = camera.Camera.Transform;
            var shift = new Vector3(-100, 2, 200);
            var aligned = new Vector3(-70, 2, 210);
            camera.DisplayedFrame(Frame(space: 2, revision: 3, origin: new(1000, 0, 0),
                poses: Poses((First, Pose(aligned.X, aligned.Y, aligned.Z)))), First, coordinateShift: shift);
            Near(camera.Focus, aligned);
            Near(camera.Camera.Position, transform.Origin + shift);
            Near(camera.Camera.Basis.Z, transform.Basis.Z);
            camera.Update(1d / 60);
            Near(camera.Focus, aligned);
            var motion = new Vector3(6, 0, 8);
            camera.DisplayedFrame(Frame(space: 2, revision: 4, origin: new(1000, 0, 0),
                poses: Poses((First, Pose(aligned.X + motion.X, aligned.Y, aligned.Z + motion.Z)))), First);
            Near(camera.Focus - aligned, motion);
            camera.Update(1d / 60);
            Near(camera.Focus - aligned, motion);
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
            camera.DisplayedFrame(Frame(revision: 2, poses: Poses((Second, Pose(30, 0, 10)))), Second);
            camera.Update(0.05);
            camera.Orbit(90, 20);
            var focus = camera.Focus;
            var position = camera.Camera.Position;
            var forward = camera.Camera.Basis.Z;
            camera.DisplayedFrame(Frame(space: 2, revision: 3, origin: new(1000, 0, 0),
                poses: Poses((Second, Pose(35, 0, 10)))), Second);
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
            camera.DisplayedFrame(Frame(revision: 2, poses: Poses((Second, Pose(30, 0, 10)))), Second);
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
                poses: Poses((Second, Pose(-700, 5, 900, Mathf.Pi / 2)))), Second);
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
            camera.DisplayedFrame(Frame(revision: 2, poses: Poses((Second, Pose(100, 0, 80, 1.2f)))), Second);
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
    public void Building_top_lifts_focus_above_a_lower_hit_and_keeps_the_orbit()
    {
        var camera = AttachCamera();
        try
        {
            camera.ObjectTop = id => id.Value == 1 ? 40f : null;
            camera.DisplayedFrame(Frame(bounds: NodeBounds, pick: Building(1, 2)), null);
            var offset = camera.Camera.Position - camera.Focus;
            camera.MoveFocus(new(10, 999, 20));
            Near(camera.Focus, new(10, 40, 20));
            Near(camera.Camera.Position - camera.Focus, offset);
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Taller_neighbor_lifts_the_camera_one_cell_above_its_top()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(bounds: NodeBounds), null);
            var offset = camera.Camera.Position - camera.Focus;
            var focus = new Vector3(10, 0, 20);
            camera.ObjectTop = id => id.Value == 2 ? 70f : 40f;
            camera.DisplayedFrame(Frame(bounds: NodeBounds, picks: ImmutableDictionary<SceneRay, ScenePick>.Empty
                .Add(WorldCamera.ClearanceRay(focus.X, focus.Z), Building(1, 2))
                .Add(WorldCamera.ClearanceRay(focus.X + offset.X, focus.Z + offset.Z), Building(2, 3))), null);
            camera.MoveFocus(focus);
            Near(camera.Focus, new(focus.X, 71f - offset.Y, focus.Z));
            Near(camera.Camera.Position, new(focus.X + offset.X, 71f, focus.Z + offset.Z));
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Terrain_hit_uses_the_surface_and_a_miss_keeps_the_height()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(bounds: NodeBounds, pick: Ground(12)), null);
            camera.MoveFocus(new(10, 0, 20));
            Near(camera.Focus, new(10, 12, 20));
            camera.DisplayedFrame(Frame(revision: 2, bounds: NodeBounds), null);
            camera.MoveFocus(new(4, 0, -6));
            Near(camera.Focus, new(4, 12, -6));
        }
        finally { camera.Free(); }
    }

    [Test]
    public void NotReady_or_a_missing_top_lifts_on_a_later_frame()
    {
        var camera = AttachCamera();
        try
        {
            camera.ObjectTop = _ => 40f;
            camera.DisplayedFrame(Frame(bounds: NodeBounds, pick: new ScenePick.NotReady()), null);
            camera.MoveFocus(new(10, 0, 20));
            Near(camera.Focus, new(10, 0, 20));
            camera.DisplayedFrame(Frame(revision: 2, bounds: NodeBounds, pick: Building(1, 2)), null);
            Near(camera.Focus, new(10, 40, 20));

            float? top = null;
            camera.ObjectTop = _ => top;
            camera.DisplayedFrame(Frame(revision: 3, bounds: NodeBounds, pick: Building(1, 2)), null);
            camera.MoveFocus(new(-8, 0, 6));
            Near(camera.Focus, new(-8, 40, 6));
            top = 18f;
            camera.DisplayedFrame(Frame(revision: 4, bounds: NodeBounds, pick: Building(1, 2)), null);
            Near(camera.Focus, new(-8, 18, 6));
        }
        finally { camera.Free(); }
    }

    [Test]
    public void Minimap_NotReady_uses_distant_terrain_and_retries_when_height_is_missing()
    {
        var camera = AttachCamera();
        try
        {
            camera.DisplayedFrame(Frame(bounds: NodeBounds, pick: new ScenePick.NotReady()), null);
            camera.MoveFocus(new(10, 0, 20));
            Near(camera.Focus, new(10, 0, 20));
            camera.DisplayedFrame(Frame(revision: 2, bounds: NodeBounds,
                pick: new ScenePick.NotReady(), displayHeight: 40), null);
            Near(camera.Focus, new(10, 40, 20));
            Require(camera.Camera.Position.Y > 40, "待就绪拾取必须以当前远景高度抬高相机。");
            camera.MoveFocus(new(-8, 0, 6));
            Near(camera.Focus, new(-8, 40, 6));
            camera.DisplayedFrame(Frame(revision: 3, bounds: NodeBounds,
                pick: new ScenePick.NotReady(), displayHeight: 80), null);
            camera.MoveFocus(new(4, 0, -6));
            Near(camera.Focus, new(4, 80, -6));
            Require(camera.Camera.Position.Y > 80, "小地图跳转必须使用新显示帧的远景高度。");
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
        Lookup<double>? height = null, SceneSessionId? session = null, ScenePick? pick = null,
        ImmutableDictionary<SceneRay, ScenePick>? picks = null, double? displayHeight = null) => new()
    {
        Update = new(@base, new(session ?? Session, new(1), new(space), revision), shift,
            [], [], new([], []), []),
        Origin = origin,
        NodePanBounds = bounds,
        Poses = poses ?? ImmutableDictionary<WorldObjectId, Lookup<ScenePose>>.Empty,
        TerrainHeightResult = height ?? new Lookup<double>.Missing(),
        DisplayTerrainHeightResult = displayHeight is { } value ? new Lookup<double>.Found(value) : null,
        PickResult = pick ?? new ScenePick.Miss(),
        Picks = picks ?? ImmutableDictionary<SceneRay, ScenePick>.Empty
    };

    private static readonly ScenePanBounds NodeBounds = new(new(-100, -100), new(100, 100));

    private static ScenePick Building(ulong id, float y) => new ScenePick.Hit(new(new(Session, new(1), new(1), 1), new(id),
        new PickSubject.WorldObject(new WorldObjectId.Instance(new InstanceId.Building(new(id))), null, null),
        default, GridFace.PositiveY, new(0, y, 0), NVector3.UnitY, 1, []));

    private static ScenePick Ground(float y) => new ScenePick.Hit(new(new(Session, new(1), new(1), 1), null,
        new PickSubject.Terrain(new WorldLocation.Node(new(1))),
        default, GridFace.PositiveY, new(0, y, 0), NVector3.UnitY, 1, []));

    private static ScenePose Pose(float x, float y, float z, float yaw = 0) => new(new(x, y, z),
        NQuaternion.CreateFromAxisAngle(NVector3.UnitY, yaw));

    private static ImmutableDictionary<WorldObjectId, Lookup<ScenePose>> Poses(
        params (WorldObjectId Subject, ScenePose Pose)[] poses) => poses.ToImmutableDictionary(
            value => value.Subject, value => (Lookup<ScenePose>)new Lookup<ScenePose>.Found(value.Pose));

    private static void Near(Vector3 actual, Vector3 expected) => Require(actual.DistanceTo(expected) < 0.001,
        $"预期 {expected}，实际 {actual}。");
}
