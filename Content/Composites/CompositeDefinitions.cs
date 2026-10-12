// 属于内容层的蓝图定义模块。
// 表示蓝图的不可变显示信息、尺寸档位和体素布局。
using System.Collections.ObjectModel;
using Train.Content.Voxels;

namespace Train.Content.Composites;

/// <summary>表示蓝图布局中一个体素格使用的体素定义与朝向。</summary>
/// <example><c>(1,0,0)</c> 处可放置朝向为 <c>px_py</c> 的 <c>core:control-unit</c>。</example>
public sealed class CompositeVoxelSpec
{
    /// <summary>获取体素格相对蓝图原点的整数坐标，单位为体素。</summary>
    public LocalVoxelCoord Cell { get; }
    /// <summary>获取该体素格引用的体素定义身份。</summary>
    public VoxelDefinitionId DefinitionId { get; }
    /// <summary>获取体素在蓝图局部坐标系中的离散朝向。</summary>
    public VoxelOrientation Orientation { get; }

    /// <summary>创建一个蓝图体素规格。</summary>
    public CompositeVoxelSpec(LocalVoxelCoord cell, VoxelDefinitionId definitionId, VoxelOrientation orientation)
    {
        Cell = cell;
        DefinitionId = definitionId;
        Orientation = orientation;
    }
}

/// <summary>表示一个固定蓝图版本的显示信息、车辆尺寸档位与非空体素布局。</summary>
/// <remarks>同一布局中每个局部坐标只允许出现一次；体素定义存在性和允许朝向由目录候选校验。</remarks>
/// <example>四个不同坐标可分别放置结构、动力、控制和货舱体素；见 <c>CompositeContentTests.Built_in_blueprint_publishes_layout_and_hand_calculated_attributes</c>。</example>
public sealed class CompositeDefinition
{
    /// <summary>获取蓝图定义的稳定身份。</summary>
    public CompositeDefinitionId Id { get; }
    /// <summary>获取去除首尾空白后的显示名称，长度为 1–100 个 Unicode 标量值。</summary>
    public string Name { get; }
    /// <summary>获取去除首尾空白后的说明，长度为 1–2000 个 Unicode 标量值。</summary>
    public string Description { get; }
    /// <summary>获取蓝图声明的单车尺寸档位。</summary>
    public VehicleSizeClass SizeClass { get; }
    /// <summary>获取按局部坐标确定性排序的非空体素布局只读副本。</summary>
    public IReadOnlyList<CompositeVoxelSpec> Voxels { get; }

    /// <summary>创建蓝图定义，并规范化显示文本、校验布局及保存坐标排序后的副本。</summary>
    /// <param name="id">蓝图定义的稳定身份。</param>
    /// <param name="name">显示名称；去除首尾空白后须有 1–100 个 Unicode 标量值。</param>
    /// <param name="description">说明；去除首尾空白后须有 1–2000 个 Unicode 标量值。</param>
    /// <param name="sizeClass">单车尺寸档位。</param>
    /// <param name="voxels">至少含一个体素且局部坐标互不重复的布局。</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/>、<paramref name="description"/>、<paramref name="voxels"/> 或其中的元素为 null。</exception>
    /// <exception cref="ArgumentException">身份或显示文本无效，或布局为空、含重复坐标。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeClass"/> 不是已定义的车辆尺寸档位。</exception>
    public CompositeDefinition(
        CompositeDefinitionId id,
        string name,
        string description,
        VehicleSizeClass sizeClass,
        IReadOnlyList<CompositeVoxelSpec> voxels)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        if (string.IsNullOrEmpty(id.Namespace) || string.IsNullOrEmpty(id.LocalId))
            throw new ArgumentException("CompositeDefinitionId cannot be blank.", nameof(id));
        if (!Enum.IsDefined(sizeClass))
            throw new ArgumentOutOfRangeException(nameof(sizeClass), sizeClass, "Unknown vehicle size class.");
        if (voxels.Count == 0)
            throw new ArgumentException("A composite layout must contain at least one voxel.", nameof(voxels));
        if (voxels.Any(item => item is null))
            throw new ArgumentNullException(nameof(voxels), "A composite layout cannot contain null voxels.");
        if (voxels.GroupBy(item => item.Cell).Any(group => group.Skip(1).Any()))
            throw new ArgumentException("A composite layout cannot contain duplicate coordinates.", nameof(voxels));

        Id = id;
        Name = VoxelDefinition.NormalizeDisplayText(name, VoxelDefinition.NameMinLength, VoxelDefinition.NameMaxLength, nameof(name));
        Description = VoxelDefinition.NormalizeDisplayText(
            description,
            VoxelDefinition.DescriptionMinLength,
            VoxelDefinition.DescriptionMaxLength,
            nameof(description));
        SizeClass = sizeClass;
        Voxels = new ReadOnlyCollection<CompositeVoxelSpec>(voxels.OrderBy(item => item.Cell).ToArray());
    }
}
