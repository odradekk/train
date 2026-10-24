using Godot;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class WorldCameraProjectionTests
{
    [TestMethod]
    [DataRow(16f / 9, false)]
    [DataRow(32f / 9, false)]
    [DataRow(16f / 9, true)]
    [DataRow(32f / 9, true)]
    public void Full_projection_envelope_bounds_every_camera_orientation(float aspect, bool keepWidth)
    {
        const float far = 4000;
        var projection = Projection.CreatePerspective(60, aspect, .1f, far, keepWidth);
        var radius = WorldCamera.ProjectionRadiusCells(projection, far);
        var tangent = Math.Tan(Math.PI / 6);
        var halfWidth = far * tangent * (keepWidth ? 1 : aspect);
        var halfHeight = far * tangent * (keepWidth ? 1 / aspect : 1);
        var farCornerDistance = Math.Sqrt(far * far + halfWidth * halfWidth + halfHeight * halfHeight);
        // 旋转保持角点长度，水平投影不长于三维向量；三角不等式覆盖所有绕转和俯仰。
        var envelope = farCornerDistance + 240 + .15;
        Assert.IsGreaterThanOrEqualTo(envelope, radius);
        Assert.IsLessThan(envelope + 256, radius);
        if (aspect == 16f / 9 && !keepWidth) Assert.AreEqual(6656, radius);
    }

    [TestMethod]
    public void Off_center_projection_includes_the_farther_side_instead_of_only_the_top_right_corner()
    {
        const float near = .1f, far = 4000;
        var projection = Projection.CreateFrustum(-.18f, .03f, -.12f, .02f, near, far);
        var radius = WorldCamera.ProjectionRadiusCells(projection, far);
        var envelope = far * Math.Sqrt(1 + Math.Pow(.18 / near, 2) + Math.Pow(.12 / near, 2)) + 240 + .15;
        Assert.IsGreaterThanOrEqualTo(envelope, radius);
        Assert.IsLessThan(envelope + 256, radius);
    }
}
