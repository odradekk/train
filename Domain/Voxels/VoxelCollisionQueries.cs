// 属于旧体素世界层。
// 将内容碰撞形状投影到旧世界坐标，并执行旧世界碰撞查询。
using Train.Core.Content.Voxels;

namespace Train.Domain.Voxels;

/// <summary>把内容定义的碰撞形状映射到旧体素世界，供旧碰撞查询与表现使用。</summary>
/// <remarks>坐标以一个体素格为一单位；局部盒随有效的正交朝向旋转，边界仅接触不算重叠。</remarks>
/// <example>偏心盒的命中区域随朝向变化；见 <c>VoxelLocalBoxTests.Asymmetric_rotation_moves_the_hit_region_for_all_24_orientations</c>。</example>
public static class VoxelCollisionQueries
{
    /// <summary>判断指定世界格中的碰撞形状是否与探测盒存在正体积重叠。</summary>
    /// <param name="collision">非空的内容碰撞定义；none 不命中，full_box 覆盖整格。</param>
    /// <param name="cell">放置体素的世界格整数坐标。</param>
    /// <param name="orientation">调用方已验证的正交朝向，用于把探测盒转到局部坐标。</param>
    /// <param name="probe">世界坐标中的探测盒，各边界以体素格为单位。</param>
    /// <returns>两个体积严格重叠时为 true；分离或只接触边界时为 false。</returns>
    /// <example>偏心盒只在覆盖范围命中；见 <c>VoxelLocalBoxTests.Identity_local_box_hits_only_inside_the_eccentric_volume</c>。</example>
    public static bool OverlapsProbe(this VoxelCollision collision, WorldVoxelCoord cell, VoxelOrientation orientation, VoxelCollisionBox probe)
    {
        if (collision.Kind == VoxelCollisionKind.None)
            return false;
        if (collision.Kind == VoxelCollisionKind.FullBox)
            return VoxelCollisionBox.ForCell(cell).Overlaps(probe);

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        foreach (var worldX in new[] { probe.MinX - cell.X, probe.MaxX - cell.X })
        foreach (var worldY in new[] { probe.MinY - cell.Y, probe.MaxY - cell.Y })
        foreach (var worldZ in new[] { probe.MinZ - cell.Z, probe.MaxZ - cell.Z })
        {
            var (localX, localY, localZ) = orientation.ToLocalCell(worldX, worldY, worldZ);
            minX = Math.Min(minX, localX);
            minY = Math.Min(minY, localY);
            minZ = Math.Min(minZ, localZ);
            maxX = Math.Max(maxX, localX);
            maxY = Math.Max(maxY, localY);
            maxZ = Math.Max(maxZ, localZ);
        }

        return minX < collision.OffsetX + collision.SizeX && maxX > collision.OffsetX &&
               minY < collision.OffsetY + collision.SizeY && maxY > collision.OffsetY &&
               minZ < collision.OffsetZ + collision.SizeZ && maxZ > collision.OffsetZ;
    }

    /// <summary>计算碰撞形状在旧世界坐标中的中心及三轴边长。</summary>
    /// <param name="collision">非空的内容碰撞定义。</param>
    /// <param name="cell">体素所在世界格的整数坐标。</param>
    /// <param name="orientation">调用方已验证的正交朝向，用于旋转局部中心并排列边长。</param>
    /// <param name="centerX">输出世界中心 X，以体素格为单位；无碰撞时为零。</param>
    /// <param name="centerY">输出世界中心 Y，以体素格为单位；无碰撞时为零。</param>
    /// <param name="centerZ">输出世界中心 Z，以体素格为单位；无碰撞时为零。</param>
    /// <param name="sizeX">输出世界 X 轴边长，以体素格为单位；无碰撞时为零。</param>
    /// <param name="sizeY">输出世界 Y 轴边长，以体素格为单位；无碰撞时为零。</param>
    /// <param name="sizeZ">输出世界 Z 轴边长，以体素格为单位；无碰撞时为零。</param>
    /// <returns>none 返回 false 并将输出清零；full_box 或 local_box 返回 true。</returns>
    /// <example>朝向下的中心和边长映射见 <c>VoxelLocalBoxTests.Asymmetric_rotation_moves_the_hit_region_for_all_24_orientations</c>。</example>
    public static bool TryDerivedExtents(this VoxelCollision collision, WorldVoxelCoord cell, VoxelOrientation orientation,
        out double centerX, out double centerY, out double centerZ, out double sizeX, out double sizeY, out double sizeZ)
    {
        centerX = centerY = centerZ = sizeX = sizeY = sizeZ = 0;
        if (collision.Kind == VoxelCollisionKind.None)
            return false;
        if (collision.Kind == VoxelCollisionKind.FullBox)
        {
            centerX = cell.X + 0.5;
            centerY = cell.Y + 0.5;
            centerZ = cell.Z + 0.5;
            sizeX = sizeY = sizeZ = 1;
            return true;
        }

        var local = orientation.ToWorldCell(collision.OffsetX + collision.SizeX * 0.5,
            collision.OffsetY + collision.SizeY * 0.5, collision.OffsetZ + collision.SizeZ * 0.5);
        centerX = cell.X + local.X;
        centerY = cell.Y + local.Y;
        centerZ = cell.Z + local.Z;
        sizeX = SizeAlong(collision, orientation.InverseTransform(VoxelAxis.PositiveX));
        sizeY = SizeAlong(collision, orientation.InverseTransform(VoxelAxis.PositiveY));
        sizeZ = SizeAlong(collision, orientation.InverseTransform(VoxelAxis.PositiveZ));
        return true;
    }

    /// <summary>沿有向局部轴选择边长，正负方向共享同一长度。</summary>
    private static double SizeAlong(VoxelCollision collision, VoxelAxis localAxis) => localAxis switch
    {
        VoxelAxis.PositiveX or VoxelAxis.NegativeX => collision.SizeX,
        VoxelAxis.PositiveY or VoxelAxis.NegativeY => collision.SizeY,
        VoxelAxis.PositiveZ or VoxelAxis.NegativeZ => collision.SizeZ,
        _ => throw new ArgumentOutOfRangeException(nameof(localAxis), localAxis, "Unknown voxel axis.")
    };
}
