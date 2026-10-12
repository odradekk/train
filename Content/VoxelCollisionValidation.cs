// 属于内容层的碰撞定义校验模块。
// 校验局部碰撞盒的边长转换为单精度浮点数后是否仍为正。

using Train.Content.Voxels;

namespace Train.Content;

/// <summary>校验局部碰撞盒的各条边长在表现使用的单精度浮点数中仍为正。</summary>
/// <remarks>碰撞表现使用 float 尺寸，而定义保存 double 尺寸；过小的正边长可能在转换后变为零。有限性和单位格范围由 <see cref="VoxelCollision"/> 先行校验，此处逐条检查边长，不计算三边相乘的体积。</remarks>
/// <example>边长 1e-50 在 double 中为正，转换为 float 后为零，内容加载器因此拒绝该局部盒；见 <c>VoxelCollisionDefinitionTests.Adapter_underflow_sizes_reject_publish_and_exclude_the_mod_package</c>。</example>
internal static class VoxelCollisionValidation
{
    /// <summary>判断已校验的碰撞定义是否满足单精度边长要求。</summary>
    /// <param name="collision">非空的碰撞定义；局部盒的几何合法性已由定义层校验。</param>
    /// <returns>none 和 full_box 返回 true；local_box 的三条边长转换为 float 后均大于零时返回 true，否则返回 false。</returns>
    /// <remarks>none 的 true 表示该定义无需局部盒精度检查，并不表示它具有碰撞体积。</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="collision"/> 为 <see langword="null"/>。</exception>
    /// <example>任一边长为 1e-50 或 double.Epsilon 的局部盒都会被内容加载器拒绝；见 <c>VoxelCollisionDefinitionTests.Adapter_underflow_sizes_reject_publish_and_exclude_the_mod_package</c>。</example>
    public static bool CanDerivePositiveVolume(VoxelCollision collision)
    {
        ArgumentNullException.ThrowIfNull(collision);
        if (collision.Kind is VoxelCollisionKind.None or VoxelCollisionKind.FullBox)
            return true;
        return TryDeriveSize(collision.SizeX, out _) &&
               TryDeriveSize(collision.SizeY, out _) &&
               TryDeriveSize(collision.SizeZ, out _);
    }

    /// <summary>将已通过定义校验的边长转换为 float，并判断转换结果是否仍大于零。</summary>
    /// <param name="size">以一个体素格边长为单位的尺寸；调用方须先确认它是单位格范围内的有限正数。</param>
    /// <param name="derived">始终输出 size 转换为 float 的结果；返回 false 时也保留该转换结果。</param>
    /// <returns>转换结果严格大于零时返回 true，否则返回 false，包括过小的正数转换为零的情况。</returns>
    /// <remarks>本方法只比较转换结果是否大于零，不独立校验有限性或单位格范围。</remarks>
    /// <example>1e-50 转换后输出 0，并返回 false；见 <c>VoxelCollisionDefinitionTests.Adapter_underflow_sizes_reject_publish_and_exclude_the_mod_package</c>。</example>
    public static bool TryDeriveSize(double size, out float derived)
    {
        derived = (float)size;
        return derived > 0f;
    }
}
