// 属于内容层的体素定义模块。
// 定义体素布局、表现和碰撞共同使用的离散正交朝向。
using System.Collections.ObjectModel;

namespace Train.Content.Voxels;

/// <summary>表示体素局部坐标或世界坐标的一条有向正交轴。</summary>
public enum VoxelAxis
{
    /// <summary>X 轴正方向。</summary>
    PositiveX,
    /// <summary>X 轴负方向。</summary>
    NegativeX,
    /// <summary>Y 轴正方向。</summary>
    PositiveY,
    /// <summary>Y 轴负方向。</summary>
    NegativeY,
    /// <summary>Z 轴正方向。</summary>
    PositiveZ,
    /// <summary>Z 轴负方向。</summary>
    NegativeZ
}

/// <summary>表示体素从局部单位格到世界单位格的右手正交朝向。</summary>
/// <example>水平旋转见 <c>VoxelCubeUvTests.Yaw90_rotates_top_uvs_instead_of_reusing_world_face_corners</c>；倾斜朝向见 <c>VoxelCubeUvTests.Tilt_maps_world_top_through_the_local_side_now_pointing_up</c>。</example>
public readonly record struct VoxelOrientation
{
    private static readonly VoxelAxis[] Axes = Enum.GetValues<VoxelAxis>();
    private static readonly IReadOnlyList<VoxelOrientation> AllOrientations = BuildAll();
    private static readonly IReadOnlyList<VoxelOrientation> FourWayOrientations = Array.AsReadOnly(new[]
    {
        new VoxelOrientation(VoxelAxis.PositiveX, VoxelAxis.PositiveY),
        new VoxelOrientation(VoxelAxis.PositiveZ, VoxelAxis.PositiveY),
        new VoxelOrientation(VoxelAxis.NegativeX, VoxelAxis.PositiveY),
        new VoxelOrientation(VoxelAxis.NegativeZ, VoxelAxis.PositiveY)
    });

    /// <summary>获取局部 X 正轴在世界坐标中的方向。</summary>
    public VoxelAxis Right { get; }
    /// <summary>获取局部 Y 正轴在世界坐标中的方向。</summary>
    public VoxelAxis Up { get; }
    /// <summary>获取由 Right × Up 得到的局部 Z 正轴世界方向。</summary>
    public VoxelAxis Forward { get; }
    /// <summary>获取三个轴是否构成有效右手正交基。</summary>
    public bool IsOrthogonal => ArePerpendicular(Right, Up) && Forward == Cross(Right, Up);

    /// <summary>获取局部轴与世界轴相同的默认朝向。</summary>
    public static VoxelOrientation Identity { get; } = new(VoxelAxis.PositiveX, VoxelAxis.PositiveY);
    /// <summary>获取绕世界 Y 轴旋转 90 度的朝向。</summary>
    public static VoxelOrientation Yaw90 { get; } = new(VoxelAxis.PositiveZ, VoxelAxis.PositiveY);
    /// <summary>获取全部二十四个右手正交朝向。</summary>
    public static IReadOnlyList<VoxelOrientation> All => AllOrientations;
    /// <summary>获取保持局部 Y 轴向上的四个水平朝向。</summary>
    public static IReadOnlyList<VoxelOrientation> FourWay => FourWayOrientations;

    /// <summary>用局部 X、Y 正轴在世界中的方向构造朝向。</summary>
    /// <param name="right">局部 X 正轴的世界方向。</param>
    /// <param name="up">局部 Y 正轴的世界方向。</param>
    /// <exception cref="ArgumentException">两个轴平行或反向，不能构成正交基。</exception>
    /// <exception cref="ArgumentOutOfRangeException">right 或 up 不是有效的有向轴枚举值。</exception>
    public VoxelOrientation(VoxelAxis right, VoxelAxis up)
    {
        if (!ArePerpendicular(right, up))
            throw new ArgumentException("Right and up axes must be perpendicular.");
        Right = right;
        Up = up;
        Forward = Cross(right, up);
    }

    /// <summary>把一条局部有向轴转换为世界有向轴。</summary>
    /// <param name="localAxis">局部有向轴。</param>
    /// <returns>对应的世界有向轴。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="localAxis"/> 不是有效枚举值。</exception>
    public VoxelAxis Transform(VoxelAxis localAxis) => localAxis switch
    {
        VoxelAxis.PositiveX => Right,
        VoxelAxis.NegativeX => Opposite(Right),
        VoxelAxis.PositiveY => Up,
        VoxelAxis.NegativeY => Opposite(Up),
        VoxelAxis.PositiveZ => Forward,
        VoxelAxis.NegativeZ => Opposite(Forward),
        _ => throw new ArgumentOutOfRangeException(nameof(localAxis), localAxis, "Unknown voxel axis.")
    };

    /// <summary>把一条世界有向轴转换为局部有向轴。</summary>
    /// <param name="worldAxis">世界有向轴。</param>
    /// <returns>对应的局部有向轴。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="worldAxis"/> 不是当前朝向产生的有效轴。</exception>
    public VoxelAxis InverseTransform(VoxelAxis worldAxis)
    {
        foreach (var local in Axes)
        {
            if (Transform(local) == worldAxis)
                return local;
        }

        throw new ArgumentOutOfRangeException(nameof(worldAxis), worldAxis, "World axis is not produced by this orientation.");
    }

    /// <summary>把局部单位格中的坐标转换到同一世界单位格。</summary>
    /// <param name="localX">局部 X 坐标，单位为体素。</param>
    /// <param name="localY">局部 Y 坐标，单位为体素。</param>
    /// <param name="localZ">局部 Z 坐标，单位为体素。</param>
    /// <returns>旋转后的世界格内坐标，单位为体素。</returns>
    /// <exception cref="ArgumentOutOfRangeException">任一输入不是有限数。</exception>
    public (double X, double Y, double Z) ToWorldCell(double localX, double localY, double localZ)
    {
        if (!IsFinite(localX) || !IsFinite(localY) || !IsFinite(localZ))
            throw new ArgumentOutOfRangeException(nameof(localX), "Local coordinates must be finite.");
        return (
            AlongWorld(VoxelAxis.PositiveX, localX, localY, localZ),
            AlongWorld(VoxelAxis.PositiveY, localX, localY, localZ),
            AlongWorld(VoxelAxis.PositiveZ, localX, localY, localZ));
    }

    /// <summary>把世界单位格中的坐标转换到定义的局部单位格。</summary>
    /// <param name="worldX">世界格内 X 坐标，单位为体素。</param>
    /// <param name="worldY">世界格内 Y 坐标，单位为体素。</param>
    /// <param name="worldZ">世界格内 Z 坐标，单位为体素。</param>
    /// <returns>逆旋转后的局部格内坐标，单位为体素。</returns>
    /// <exception cref="ArgumentOutOfRangeException">任一输入不是有限数。</exception>
    public (double X, double Y, double Z) ToLocalCell(double worldX, double worldY, double worldZ)
    {
        if (!IsFinite(worldX) || !IsFinite(worldY) || !IsFinite(worldZ))
            throw new ArgumentOutOfRangeException(nameof(worldX), "World-relative coordinates must be finite.");
        return (
            AlongLocal(VoxelAxis.PositiveX, worldX, worldY, worldZ),
            AlongLocal(VoxelAxis.PositiveY, worldX, worldY, worldZ),
            AlongLocal(VoxelAxis.PositiveZ, worldX, worldY, worldZ));
    }

    /// <summary>返回 <c>right_up</c> 形式的小写线格式。</summary>
    public override string ToString() => $"{ToWire(Right)}_{ToWire(Up)}";

    /// <summary>不区分大小写地解析一个二十四朝向线格式。</summary>
    /// <param name="value">候选文本；<see langword="null"/> 无效。</param>
    /// <param name="orientation">成功时为解析结果；失败时为默认结构值。</param>
    /// <returns>文本匹配一个有效朝向时返回 <see langword="true"/>。</returns>
    public static bool TryParse(string? value, out VoxelOrientation orientation)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                orientation = candidate;
                return true;
            }
        }

        orientation = default;
        return false;
    }

    private static IReadOnlyList<VoxelOrientation> BuildAll()
    {
        var orientations = new List<VoxelOrientation>(24);
        foreach (var right in Axes)
        {
            foreach (var up in Axes)
            {
                if (ArePerpendicular(right, up))
                    orientations.Add(new VoxelOrientation(right, up));
            }
        }

        return new ReadOnlyCollection<VoxelOrientation>(orientations);
    }

    private static bool ArePerpendicular(VoxelAxis first, VoxelAxis second) =>
        AxisDimension(first) != AxisDimension(second);

    private static int AxisDimension(VoxelAxis axis) => axis switch
    {
        VoxelAxis.PositiveX or VoxelAxis.NegativeX => 0,
        VoxelAxis.PositiveY or VoxelAxis.NegativeY => 1,
        VoxelAxis.PositiveZ or VoxelAxis.NegativeZ => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };

    private static VoxelAxis Cross(VoxelAxis first, VoxelAxis second)
    {
        var (x1, y1, z1) = Components(first);
        var (x2, y2, z2) = Components(second);
        return FromComponents(y1 * z2 - z1 * y2, z1 * x2 - x1 * z2, x1 * y2 - y1 * x2);
    }

    private static (int X, int Y, int Z) Components(VoxelAxis axis) => axis switch
    {
        VoxelAxis.PositiveX => (1, 0, 0),
        VoxelAxis.NegativeX => (-1, 0, 0),
        VoxelAxis.PositiveY => (0, 1, 0),
        VoxelAxis.NegativeY => (0, -1, 0),
        VoxelAxis.PositiveZ => (0, 0, 1),
        VoxelAxis.NegativeZ => (0, 0, -1),
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };

    private double AlongWorld(VoxelAxis world, double localX, double localY, double localZ)
    {
        if (Right == world)
            return localX;
        if (Opposite(Right) == world)
            return 1 - localX;
        if (Up == world)
            return localY;
        if (Opposite(Up) == world)
            return 1 - localY;
        if (Forward == world)
            return localZ;
        if (Opposite(Forward) == world)
            return 1 - localZ;
        throw new ArgumentOutOfRangeException(nameof(world), world, "World axis is not produced by this orientation.");
    }

    private double AlongLocal(VoxelAxis local, double worldX, double worldY, double worldZ)
    {
        var world = Transform(local);
        var value = world switch
        {
            VoxelAxis.PositiveX or VoxelAxis.NegativeX => worldX,
            VoxelAxis.PositiveY or VoxelAxis.NegativeY => worldY,
            VoxelAxis.PositiveZ or VoxelAxis.NegativeZ => worldZ,
            _ => throw new ArgumentOutOfRangeException(nameof(local), local, "Unknown voxel axis.")
        };
        return world is VoxelAxis.NegativeX or VoxelAxis.NegativeY or VoxelAxis.NegativeZ
            ? 1 - value
            : value;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static VoxelAxis Opposite(VoxelAxis axis) => axis switch
    {
        VoxelAxis.PositiveX => VoxelAxis.NegativeX,
        VoxelAxis.NegativeX => VoxelAxis.PositiveX,
        VoxelAxis.PositiveY => VoxelAxis.NegativeY,
        VoxelAxis.NegativeY => VoxelAxis.PositiveY,
        VoxelAxis.PositiveZ => VoxelAxis.NegativeZ,
        VoxelAxis.NegativeZ => VoxelAxis.PositiveZ,
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };

    private static VoxelAxis FromComponents(int x, int y, int z) => (x, y, z) switch
    {
        (1, 0, 0) => VoxelAxis.PositiveX,
        (-1, 0, 0) => VoxelAxis.NegativeX,
        (0, 1, 0) => VoxelAxis.PositiveY,
        (0, -1, 0) => VoxelAxis.NegativeY,
        (0, 0, 1) => VoxelAxis.PositiveZ,
        (0, 0, -1) => VoxelAxis.NegativeZ,
        _ => throw new ArgumentException("The axes are not orthogonal.")
    };

    private static string ToWire(VoxelAxis axis) => axis switch
    {
        VoxelAxis.PositiveX => "px",
        VoxelAxis.NegativeX => "nx",
        VoxelAxis.PositiveY => "py",
        VoxelAxis.NegativeY => "ny",
        VoxelAxis.PositiveZ => "pz",
        VoxelAxis.NegativeZ => "nz",
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, "Unknown voxel axis.")
    };
}

/// <summary>
/// 表示立方体在世界坐标中的一个面方向。
/// </summary>
public enum VoxelFaceNormal
{
    /// <summary>朝向 X 轴正方向的面。</summary>
    PositiveX,
    /// <summary>朝向 X 轴负方向的面。</summary>
    NegativeX,
    /// <summary>朝向 Y 轴正方向的面。</summary>
    PositiveY,
    /// <summary>朝向 Y 轴负方向的面。</summary>
    NegativeY,
    /// <summary>朝向 Z 轴正方向的面。</summary>
    PositiveZ,
    /// <summary>朝向 Z 轴负方向的面。</summary>
    NegativeZ
}
