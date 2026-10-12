namespace Train.Content.Composites;

public enum VehicleSizeClass
{
    Small = 0,
    Medium = 1,
    Large = 2,
    ExtraLarge = 3,
    Titan = 4
}

public static class VehicleSize
{
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
