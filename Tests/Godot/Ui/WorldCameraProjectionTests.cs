using Godot;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Ui;

namespace Train.Godot.Tests.Ui;

[TestClass]
public sealed class WorldCameraProjectionTests
{
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
