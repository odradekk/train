using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:composites")]
public sealed class FragmentGridSnapTests
{
    [TestMethod]
    public void Rotation_snap_uses_minimum_angle_and_ordered_axis_pair_ties()
    {
        Assert.AreEqual(24, FragmentGridSnap.OrderedOrientations.Count);
        Assert.AreEqual(VoxelOrientation.Identity, FragmentGridSnap.OrderedOrientations[0]);
        Assert.AreEqual(
            new VoxelOrientation(VoxelAxis.PositiveX, VoxelAxis.PositiveY),
            FragmentGridSnap.SnapRotation(VoxelOrientation.Identity));
        Assert.AreEqual(
            VoxelOrientation.Identity,
            FragmentGridSnap.SnapRotation((1, 0, 1), (0, 1, 0)));
        Assert.AreEqual(
            VoxelOrientation.Yaw90,
            FragmentGridSnap.SnapRotation((0.4, 0, 1), (0, 1, 0)));
    }

    [TestMethod]
    public void Anchor_snap_takes_the_larger_integer_on_half_cells()
    {
        Assert.AreEqual(8, FragmentGridSnap.SnapCoordinate(8));
        Assert.AreEqual(8, FragmentGridSnap.SnapCoordinate(8.4));
        Assert.AreEqual(9, FragmentGridSnap.SnapCoordinate(8.5));
        Assert.AreEqual(9, FragmentGridSnap.SnapCoordinate(8.6));
        Assert.AreEqual(-1, FragmentGridSnap.SnapCoordinate(-1.5));
        Assert.AreEqual(new WorldVoxelCoord(9, 8, 8), FragmentGridSnap.SnapAnchor(8.5, 8, 8));
    }

    [TestMethod]
    public void Search_offsets_are_manhattan_radius_1_in_distance_then_xyz_order()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                new WorldVoxelCoord(0, 0, 0),
                new WorldVoxelCoord(-1, 0, 0),
                new WorldVoxelCoord(0, -1, 0),
                new WorldVoxelCoord(0, 0, -1),
                new WorldVoxelCoord(0, 0, 1),
                new WorldVoxelCoord(0, 1, 0),
                new WorldVoxelCoord(1, 0, 0)
            },
            FragmentGridSnap.SearchOffsets.ToArray());
    }

    [TestMethod]
    public void Cell_transform_maps_the_whole_occupancy_not_independent_rounds()
    {
        var identity = VoxelOrientation.Identity;
        var flipped = new VoxelOrientation(VoxelAxis.NegativeX, VoxelAxis.PositiveY);
        Assert.AreEqual(
            new WorldVoxelCoord(11, 9, 9),
            FragmentGridSnap.TransformCell(identity, new LocalVoxelCoord(3, 1, 1), new WorldVoxelCoord(8, 8, 8)));
        Assert.AreEqual(
            new WorldVoxelCoord(4, 8, 7),
            FragmentGridSnap.TransformCell(flipped, new LocalVoxelCoord(3, 0, 0), new WorldVoxelCoord(8, 8, 8)));
        Assert.AreEqual(
            VoxelOrientation.Yaw90,
            FragmentGridSnap.Compose(VoxelOrientation.Identity, VoxelOrientation.Yaw90));
    }
}
