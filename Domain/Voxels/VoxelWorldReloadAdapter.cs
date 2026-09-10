using Train.Content;
using Train.Domain.Composites;

namespace Train.Domain.Voxels;

public sealed class VoxelWorldReloadAdapter : IContentWorldReloadAdapter
{
    private readonly Func<VoxelWorld> _world;
    private readonly VoxelDevelopmentSession? _session;

    public VoxelWorldReloadAdapter(Func<VoxelWorld> world, VoxelDevelopmentSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _session = session;
    }

    public static VoxelWorldReloadAdapter For(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new VoxelWorldReloadAdapter(() => session.World, session);
    }

    public ContentWorldReloadPlan Prepare(VoxelCatalogSnapshot candidate) =>
        Prepare(candidate, _session?.CurrentComposites ?? CompositeCatalogSnapshot.Empty);

    public ContentWorldReloadPlan Prepare(VoxelCatalogSnapshot voxels, CompositeCatalogSnapshot composites)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        ArgumentNullException.ThrowIfNull(composites);
        var world = _world() ?? throw new InvalidOperationException("A voxel world is required to prepare a live reload.");
        var diagnostics = new List<ContentDiagnostic>();
        var replacements = new List<(WorldVoxelCoord Position, WorldVoxel Voxel)>();

        foreach (var (position, voxel) in world.OccupiedVoxels())
        {
            if (!voxels.TryGet(voxel.DefinitionId, out var entry))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.UnresolvedVoxelReference,
                    $"A world voxel at ({position.X},{position.Y},{position.Z}) is missing from the candidate catalog.",
                    jsonPath: voxel.DefinitionId.Value));
                continue;
            }

            if (!voxel.Orientation.IsAllowedBy(entry.Definition.AllowedOrientations))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.IncompatibleVoxelState,
                    $"A world voxel at ({position.X},{position.Y},{position.Z}) keeps an orientation the candidate definition does not allow.",
                    jsonPath: voxel.DefinitionId.Value));
                continue;
            }

            var durability = Math.Min(voxel.CurrentDurability, entry.Definition.MaxDurability);
            if (durability != voxel.CurrentDurability)
                replacements.Add((position, voxel with { CurrentDurability = durability }));
        }

        if (_session is not null)
        {
            foreach (var vehicle in _session.Vehicles)
            {
                if (!composites.TryGet(vehicle.DefinitionId, out var compositeEntry))
                {
                    diagnostics.Add(new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.UnresolvedCompositeReference,
                        "A manufactured vehicle is missing from the candidate composite catalog.",
                        jsonPath: vehicle.DefinitionId.Value));
                    continue;
                }

                if (!string.Equals(compositeEntry.SemanticRevision, vehicle.LastUsedRevision, StringComparison.Ordinal))
                {
                    diagnostics.Add(new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.UnsupportedVehicleAdaptation,
                        "Vehicle definition adaptation is not supported yet.",
                        jsonPath: vehicle.DefinitionId.Value));
                }

                if (!composites.TryGet(vehicle.ArchetypeId, out var archetypeEntry))
                {
                    diagnostics.Add(new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.UnresolvedArchetypeReference,
                        "A manufactured vehicle archetype is missing from the candidate catalog.",
                        jsonPath: vehicle.ArchetypeId.Value));
                }
                else if (!string.Equals(archetypeEntry.SemanticRevision, vehicle.LastUsedArchetypeRevision, StringComparison.Ordinal))
                {
                    diagnostics.Add(new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.UnsupportedVehicleAdaptation,
                        "Vehicle archetype adaptation is not supported yet.",
                        jsonPath: vehicle.ArchetypeId.Value));
                }

                foreach (var part in vehicle.Parts)
                {
                    if (!voxels.TryGet(part.DefinitionId, out var partEntry))
                    {
                        diagnostics.Add(new ContentDiagnostic(
                            ContentDiagnosticSeverity.Error,
                            ContentDiagnosticCodes.UnresolvedVoxelReference,
                            "A vehicle part is missing from the candidate catalog.",
                            jsonPath: part.DefinitionId.Value));
                        continue;
                    }

                    if (!part.Orientation.IsAllowedBy(partEntry.Definition.AllowedOrientations))
                    {
                        diagnostics.Add(new ContentDiagnostic(
                            ContentDiagnosticSeverity.Error,
                            ContentDiagnosticCodes.IncompatibleVoxelState,
                            "A vehicle part keeps an orientation the candidate definition does not allow.",
                            jsonPath: part.DefinitionId.Value));
                    }

                    if (!string.Equals(partEntry.SemanticRevision, part.LastUsedRevision, StringComparison.Ordinal))
                    {
                        diagnostics.Add(new ContentDiagnostic(
                            ContentDiagnosticSeverity.Error,
                            ContentDiagnosticCodes.UnsupportedVehicleAdaptation,
                            "Vehicle part adaptation is not supported yet.",
                            jsonPath: part.DefinitionId.Value));
                    }
                }
            }
        }

        if (diagnostics.Count > 0)
            return ContentWorldReloadPlan.Reject(diagnostics);

        return ContentWorldReloadPlan.Accept(
            replacements.Count == 0 ? null : () => world.ReplaceOccupied(replacements));
    }
}
