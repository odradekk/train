using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Train.Domain.Voxels;

public static class VoxelTags
{
    public const string NaturalResource = "natural_resource";
}

public enum VoxelOrientationConstraint
{
    None = 0,
    FourWay = 1,
    All24 = 2
}

public enum VoxelPresentationKind
{
    Cube = 0
}

public enum VoxelCollisionKind
{
    None = 0,
    FullBox = 1,
    LocalBox = 2
}

public sealed class VoxelPresentation
{
    public VoxelPresentationKind Kind { get; }
    public string IconPath { get; }
    public string TopPath { get; }
    public string SidePath { get; }
    public string BottomPath { get; }

    public VoxelPresentation(
        VoxelPresentationKind kind,
        string iconPath,
        string topPath,
        string sidePath,
        string bottomPath)
    {
        if (kind != VoxelPresentationKind.Cube)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only cube presentation is supported.");
        ArgumentException.ThrowIfNullOrEmpty(iconPath);
        ArgumentException.ThrowIfNullOrEmpty(topPath);
        ArgumentException.ThrowIfNullOrEmpty(sidePath);
        ArgumentException.ThrowIfNullOrEmpty(bottomPath);
        Kind = kind;
        IconPath = iconPath;
        TopPath = topPath;
        SidePath = sidePath;
        BottomPath = bottomPath;
    }
}

public sealed class VoxelCollision
{
    public VoxelCollisionKind Kind { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }
    public double OffsetZ { get; }
    public double SizeX { get; }
    public double SizeY { get; }
    public double SizeZ { get; }

    public VoxelCollision(VoxelCollisionKind kind)
    {
        if (kind is not VoxelCollisionKind.None and not VoxelCollisionKind.FullBox)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported collision kind.");
        Kind = kind;
    }

    private VoxelCollision(
        double offsetX,
        double offsetY,
        double offsetZ,
        double sizeX,
        double sizeY,
        double sizeZ)
    {
        Kind = VoxelCollisionKind.LocalBox;
        OffsetX = offsetX;
        OffsetY = offsetY;
        OffsetZ = offsetZ;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
    }

    public static bool TryCreateLocalBox(
        double offsetX,
        double offsetY,
        double offsetZ,
        double sizeX,
        double sizeY,
        double sizeZ,
        out VoxelCollision collision)
    {
        collision = null!;
        if (!IsValidLocalBox(offsetX, offsetY, offsetZ, sizeX, sizeY, sizeZ))
            return false;
        collision = new VoxelCollision(offsetX, offsetY, offsetZ, sizeX, sizeY, sizeZ);
        return true;
    }

    public static VoxelCollision CreateLocalBox(
        double offsetX,
        double offsetY,
        double offsetZ,
        double sizeX,
        double sizeY,
        double sizeZ)
    {
        if (!TryCreateLocalBox(offsetX, offsetY, offsetZ, sizeX, sizeY, sizeZ, out var collision))
            throw new ArgumentOutOfRangeException(nameof(sizeX), "Local box must be finite, strictly positive, and inside the unit cell.");
        return collision;
    }

    public bool TryWorldBox(WorldVoxelCoord cell, VoxelOrientation orientation, out VoxelCollisionBox box)
    {
        box = default;
        if (Kind == VoxelCollisionKind.None)
            return false;
        if (Kind == VoxelCollisionKind.FullBox)
        {
            box = VoxelCollisionBox.ForCell(cell);
            return true;
        }

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        foreach (var localX in new[] { OffsetX, OffsetX + SizeX })
        foreach (var localY in new[] { OffsetY, OffsetY + SizeY })
        foreach (var localZ in new[] { OffsetZ, OffsetZ + SizeZ })
        {
            var (x, y, z) = orientation.ToWorldCell(localX, localY, localZ);
            minX = Math.Min(minX, cell.X + x);
            minY = Math.Min(minY, cell.Y + y);
            minZ = Math.Min(minZ, cell.Z + z);
            maxX = Math.Max(maxX, cell.X + x);
            maxY = Math.Max(maxY, cell.Y + y);
            maxZ = Math.Max(maxZ, cell.Z + z);
        }

        return VoxelCollisionBox.TryCreate(minX, minY, minZ, maxX, maxY, maxZ, out box);
    }

    private static bool IsValidLocalBox(
        double offsetX,
        double offsetY,
        double offsetZ,
        double sizeX,
        double sizeY,
        double sizeZ) =>
        IsFinite(offsetX) && IsFinite(offsetY) && IsFinite(offsetZ) &&
        IsFinite(sizeX) && IsFinite(sizeY) && IsFinite(sizeZ) &&
        sizeX > 0 && sizeY > 0 && sizeZ > 0 &&
        offsetX >= 0 && offsetY >= 0 && offsetZ >= 0 &&
        offsetX + sizeX <= 1 && offsetY + sizeY <= 1 && offsetZ + sizeZ <= 1;

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}

public sealed class VoxelDefinition
{
    public const int NameMinLength = 1;
    public const int NameMaxLength = 100;
    public const int DescriptionMinLength = 1;
    public const int DescriptionMaxLength = 2000;
    public const long ValueMin = 1;
    public const long ValueMax = 1_000_000_000;
    public const double MassMinExclusive = 0;
    public const double MassMax = 1_000_000_000;
    public const int MaxDurabilityMin = 1;
    public const int MaxDurabilityMax = 1_000_000_000;
    public const int TagMinLength = 1;
    public const int TagMaxLength = 64;

