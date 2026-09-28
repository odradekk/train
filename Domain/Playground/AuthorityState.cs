using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Train.Domain.Commands;

namespace Train.Domain.Playground;

/// <summary>
/// Fields written into the canonical authority document. Camera, window, render, interpolation,
/// derived graphs, pause, save identity, display name, and the edit log are not in this set.
/// </summary>
public static class AuthorityFieldCatalog
{
    public const int FormatVersion = 1;

    public const string Guarantee =
        "Comparable on the same build, Windows x64, and the same .NET runtime. This is not a cross-platform bitwise guarantee.";

    public static readonly string[] TopLevel =
    {
        "authority_format_version",
        "seed",
        "generator_version",
        "bounds",
        "chunk_size",
        "next_entity_id",
        "transaction_count",
        "simulation_step",
        "next_edit_id",
        "boundary_sequence",
        "simulation_fault",
        "world",
        "save_local_definitions",
        "vehicles",
        "railway",
        "assemblies",
        "pending"
    };

    public static readonly string[] Names =
    {
        "accepted_sequence", "accepted_step", "active_locomotive_id", "alignment", "along_end", "along_start",
        "archetype_id", "assemblies", "authority_format_version", "bound_x", "bound_y", "bound_z", "boundary_sequence",
        "bounds", "chunk_size", "connections", "contacts", "definition_id", "destroyed",
        "distance", "durability", "edit", "edit_id", "end_x", "end_y", "end_z", "exception_type",
        "from_end", "from_part_id", "from_segment_id", "from_vehicle_id", "from_x", "from_y", "from_z",
        "generator_version", "halt_reason", "heading", "id", "input_format_version", "kind", "last_used_archetype_revision",
        "last_used_revision", "max_x", "max_y", "max_z", "message", "min_x", "min_y", "min_z",
        "next_edit_id", "next_entity_id", "occupancy", "orientation", "origin_x", "origin_y", "origin_z",
        "parts", "pending", "position", "rail_volumes", "railway", "reference_segment_id", "revision", "right_x",
        "right_y", "right_z", "running", "runtime_id", "save_local_definitions", "save_local_revision",
        "seed", "segment_id", "segments", "sequence", "simulation_fault", "simulation_step", "size_class",
        "sleeper_count", "sleeper_volumes", "speed", "start_x", "start_y", "start_z", "step", "supports",
        "target_step", "to_end", "to_part_id", "to_segment_id", "to_vehicle_id", "to_x", "to_y", "to_z",
        "transaction_count", "transform", "up_x", "up_y", "up_z", "vehicle_ids", "vehicles", "voxels",
        "world", "world_x_end", "world_x_start", "x", "y", "z"
    };

    public static readonly string[] Excluded =
    {
        "assembly_graph_generation", "asset_revision", "camera", "catalog_generation", "derived_graph",
        "description", "display_name", "edit_log", "interpolation", "name", "paused", "render", "save_id", "window"
    };
}

public enum AuthorityDecodeStatus
{
    Decoded = 0,
    UnknownFormat = 1,
    Invalid = 2
}

public enum AuthorityDifferenceKind
{
    Equal = 0,
    SourceMismatch = 1,
    VersionMismatch = 2,
    EnvironmentMismatch = 3,
    ContentAdaptation = 4,
    MissingCheckpoint = 5,
    MissingObject = 6,
    FieldMismatch = 7
}

public sealed record AuthorityCell(int X, int Y, int Z);

public sealed record AuthorityVoxel(
    int X,
    int Y,
    int Z,
    string DefinitionId,
    string Orientation,
    int Durability,
    ulong? RuntimeId);

public sealed record AuthorityContact(double X, double Y, double Z, int BoundX, int BoundY, int BoundZ);

public sealed record AuthorityTransform(
    double OriginX,
    double OriginY,
    double OriginZ,
    double RightX,
    double RightY,
    double RightZ,
    double UpX,
    double UpY,
    double UpZ);

public sealed record AuthorityPart(
    ulong Id,
    int X,
    int Y,
    int Z,
    string DefinitionId,
    string Orientation,
    int Durability,
    string LastUsedRevision);

public sealed record AuthorityVehicle(
    ulong Id,
    string DefinitionId,
    string LastUsedRevision,
    string ArchetypeId,
    string LastUsedArchetypeRevision,
    string SizeClass,
    string? SaveLocalRevision,
    AuthorityTransform Transform,
    IReadOnlyList<AuthorityPart> Parts,
    IReadOnlyList<AuthorityContact> Contacts);

public sealed record AuthorityLocalVoxel(int X, int Y, int Z, string DefinitionId, string Orientation);

