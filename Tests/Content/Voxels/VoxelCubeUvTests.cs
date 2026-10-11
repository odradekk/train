// Core 内容测试验证方块面的朝向和 UV 映射契约。
// 使用正交朝向和固定面顶点，无需世界或引擎。

using Train.Content.Voxels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Content.Tests.Voxels;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:voxels")]
[TestCategory("scope:content")]
public sealed class VoxelCubeUvTests
{
    [TestMethod]
    public void Identity_top_keeps_the_established_local_u_along_x_and_v_toward_negative_z()
    {
        Assert.AreEqual((0f, 1f), VoxelCubeUv.ForWorldFace(VoxelOrientation.Identity, VoxelFaceNormal.PositiveY, 0, 1, 0));
        Assert.AreEqual((0f, 0f), VoxelCubeUv.ForWorldFace(VoxelOrientation.Identity, VoxelFaceNormal.PositiveY, 0, 1, 1));
        Assert.AreEqual((1f, 0f), VoxelCubeUv.ForWorldFace(VoxelOrientation.Identity, VoxelFaceNormal.PositiveY, 1, 1, 1));
        Assert.AreEqual((1f, 1f), VoxelCubeUv.ForWorldFace(VoxelOrientation.Identity, VoxelFaceNormal.PositiveY, 1, 1, 0));
    }

    [TestMethod]
    public void Yaw90_rotates_top_uvs_instead_of_reusing_world_face_corners()
    {
        var identity = VoxelCubeUv.TopCorners(VoxelOrientation.Identity);
        var yaw90 = VoxelCubeUv.TopCorners(VoxelOrientation.Yaw90);
        Assert.AreNotEqual(identity[(0, 1, 0)], yaw90[(0, 1, 0)]);
        Assert.AreNotEqual(identity[(0, 1, 1)], yaw90[(0, 1, 1)]);
        Assert.AreEqual((0f, 0f), yaw90[(0, 1, 0)]);
        Assert.AreEqual((1f, 0f), yaw90[(0, 1, 1)]);
        Assert.AreEqual((1f, 1f), yaw90[(1, 1, 1)]);
        Assert.AreEqual((0f, 1f), yaw90[(1, 1, 0)]);
    }

    [TestMethod]
    public void Identity_positive_x_keeps_the_original_ccw_world_face_uvs()
    {
        Assert.AreEqual((0f, 1f), VoxelCubeUv.ForWorldFace(VoxelOrientation.Identity, VoxelFaceNormal.PositiveX, 1, 0, 0));
        Assert.AreEqual((0f, 0f), VoxelCubeUv.ForWorldFace(VoxelOrientation.Identity, VoxelFaceNormal.PositiveX, 1, 1, 0));
        Assert.AreEqual((1f, 0f), VoxelCubeUv.ForWorldFace(VoxelOrientation.Identity, VoxelFaceNormal.PositiveX, 1, 1, 1));
        Assert.AreEqual((1f, 1f), VoxelCubeUv.ForWorldFace(VoxelOrientation.Identity, VoxelFaceNormal.PositiveX, 1, 0, 1));
    }

    [TestMethod]
    public void Tilt_maps_world_top_through_the_local_side_now_pointing_up()
    {
        var tilt = new VoxelOrientation(VoxelAxis.PositiveX, VoxelAxis.PositiveZ);
        Assert.AreEqual(VoxelAxis.NegativeZ, tilt.InverseTransform(VoxelAxis.PositiveY));
        var identityTop = VoxelCubeUv.TopCorners(VoxelOrientation.Identity);
        var tilted = VoxelCubeUv.TopCorners(tilt);
        Assert.AreEqual(identityTop[(0, 1, 0)], tilted[(0, 1, 0)]);
        Assert.AreEqual((0f, 1f), tilted[(0, 1, 0)]);
        Assert.AreEqual((0f, 0f), tilted[(0, 1, 1)]);
        Assert.AreEqual((1f, 0f), tilted[(1, 1, 1)]);
        Assert.AreEqual((1f, 1f), tilted[(1, 1, 0)]);
        Assert.AreNotEqual(
            VoxelCubeUv.ForWorldFace(VoxelOrientation.Identity, VoxelFaceNormal.NegativeZ, 0, 1, 0),
            VoxelCubeUv.ForWorldFace(tilt, VoxelFaceNormal.NegativeZ, 0, 1, 0));
    }
}
