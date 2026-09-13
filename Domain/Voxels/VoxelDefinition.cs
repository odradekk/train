using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using Train.Domain.Behaviors;

namespace Train.Domain.Voxels;

public static class VoxelTags
{
    public const string NaturalResource = "natural_resource";
    public const string IdentityCore = "identity_core";
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
            throw new ArgumentException("Local box must be finite, strictly positive, and a distinguishable interval inside the unit cell.");
        return collision;
    }

    public bool OverlapsProbe(WorldVoxelCoord cell, VoxelOrientation orientation, VoxelCollisionBox probe)
    {
        if (Kind == VoxelCollisionKind.None)
            return false;
        if (Kind == VoxelCollisionKind.FullBox)
            return VoxelCollisionBox.ForCell(cell).Overlaps(probe);
        return LocalBoxOverlaps(cell, orientation, probe);
    }

    public bool TryDerivedExtents(
        WorldVoxelCoord cell,
        VoxelOrientation orientation,
        out double centerX,
        out double centerY,
        out double centerZ,
        out double sizeX,
        out double sizeY,
        out double sizeZ)
    {
        centerX = centerY = centerZ = sizeX = sizeY = sizeZ = 0;
        if (Kind == VoxelCollisionKind.None)
            return false;
        if (Kind == VoxelCollisionKind.FullBox)
        {
            centerX = cell.X + 0.5;
            centerY = cell.Y + 0.5;
            centerZ = cell.Z + 0.5;
            sizeX = sizeY = sizeZ = 1;
            return true;
        }

        var (localCenterX, localCenterY, localCenterZ) = orientation.ToWorldCell(
            OffsetX + SizeX * 0.5,
            OffsetY + SizeY * 0.5,
            OffsetZ + SizeZ * 0.5);
        centerX = cell.X + localCenterX;
        centerY = cell.Y + localCenterY;
        centerZ = cell.Z + localCenterZ;
        sizeX = SizeAlong(orientation.InverseTransform(VoxelAxis.PositiveX));
        sizeY = SizeAlong(orientation.InverseTransform(VoxelAxis.PositiveY));
        sizeZ = SizeAlong(orientation.InverseTransform(VoxelAxis.PositiveZ));
        return true;
    }

    private bool LocalBoxOverlaps(WorldVoxelCoord cell, VoxelOrientation orientation, VoxelCollisionBox probe)
    {
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

        return minX < OffsetX + SizeX && maxX > OffsetX &&
               minY < OffsetY + SizeY && maxY > OffsetY &&
               minZ < OffsetZ + SizeZ && maxZ > OffsetZ;
    }

    private double SizeAlong(VoxelAxis localAxis) => localAxis switch
    {
        VoxelAxis.PositiveX or VoxelAxis.NegativeX => SizeX,
        VoxelAxis.PositiveY or VoxelAxis.NegativeY => SizeY,
        VoxelAxis.PositiveZ or VoxelAxis.NegativeZ => SizeZ,
        _ => throw new ArgumentOutOfRangeException(nameof(localAxis), localAxis, "Unknown voxel axis.")
    };

    private static bool IsValidLocalBox(
        double offsetX,
        double offsetY,
        double offsetZ,
        double sizeX,
        double sizeY,
        double sizeZ)
    {
        if (!IsFinite(offsetX) || !IsFinite(offsetY) || !IsFinite(offsetZ) ||
            !IsFinite(sizeX) || !IsFinite(sizeY) || !IsFinite(sizeZ) ||
            sizeX <= 0 || sizeY <= 0 || sizeZ <= 0 ||
            offsetX < 0 || offsetY < 0 || offsetZ < 0)
            return false;
        // Ends must stay distinguishable in double and inside the unit cell.
        // Orthogonal maps send [0,1] to [0,1]; world translation and Godot
        // single-precision conversion are query/adapter concerns, not content law.
        return offsetX + sizeX > offsetX && offsetY + sizeY > offsetY && offsetZ + sizeZ > offsetZ &&
               offsetX + sizeX <= 1 && offsetY + sizeY <= 1 && offsetZ + sizeZ <= 1;
    }

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
        if (behaviorList.Any(behavior => !BehaviorRegistry.IsRegistered(behavior)))
            throw new ArgumentException("Unknown or unregistered behavior.", nameof(behaviors));

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
