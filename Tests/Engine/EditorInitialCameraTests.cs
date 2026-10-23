using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Scenes.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class EditorInitialCameraTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Opening_frames_occupied_bounds_once_and_raises_zoom_above_the_world_ceiling()
    {
        var window = TestScene.GetTree().Root;
        var scale = window.ContentScaleFactor;
        window.ContentScaleFactor = 1;
        EditorPreview? fixture = null;
        try
        {
            await new UiTestInput(TestScene).Frames(2);
            var titan = new GridBounds(new(-15, 0, -68), new(14, 37, 67));
            fixture = await Open(titan);
            var camera = fixture.Controller.View.CameraRig;
            var box = Box(titan);
            Require(camera.Focus.IsEqualApprox(box.Center), $"焦点应在占格中心 {box.Center}，实际 {camera.Focus}。");
            Require(Outside(camera.Camera.GlobalPosition, box),
                $"相机应在占格外，实际 {camera.Camera.GlobalPosition}，盒子 {box.Minimum}…{box.Maximum}。");
            Require(Mathf.IsEqualApprox(camera.OrbitYawRadians, 0.6f) && Mathf.IsEqualApprox(camera.OrbitPitchRadians, 0.55f),
                $"取景姿态应为偏航 0.6、俯仰 0.55，实际 {camera.OrbitYawRadians}、{camera.OrbitPitchRadians}。");
            CornersInside(camera, box, fixture.Controller.VisibleArea);
            camera.Orbit(80f, -40f);
            camera.Zoom(1f);
            var posed = Pose(camera);
            var displayed = fixture.Controller.Display!.DisplayedFrame;
            var preview = (SceneResult<IEditPreview>.Success)fixture.Editor.Preview(new EditOperation.Properties(
                new("取景后", ""), EditorPreviewSession.Draft.Specification))!;
            fixture.Editor.Apply(preview.Value, false);
            await FramesUntil(TestScene, () => fixture.Editor.State?.Snapshot.Stamp.Revision.Value > 0 &&
                fixture.Editor.State?.CurrentEvaluation is DesignEvaluation.Complete &&
                fixture.Controller.Display?.DisplayedFrame is { } frame && !ReferenceEquals(frame, displayed));
            var later = Pose(camera);
            Require(later.Focus.IsEqualApprox(posed.Focus) && later.Position.IsEqualApprox(posed.Position) &&
                Mathf.IsEqualApprox(later.Yaw, posed.Yaw) && Mathf.IsEqualApprox(later.Pitch, posed.Pitch),
                $"编辑帧后姿态必须保持不变。之前 {posed}，之后 {later}。");

            fixture.QueueFree();
            await new UiTestInput(TestScene).Frames(3);
            fixture = null;

            var giant = new GridBounds(new(-192, 0, -192), new(191, 255, 191));
            fixture = await Open(giant);
            camera = fixture.Controller.View.CameraRig;
            var distance = camera.Camera.GlobalPosition.DistanceTo(camera.Focus);
            Require(distance > WorldCamera.MaximumDistance, $"384×256×384 包络的取景距离应超过 240，实际 {distance}。");
            camera.Zoom(1);
            var inward = camera.Camera.GlobalPosition.DistanceTo(camera.Focus);
            Require(Mathf.IsEqualApprox(inward, distance * Mathf.Exp(-WorldCamera.ZoomStep)) && inward > WorldCamera.MaximumDistance,
                $"向里应乘 exp(-0.15) 且不被夹回 240。期望 {distance * Mathf.Exp(-WorldCamera.ZoomStep)}，实际 {inward}。");
            camera.Zoom(-1);
            var restored = camera.Camera.GlobalPosition.DistanceTo(camera.Focus);
            Require(Mathf.IsEqualApprox(restored, distance), $"向外回到取景距离应乘 exp(0.15)。期望 {distance}，实际 {restored}。");
            camera.Zoom(-1);
            var outward = camera.Camera.GlobalPosition.DistanceTo(camera.Focus);
            Require(Mathf.IsEqualApprox(outward, distance * Mathf.Exp(WorldCamera.ZoomStep)) && outward > WorldCamera.MaximumDistance,
                $"从取景距离向外应乘 exp(0.15) 且不被夹回 240。期望 {distance * Mathf.Exp(WorldCamera.ZoomStep)}，实际 {outward}。");
        }
        finally
        {
            fixture?.QueueFree();
            window.ContentScaleFactor = scale;
            await new UiTestInput(TestScene).Frames(3);
        }
    }

    private async Task<EditorPreview> Open(GridBounds bounds)
    {
        var fixture = new EditorPreview { Name = "EditorCameraFixture", OccupiedBounds = bounds };
        TestScene.AddChild(fixture);
        var center = Box(bounds).Center;
        await FramesUntil(TestScene, () => fixture.App.Editor?.Session is not null &&
            fixture.Controller.Display?.DisplayedFrame is not null &&
            fixture.Controller.View.CameraRig.Focus.IsEqualApprox(center));
        return fixture;
    }

    private static OccupiedBox Box(GridBounds bounds)
    {
        var minimum = new Vector3(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z);
        var maximum = minimum + new Vector3(bounds.Maximum.X - bounds.Minimum.X + 1,
            bounds.Maximum.Y - bounds.Minimum.Y + 1, bounds.Maximum.Z - bounds.Minimum.Z + 1);
        return new(minimum, maximum);
    }

    private static bool Outside(Vector3 point, OccupiedBox box) => point.X < box.Minimum.X || point.X > box.Maximum.X ||
        point.Y < box.Minimum.Y || point.Y > box.Maximum.Y || point.Z < box.Minimum.Z || point.Z > box.Maximum.Z;

    private static void CornersInside(WorldCamera camera, OccupiedBox box, Rect2 area)
    {
        var view = camera.Camera;
        for (var corner = 0; corner < 8; corner++)
        {
            var point = new Vector3((corner & 1) == 0 ? box.Minimum.X : box.Maximum.X,
                (corner & 2) == 0 ? box.Minimum.Y : box.Maximum.Y, (corner & 4) == 0 ? box.Minimum.Z : box.Maximum.Z);
            Require(!view.IsPositionBehind(point), $"角 {point} 在相机背后。");
            var screen = view.UnprojectPosition(point);
            Require(area.HasPoint(screen), $"角 {point} 投影到 {screen}，不在可见区域 {area} 内。");
        }
    }

    private static CameraPose Pose(WorldCamera camera) =>
        new(camera.Focus, camera.Camera.GlobalPosition, camera.OrbitYawRadians, camera.OrbitPitchRadians);

    private readonly record struct OccupiedBox(Vector3 Minimum, Vector3 Maximum)
    {
        public Vector3 Center => (Minimum + Maximum) * 0.5f;
    }

    private readonly record struct CameraPose(Vector3 Focus, Vector3 Position, float Yaw, float Pitch);
}
