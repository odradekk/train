namespace Train.Domain.Composites;

public enum VehicleSizeClass
{
    Small = 0,
    Medium = 1,
    Large = 2,
    ExtraLarge = 3,
    Titan = 4
}

public readonly record struct VehicleSizeDimensions(int Length, int Width, int Height);

public static class VehicleSize
{
    public static VehicleSizeDimensions Small { get; } = new(8, 5, 6);
    public static VehicleSizeDimensions Medium { get; } = new(12, 7, 6);
    public static VehicleSizeDimensions Large { get; } = new(16, 9, 8);
    public static VehicleSizeDimensions ExtraLarge { get; } = new(20, 11, 8);
    public static VehicleSizeDimensions Titan { get; } = new(24, 13, 10);

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

    public static VehicleSizeDimensions Dimensions(VehicleSizeClass size) => size switch
    {
        VehicleSizeClass.Small => Small,
        VehicleSizeClass.Medium => Medium,
        VehicleSizeClass.Large => Large,
        VehicleSizeClass.ExtraLarge => ExtraLarge,
        VehicleSizeClass.Titan => Titan,
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Unknown vehicle size class.")
    };
}

public readonly record struct VehicleEnvelopeBox(
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ);

public static class VehicleEnvelope
{
    public static bool TryFromContacts(
        VehicleSizeClass size,
        IReadOnlyList<CompositeContactAnchor> contacts,
        out VehicleEnvelopeBox box)
    {
        box = default;
        if (contacts is null || contacts.Count < 2)
            return false;
        if (!Enum.IsDefined(size))
            return false;

        var midX = (contacts[0].X + contacts[1].X) / 2.0;
        var midZ = (contacts[0].Z + contacts[1].Z) / 2.0;
        if (!double.IsFinite(midX) || !double.IsFinite(midZ))
            return false;

        var dimensions = VehicleSize.Dimensions(size);
        box = new VehicleEnvelopeBox(
            midX - dimensions.Length / 2.0,
            0,
            midZ - dimensions.Width / 2.0,
            midX + dimensions.Length / 2.0,
            dimensions.Height,
            midZ + dimensions.Width / 2.0);
        return true;
    }

    public static bool ContainsCell(VehicleEnvelopeBox box, LocalVoxelCoord cell) =>
        cell.X >= box.MinX && cell.X + 1 <= box.MaxX &&
        cell.Y >= box.MinY && cell.Y + 1 <= box.MaxY &&
        cell.Z >= box.MinZ && cell.Z + 1 <= box.MaxZ;
}