public sealed record AuthorityLocalDefinition(
    string Revision,
    string Id,
    string ArchetypeId,
    string SizeClass,
    IReadOnlyList<AuthorityLocalVoxel> Voxels,
    IReadOnlyList<AuthorityContact> Contacts);

public sealed record AuthorityVolume(
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ);

public sealed record AuthorityAlignment(
    double StartX,
    double StartY,
    double StartZ,
    double EndX,
    double EndY,
    double EndZ);

public sealed record AuthoritySegment(
    ulong Id,
    AuthorityAlignment Alignment,
    IReadOnlyList<AuthorityVolume> RailVolumes,
    IReadOnlyList<AuthorityVolume> SleeperVolumes);

public sealed record AuthorityLink(ulong FromSegment, string FromEnd, ulong ToSegment, string ToEnd);

public sealed record AuthoritySupport(
    ulong SegmentId,
    double WorldXStart,
    double WorldXEnd,
    double AlongStart,
    double AlongEnd);

public sealed record AuthorityOccupancy(int X, int Y, int Z, IReadOnlyList<AuthoritySupport> Supports);

public sealed record AuthorityRailway(
    int SleeperCount,
    IReadOnlyList<AuthoritySegment> Segments,
    IReadOnlyList<AuthorityLink> Connections,
    IReadOnlyList<AuthorityOccupancy> Occupancy,
    IReadOnlyList<AuthorityCell> Destroyed);

public sealed record AuthorityCoupler(
    ulong FromVehicleId,
    ulong FromPartId,
    int FromX,
    int FromY,
    int FromZ,
    ulong ToVehicleId,
    ulong ToPartId,
    int ToX,
    int ToY,
    int ToZ);

public sealed record AuthorityAssembly(
    ulong Id,
    ulong? ActiveLocomotiveId,
    IReadOnlyList<ulong> VehicleIds,
    IReadOnlyList<AuthorityCoupler> Connections,
    ulong ReferenceSegmentId,
    double Distance,
    string Heading,
    double Speed,
    bool Running,
    string HaltReason);

public sealed record AuthorityPending(
    ulong EditId,
    ulong AcceptedStep,
    ulong AcceptedSequence,
    ulong TargetStep,
    string EditCanonical);

public sealed record AuthorityFault(
    ulong? EditId,
    string? Kind,
    ulong Step,
    ulong Sequence,
    string ExceptionType,
    string Message);

public sealed record AuthorityStateModel(
    ulong Seed,
    string GeneratorVersion,
    int BoundsX,
    int BoundsY,
    int BoundsZ,
    int ChunkSize,
    ulong NextEntityId,
    ulong TransactionCount,
    ulong SimulationStep,
    ulong NextEditId,
    ulong BoundarySequence,
    AuthorityFault? Fault,
    IReadOnlyList<AuthorityVoxel> World,
    IReadOnlyList<AuthorityLocalDefinition> SaveLocalDefinitions,
    IReadOnlyList<AuthorityVehicle> Vehicles,
    AuthorityRailway Railway,
    IReadOnlyList<AuthorityAssembly> Assemblies,
    IReadOnlyList<AuthorityPending> Pending);

