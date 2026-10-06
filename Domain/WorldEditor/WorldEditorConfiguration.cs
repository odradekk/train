using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using System.Globalization;
using Train.Core.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

public enum WorldEditorEstablishStatus
{
    Established,
    UnknownConfiguration,
    ContentUnavailable,
    MissingContent,
    UnsupportedDeclaration,
    InitialStateRejected,
    ConfigurationMismatch
}

public sealed record WorldEditorStaticTrain(
    CompositeDefinitionId LocomotiveId,
    CompositeTransform LocomotiveTransform,
    CompositeDefinitionId CarriageId,
    CompositeTransform CarriageTransform)
{
    public static WorldEditorStaticTrain Formal { get; } = new(
        StaticTrainFixture.LocomotiveId,
        StaticTrainFixture.LocomotiveTransform,
        StaticTrainFixture.CarriageId,
        StaticTrainFixture.CarriageTransform);
}

/// <summary>
/// A named, versioned fixed configuration: it declares only the initial world, not a generator and not verification steps.
/// </summary>
public sealed record WorldEditorConfiguration(
    string Id,
    int Version,
    string Title,
    VoxelWorldIdentity BaseWorld,
    RailConstructionPlan? Railway,
    WorldEditorStaticTrain? Train);

public static class WorldEditorConfigurations
{
    public const string StraightRailTrainId = "straight-rail-train";
    public const string StraightRailEmptyId = "straight-rail-empty";

    public static IReadOnlyList<WorldEditorConfiguration> All { get; } = new[]
    {
        new WorldEditorConfiguration(
            StraightRailTrainId,
            1,
            "正式直轨 + 一车头一车厢",
            VoxelWorldIdentity.Slice4,
            StraightRailFixture.CreateFixturePlan(),
            WorldEditorStaticTrain.Formal),
        new WorldEditorConfiguration(
            StraightRailEmptyId,
            1,
            "正式直轨（无车辆）",
            VoxelWorldIdentity.Slice4,
            StraightRailFixture.CreateFixturePlan(),
            null)
    };

    public static WorldEditorConfiguration? Find(string id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));

    public static WorldEditorEstablishStatus TryResolve(
        string id,
        VoxelCatalog catalog,
        CompositeCatalog composites,
        out WorldSource source)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(composites);
        source = null!;
        var configuration = Find(id);
        if (configuration is null)
            return WorldEditorEstablishStatus.UnknownConfiguration;
        if (catalog.Current is not { } voxels)
            return WorldEditorEstablishStatus.ContentUnavailable;
        if (configuration.Train is { } train && train != WorldEditorStaticTrain.Formal)
            return WorldEditorEstablishStatus.UnsupportedDeclaration;

        var status = TryCollectDependencies(configuration, voxels, composites.Current, out var dependencies);
        if (status != WorldEditorEstablishStatus.Established)
            return status;
        source = new WorldSource(
            WorldSource.FixedConfigurationKind,
            configuration.Id,
            configuration.Version,
            EffectiveParameters(configuration),
            dependencies);
        return WorldEditorEstablishStatus.Established;
    }

    public static IReadOnlyList<WorldSourceParameter> EffectiveParameters(WorldEditorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var world = configuration.BaseWorld;
        var parameters = new List<WorldSourceParameter>
        {
            new("base.generator_version", world.GeneratorVersion),
            new("base.seed", world.Seed.ToString(CultureInfo.InvariantCulture)),
            new("base.bounds", $"{world.Bounds.SizeX},{world.Bounds.SizeY},{world.Bounds.SizeZ}"),
            new("base.chunk_size", world.ChunkSize.ToString(CultureInfo.InvariantCulture))
        };
        if (configuration.Railway is { } railway)
        {
            parameters.Add(new("railway.segments", railway.Segments.Count.ToString(CultureInfo.InvariantCulture)));
            for (var index = 0; index < railway.Segments.Count; index++)
            {
                var alignment = railway.Segments[index].Alignment;
                parameters.Add(new(
                    $"railway.segment.{index}.alignment",
                    string.Join(',', new[] { alignment.StartX, alignment.StartY, alignment.StartZ, alignment.EndX, alignment.EndY, alignment.EndZ }.Select(Number))));
            }

            parameters.Add(new("railway.connections", string.Join(';', railway.Connections.Select(connection =>
                $"{connection.FromSegment}:{connection.FromEnd}->{connection.ToSegment}:{connection.ToEnd}"))));
            parameters.Add(new("railway.sleepers", railway.Segments
                .SelectMany(segment => segment.SleeperVolumes)
                .Distinct()
                .Count()
                .ToString(CultureInfo.InvariantCulture)));
        }

        if (configuration.Train is { } train)
        {
            parameters.Add(new("train.locomotive", train.LocomotiveId.Value));
            parameters.Add(new("train.locomotive_origin", Origin(train.LocomotiveTransform)));
            parameters.Add(new("train.carriage", train.CarriageId.Value));
            parameters.Add(new("train.carriage_origin", Origin(train.CarriageTransform)));
        }

        return parameters;
    }

    private static WorldEditorEstablishStatus TryCollectDependencies(
        WorldEditorConfiguration configuration,
        VoxelCatalogSnapshot voxels,
        CompositeCatalogSnapshot composites,
        out IReadOnlyList<WorldSourceDependency> dependencies)
    {
        dependencies = Array.Empty<WorldSourceDependency>();
        var voxelIds = new HashSet<VoxelDefinitionId> { VoxelWorldFactory.DevelopmentDefinitionId };
        if (configuration.Railway is not null)
            voxelIds.Add(StraightRailFixture.RailDefinitionId);

        var collected = new List<WorldSourceDependency>();
        if (configuration.Train is { } train)
        {
            foreach (var compositeId in new[] { train.LocomotiveId, train.CarriageId })
            {
                if (!composites.TryGet(compositeId, out var composite) ||
                    !composites.TryGet(composite.Definition.ArchetypeId, out var archetype))
                    return WorldEditorEstablishStatus.MissingContent;
                collected.Add(new("composite", compositeId.Value, composite.SemanticRevision));
                collected.Add(new("archetype", composite.Definition.ArchetypeId.Value, archetype.SemanticRevision));
                foreach (var voxel in composite.Definition.Voxels)
                    voxelIds.Add(voxel.DefinitionId);
            }
        }

        foreach (var voxelId in voxelIds)
        {
            if (!voxels.TryGet(voxelId, out var entry))
                return WorldEditorEstablishStatus.MissingContent;
            collected.Add(new("voxel", voxelId.Value, entry.SemanticRevision));
        }

        dependencies = collected
            .Distinct()
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        return WorldEditorEstablishStatus.Established;
    }

    private static string Origin(CompositeTransform transform) =>
        string.Join(',', new[] { transform.OriginX, transform.OriginY, transform.OriginZ }.Select(Number));

    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
}
