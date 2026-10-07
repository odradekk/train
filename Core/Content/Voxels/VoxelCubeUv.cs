// 属于内容层的体素定义模块。
// 计算立方体表现按体素朝向旋转后的纹理坐标。
namespace Train.Core.Content.Voxels;

/// <summary>按体素朝向计算立方体各面顶点的纹理坐标。</summary>
/// <remarks>输入坐标相对所在体素格的原点，以一格边长为单位；先转换到体素局部坐标，再按局部面的约定计算 UV，使纹理随体素一起旋转。</remarks>
/// <example>未旋转时，顶部顶点 (0, 1, 0) 对应 UV (0, 1)；见 <c>VoxelCubeUvTests.Identity_top_keeps_the_established_local_u_along_x_and_v_toward_negative_z</c>。</example>
public static class VoxelCubeUv
{
    /// <summary>把以世界坐标轴表示的格内顶点转换为体素局部坐标轴下的格内顶点。</summary>
    /// <param name="orientation">体素局部轴在世界中的方向；调用方须提供 <see cref="VoxelOrientation.IsOrthogonal"/> 为 true 的朝向。</param>
    /// <param name="dx">顶点相对所在体素格原点的世界 X 坐标，只能为 0 或 1。</param>
    /// <param name="dy">顶点相对所在体素格原点的世界 Y 坐标，只能为 0 或 1。</param>
    /// <param name="dz">顶点相对所在体素格原点的世界 Z 坐标，只能为 0 或 1。</param>
    /// <returns>相对体素局部格原点的 (X, Y, Z)，三个分量均为 0 或 1。</returns>
    /// <remarks>只检查顶点分量，不检查朝向的正交性。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">任一顶点分量不是 0 或 1。</exception>
    /// <example>倾斜朝向下的顶点转换参与侧面纹理转到顶部的计算；见 <c>VoxelCubeUvTests.Tilt_maps_world_top_through_the_local_side_now_pointing_up</c>。</example>
    public static (int X, int Y, int Z) LocalCorner(VoxelOrientation orientation, int dx, int dy, int dz)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dx, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dx, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dy, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dy, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dz, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dz, 1);
        return (
            AxisComponent(orientation.Right, dx, dy, dz),
            AxisComponent(orientation.Up, dx, dy, dz),
            AxisComponent(orientation.Forward, dx, dy, dz));
    }

    /// <summary>计算指定世界朝向的立方体面上一个格内顶点的 UV。</summary>
    /// <param name="orientation">体素局部轴在世界中的方向；调用方须提供有效的正交朝向。</param>
    /// <param name="worldNormal">待映射面在世界坐标中的有向轴法线。</param>
    /// <param name="dx">面顶点相对所在体素格原点的世界 X 坐标，只能为 0 或 1。</param>
    /// <param name="dy">面顶点相对所在体素格原点的世界 Y 坐标，只能为 0 或 1。</param>
    /// <param name="dz">面顶点相对所在体素格原点的世界 Z 坐标，只能为 0 或 1。</param>
    /// <returns>对应局部面纹理的 (U, V)，两个分量均取 0 或 1。</returns>
    /// <remarks>调用方须保证顶点位于指定面；本方法不检查这一条件。未旋转时，正 Y 面的 U 沿 X 正方向增大，V 沿 Z 负方向增大。</remarks>
    /// <exception cref="ArgumentOutOfRangeException">任一顶点分量不是 0 或 1、面法线不是有效枚举值，或朝向无法将该法线映射到局部轴。</exception>
    /// <example>未旋转时，正 X 面顶点 (1, 0, 0) 对应 UV (0, 1)；见 <c>VoxelCubeUvTests.Identity_positive_x_keeps_the_original_ccw_world_face_uvs</c>。</example>
    public static (float U, float V) ForWorldFace(
        VoxelOrientation orientation,
        VoxelFaceNormal worldNormal,
        int dx,
        int dy,
        int dz)
    {
        var local = LocalCorner(orientation, dx, dy, dz);
        var localFace = orientation.InverseTransform(ToAxis(worldNormal));
        return ForLocalFace(localFace, local.X, local.Y, local.Z);
    }

    /// <summary>计算世界正 Y 面的四个格内顶点及其 UV。</summary>
    /// <param name="orientation">体素局部轴在世界中的方向；调用方须提供有效的正交朝向。</param>
    /// <returns>每次调用生成的新字典，通过只读接口返回；键为 Y 等于 1、X 和 Z 各取 0 或 1 的四个格内顶点，值为对应的 (U, V)。</returns>
    /// <exception cref="ArgumentOutOfRangeException">朝向无法将世界正 Y 法线映射到局部轴。</exception>
    /// <example>水平旋转 90 度后，世界顶部顶点 (0, 1, 0) 的 UV 从 (0, 1) 变为 (0, 0)；见 <c>VoxelCubeUvTests.Yaw90_rotates_top_uvs_instead_of_reusing_world_face_corners</c>。</example>
    public static IReadOnlyDictionary<(int X, int Y, int Z), (float U, float V)> TopCorners(VoxelOrientation orientation)
    {
        var corners = new Dictionary<(int X, int Y, int Z), (float U, float V)>();
        foreach (var dx in new[] { 0, 1 })
        foreach (var dz in new[] { 0, 1 })
            corners[(dx, 1, dz)] = ForWorldFace(orientation, VoxelFaceNormal.PositiveY, dx, 1, dz);
        return corners;
    }

    /// <summary>按局部面法线及格内顶点计算该面的固定 UV 映射。</summary>
    /// <remarks>各面从外部观察、按表现层的逆时针顶点顺序排列时，UV 依次为 (0, 1)、(0, 0)、(1, 0)、(1, 1)，以保持各面的纹理方向一致。</remarks>
    private static (float U, float V) ForLocalFace(VoxelAxis localFace, int localX, int localY, int localZ) =>
        localFace switch
        {
            VoxelAxis.PositiveY => (localX, 1 - localZ),
            VoxelAxis.NegativeY => (localZ, 1 - localX),
            VoxelAxis.PositiveX => (localZ, 1 - localY),
            VoxelAxis.NegativeX => (localY, 1 - localZ),
            VoxelAxis.PositiveZ => (localY, 1 - localX),
            VoxelAxis.NegativeZ => (localX, 1 - localY),
            _ => throw new ArgumentOutOfRangeException(nameof(localFace), localFace, "Unknown voxel axis.")
        };

    /// <summary>沿指定有向轴取格内顶点的分量；负方向使用 1 减去对应坐标，使旋转后的顶点仍位于同一单位格内。</summary>
    private static int AxisComponent(VoxelAxis axis, int dx, int dy, int dz) => axis switch
    {
        VoxelAxis.PositiveX => dx,
        VoxelAxis.NegativeX => 1 - dx,
        VoxelAxis.PositiveY => dy,
        VoxelAxis.NegativeY => 1 - dy,
        VoxelAxis.PositiveZ => dz,
        VoxelAxis.NegativeZ => 1 - dz,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };

    /// <summary>把立方体面的法线转换为同方向的有向轴；未知枚举值抛出 ArgumentOutOfRangeException。</summary>
    private static VoxelAxis ToAxis(VoxelFaceNormal normal) => normal switch
    {
        VoxelFaceNormal.PositiveX => VoxelAxis.PositiveX,
        VoxelFaceNormal.NegativeX => VoxelAxis.NegativeX,
        VoxelFaceNormal.PositiveY => VoxelAxis.PositiveY,
        VoxelFaceNormal.NegativeY => VoxelAxis.NegativeY,
        VoxelFaceNormal.PositiveZ => VoxelAxis.PositiveZ,
        VoxelFaceNormal.NegativeZ => VoxelAxis.NegativeZ,
        _ => throw new ArgumentOutOfRangeException(nameof(normal), normal, "Unknown face normal.")
    };
}