    public VoxelDefinitionId Id { get; }
    public string Name { get; }
    public string Description { get; }
    public long Value { get; }
    public double Mass { get; }
    public int MaxDurability { get; }
    public IReadOnlySet<string> Tags { get; }
    public IReadOnlyList<string> Behaviors { get; }
    public VoxelOrientationConstraint AllowedOrientations { get; }
    public VoxelPresentation Presentation { get; }
    public VoxelCollision Collision { get; }

    public bool HasNaturalResourceTag => Tags.Contains(VoxelTags.NaturalResource);

    public VoxelDefinition(
        VoxelDefinitionId id,
        string name,
        string description,
        long value,
        double mass,
        int maxDurability,
        IEnumerable<string> tags,
        IEnumerable<string> behaviors,
        VoxelOrientationConstraint allowedOrientations,
        VoxelPresentation presentation,
        VoxelCollision collision)
    {
        if (string.IsNullOrEmpty(id.Namespace) || string.IsNullOrEmpty(id.LocalId))
            throw new ArgumentException("VoxelDefinitionId cannot be blank.", nameof(id));
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(behaviors);
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(collision);
        if (value is < ValueMin or > ValueMax)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value is out of range.");
        if (double.IsNaN(mass) || double.IsInfinity(mass) || mass <= MassMinExclusive || mass > MassMax)
            throw new ArgumentOutOfRangeException(nameof(mass), mass, "Mass is out of range.");
        if (maxDurability is < MaxDurabilityMin or > MaxDurabilityMax)
            throw new ArgumentOutOfRangeException(nameof(maxDurability), maxDurability, "Max durability is out of range.");
        if (allowedOrientations is not VoxelOrientationConstraint.None
            and not VoxelOrientationConstraint.FourWay
            and not VoxelOrientationConstraint.All24)
            throw new ArgumentOutOfRangeException(nameof(allowedOrientations), allowedOrientations, "Unsupported orientation constraint.");

        var normalizedName = NormalizeDisplayText(name, NameMinLength, NameMaxLength, nameof(name));
        var normalizedDescription = NormalizeDisplayText(description, DescriptionMinLength, DescriptionMaxLength, nameof(description));
        var tagSet = NormalizeTags(tags);
        var behaviorList = behaviors.ToArray();
        if (behaviorList.Length > 0)
            throw new ArgumentException("No behaviors are registered.", nameof(behaviors));

        Id = id;
        Name = normalizedName;
        Description = normalizedDescription;
        Value = value;
        Mass = mass;
        MaxDurability = maxDurability;
        Tags = tagSet;
        Behaviors = new ReadOnlyCollection<string>(behaviorList);
        AllowedOrientations = allowedOrientations;
        Presentation = presentation;
        Collision = collision;
    }

    public static string NormalizeDisplayText(string value, int minLength, int maxLength, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var trimmed = value.Trim();
        var runes = 0;
        foreach (var _ in trimmed.EnumerateRunes())
            runes++;
        if (runes < minLength || runes > maxLength)
            throw new ArgumentException($"Must be {minLength}-{maxLength} Unicode characters after trimming.", paramName);
        return trimmed;
    }

    public static string NormalizeTag(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var trimmed = value.Trim();
        if (!VoxelDefinitionId.IsValidPart(trimmed) ||
            trimmed.Length is < TagMinLength or > TagMaxLength)
            throw new ArgumentException("Tag must be 1-64 lowercase ASCII letters, digits, underscores, or hyphens, starting with a letter or digit.", paramName);
        return trimmed;
    }

    public static string ToWire(VoxelOrientationConstraint orientations) => orientations switch
    {
        VoxelOrientationConstraint.None => "none",
        VoxelOrientationConstraint.FourWay => "four_way",
        VoxelOrientationConstraint.All24 => "all_24",
        _ => throw new ArgumentOutOfRangeException(nameof(orientations), orientations, "Unknown orientation constraint.")
    };

    public static bool TryParseOrientations(string? value, out VoxelOrientationConstraint orientations)
    {
        orientations = value switch
        {
            "none" => VoxelOrientationConstraint.None,
            "four_way" => VoxelOrientationConstraint.FourWay,
            "all_24" => VoxelOrientationConstraint.All24,
            _ => default
        };
        return value is "none" or "four_way" or "all_24";
    }

    public static string ToWire(VoxelPresentationKind kind) => kind switch
    {
        VoxelPresentationKind.Cube => "cube",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown presentation kind.")
    };

    public static bool TryParsePresentationKind(string? value, out VoxelPresentationKind kind)
    {
        if (value == "cube")
        {
            kind = VoxelPresentationKind.Cube;
            return true;
        }

        kind = default;
        return false;
    }

    public static string ToWire(VoxelCollisionKind kind) => kind switch
    {
        VoxelCollisionKind.None => "none",
        VoxelCollisionKind.FullBox => "full_box",
        VoxelCollisionKind.LocalBox => "local_box",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown collision kind.")
    };

    public static bool TryParseCollisionKind(string? value, out VoxelCollisionKind kind)
    {
        kind = value switch
        {
            "none" => VoxelCollisionKind.None,
            "full_box" => VoxelCollisionKind.FullBox,
            "local_box" => VoxelCollisionKind.LocalBox,
            _ => default
        };
        return value is "none" or "full_box" or "local_box";
    }

    public static string FormatMass(double mass) =>
        mass.ToString("G17", CultureInfo.InvariantCulture);

    private static FrozenSet<string> NormalizeTags(IEnumerable<string> tags)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
            set.Add(NormalizeTag(tag, nameof(tags)));
        return set.ToFrozenSet(StringComparer.Ordinal);
    }
}