public sealed record AuthorityContext(
    int FormatVersion,
    string? BuildId,
    string ContentDigest,
    IReadOnlyList<string> ContentDependencies,
    string SourceKind,
    string? ConfigurationId,
    int? ConfigurationVersion,
    ulong Seed,
    string GeneratorVersion,
    string OperatingSystem,
    string Architecture,
    string Framework,
    string Guarantee,
    string? SaveId)
{
    public bool Equals(AuthorityContext? other) =>
        other is not null &&
        FormatVersion == other.FormatVersion &&
        string.Equals(BuildId, other.BuildId, StringComparison.Ordinal) &&
        string.Equals(ContentDigest, other.ContentDigest, StringComparison.Ordinal) &&
        ContentDependencies.SequenceEqual(other.ContentDependencies) &&
        string.Equals(SourceKind, other.SourceKind, StringComparison.Ordinal) &&
        string.Equals(ConfigurationId, other.ConfigurationId, StringComparison.Ordinal) &&
        ConfigurationVersion == other.ConfigurationVersion &&
        Seed == other.Seed &&
        string.Equals(GeneratorVersion, other.GeneratorVersion, StringComparison.Ordinal) &&
        string.Equals(OperatingSystem, other.OperatingSystem, StringComparison.Ordinal) &&
        string.Equals(Architecture, other.Architecture, StringComparison.Ordinal) &&
        string.Equals(Framework, other.Framework, StringComparison.Ordinal) &&
        string.Equals(Guarantee, other.Guarantee, StringComparison.Ordinal) &&
        string.Equals(SaveId, other.SaveId, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(FormatVersion, BuildId, ContentDigest, SourceKind, Seed);
}

public sealed record AuthorityCheckpoint(int Index, string Input, SimulationPosition Boundary, string Canonical, string Digest);

public sealed record AuthorityObservation(AuthorityContext Context, IReadOnlyList<AuthorityCheckpoint> Checkpoints)
{
    public IEnumerable<string> Lines()
    {
        var source = Context.ConfigurationId is null
            ? Context.SourceKind
            : $"{Context.SourceKind} {Context.ConfigurationId} v{Context.ConfigurationVersion}";
        yield return
            $"authority format={Context.FormatVersion.ToString(CultureInfo.InvariantCulture)} " +
            $"build={Context.BuildId ?? "unspecified"} content={Context.ContentDigest} source={source} " +
            $"seed={Context.Seed.ToString(CultureInfo.InvariantCulture)} generator={Context.GeneratorVersion} " +
            $"os={Context.OperatingSystem} arch={Context.Architecture} runtime={Context.Framework} " +
            $"save={Context.SaveId ?? "none"}";
        yield return $"guarantee {Context.Guarantee}";
        foreach (var dependency in Context.ContentDependencies)
            yield return $"content {dependency}";
        foreach (var checkpoint in Checkpoints)
        {
            yield return
                $"checkpoint {checkpoint.Index.ToString(CultureInfo.InvariantCulture)} {checkpoint.Input} " +
                $"boundary {checkpoint.Boundary} digest {checkpoint.Digest}";
        }
    }
}

public sealed record AuthorityDifference(
    AuthorityDifferenceKind Kind,
    int? CheckpointIndex,
    string? Input,
    string? Boundary,
    string? Object,
    string? Field,
    string? Expected,
    string? Actual,
    string Detail)
{
    public bool Equal => Kind == AuthorityDifferenceKind.Equal;

    public static AuthorityDifference Match { get; } = new(
        AuthorityDifferenceKind.Equal, null, null, null, null, null, null, null, "权威状态一致。");

    public IEnumerable<string> Lines()
    {
        yield return $"difference {Kind}";
        if (CheckpointIndex is { } index)
            yield return $"checkpoint {index.ToString(CultureInfo.InvariantCulture)} input {Input} boundary {Boundary}";
        if (Object is not null)
            yield return $"object {Object}";
        if (Field is not null)
            yield return $"field {Field}";
        if (Expected is not null || Actual is not null)
            yield return $"expected {Expected ?? "absent"} actual {Actual ?? "absent"}";
        if (!string.IsNullOrEmpty(Detail))
            yield return Detail;
    }
}

public static class AuthorityEnvironment
{
    private static readonly Lazy<(string? Id, string? File)> Build = new(ReadBuild);

    public static string OperatingSystem => RuntimeInformation.OSDescription;

    public static string Architecture => RuntimeInformation.OSArchitecture.ToString();

    public static string Framework => RuntimeInformation.FrameworkDescription;

    /// <summary>
    /// SHA-256 of a file proven to be the loaded module that contains this type.
    /// Null when no candidate file has that module's identity.
    /// </summary>
    public static string? CurrentBuildId => Build.Value.Id;

    public static string? CurrentBuildFile => Build.Value.File;

    private static (string? Id, string? File) ReadBuild() =>
        TryIdentifyBuild(typeof(AuthorityEnvironment).Assembly, Candidates());

    /// <summary>
    /// Returns the SHA-256 of the first candidate whose module id matches <paramref name="assembly"/>.
    /// A same-named file that is a different module is ignored. Unknown when none match.
    /// </summary>
    internal static (string? Id, string? File) TryIdentifyBuild(Assembly assembly, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            string path;
            try
            {
                path = Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
                continue;
            }
            catch (NotSupportedException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (!ModuleMatches(assembly, bytes))
                continue;
            return (Convert.ToHexString(SHA256.HashData(bytes)), path);
        }

        return (null, null);
    }

    private static bool ModuleMatches(Assembly assembly, byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new PEReader(stream);
            if (!reader.HasMetadata)
                return false;
            var metadata = reader.GetMetadataReader();
            var module = metadata.GetModuleDefinition();
            return metadata.GetGuid(module.Mvid) == assembly.ManifestModule.ModuleVersionId;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        var assembly = typeof(AuthorityEnvironment).Assembly;
        yield return assembly.Location;
        var name = assembly.GetName().Name;
        if (string.IsNullOrEmpty(name))
            yield break;
        yield return Path.Combine(AppContext.BaseDirectory, name + ".dll");
        yield return Path.Combine(Directory.GetCurrentDirectory(), ".godot", "mono", "temp", "bin", "Debug", name + ".dll");
    }
}
