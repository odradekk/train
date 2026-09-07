using Train.Content;

namespace Train.Domain.Voxels;

public sealed class VoxelWorldReloadAdapter : IContentWorldReloadAdapter
{
    private readonly Func<VoxelWorld> _world;

    public VoxelWorldReloadAdapter(Func<VoxelWorld> world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
    }

    public static VoxelWorldReloadAdapter For(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new VoxelWorldReloadAdapter(() => session.World);
    }

    public ContentWorldReloadPlan Prepare(VoxelCatalogSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var world = _world() ?? throw new InvalidOperationException("A voxel world is required to prepare a live reload.");
        var diagnostics = new List<ContentDiagnostic>();
        var replacements = new List<(WorldVoxelCoord Position, WorldVoxel Voxel)>();

        foreach (var (position, voxel) in world.OccupiedVoxels())
        {
            if (!candidate.TryGet(voxel.DefinitionId, out var entry))
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

        if (diagnostics.Count > 0)
            return ContentWorldReloadPlan.Reject(diagnostics);

        return ContentWorldReloadPlan.Accept(
            replacements.Count == 0 ? null : () => world.ReplaceOccupied(replacements));
    }
}
