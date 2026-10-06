// 属于内容层的蓝图定义模块。
// 定义当前蓝图 JSON 格式使用的车辆尺寸档位及其线格式。
namespace Train.Core.Content.Composites;

/// <summary>表示车辆蓝图声明的尺寸档位。</summary>
/// <example><see cref="VehicleSizeClass.Small"/> 在线格式中写作 <c>small</c>。</example>
public enum VehicleSizeClass
{
    /// <summary>小型车辆档位。</summary>
    Small = 0,
    /// <summary>中型车辆档位。</summary>
    Medium = 1,
    /// <summary>大型车辆档位。</summary>
    Large = 2,
    /// <summary>超大型车辆档位。</summary>
    ExtraLarge = 3,
    /// <summary>泰坦车辆档位。</summary>
    Titan = 4
}

/// <summary>转换车辆尺寸档位与当前 JSON 内容格式中的固定字符串。</summary>
/// <example><c>TryParse("extra_large", out size)</c> 得到 <see cref="VehicleSizeClass.ExtraLarge"/>，反向编码仍为 <c>extra_large</c>。</example>
public static class VehicleSize
{
    /// <summary>尝试精确解析当前内容格式中的车辆尺寸档位。</summary>
    /// <param name="value">区分大小写且不修剪的线格式文本；<see langword="null"/> 无效。</param>
    /// <param name="size">成功时为解析出的档位；失败时为 <see cref="VehicleSizeClass.Small"/>。</param>
    /// <returns>文本与五种已定义线格式之一完全相同时返回 <see langword="true"/>。</returns>
    public static bool TryParse(string? value, out VehicleSizeClass size)
    {
        size = value switch
        {
            "small" => VehicleSizeClass.Small,
            "medium" => VehicleSizeClass.Medium,
            "large" => VehicleSizeClass.Large,
            "extra_large" => VehicleSizeClass.ExtraLarge,
            "titan" => VehicleSizeClass.Titan,
            _ => default
        };
        return value is "small" or "medium" or "large" or "extra_large" or "titan";
    }

    /// <summary>返回车辆尺寸档位在当前内容格式中的字符串。</summary>
    /// <param name="size">要编码的车辆尺寸档位。</param>
    /// <returns>区分大小写的线格式文本。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> 不是已定义档位。</exception>
    public static string ToWire(VehicleSizeClass size) => size switch
    {
        VehicleSizeClass.Small => "small",
        VehicleSizeClass.Medium => "medium",
        VehicleSizeClass.Large => "large",
        VehicleSizeClass.ExtraLarge => "extra_large",
        VehicleSizeClass.Titan => "titan",
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Unknown vehicle size class.")
    };
}
