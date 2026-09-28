using Train.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;

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
        if (_session is { CanRecordContentPublication: false })
        {
            return ContentWorldReloadPlan.Reject(new[]
            {
                new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.SimulationUnavailable,
                    "The live session is faulted or out of input ids, so content cannot be published into it.")
            });
        }

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

        List<CompositeInstance>? vehicles = null;
        if (_session is not null)
            vehicles = AdaptVehicles(_session.Vehicles, voxels, composites, diagnostics);

        if (_session is not null && vehicles is not null)
            ValidateAssemblies(_session, vehicles, voxels, diagnostics);

        if (diagnostics.Count > 0)
            return ContentWorldReloadPlan.Reject(diagnostics);

        if (_session is null)
            return ContentWorldReloadPlan.Accept(replacements.Count == 0 ? null : () => world.ReplaceOccupied(replacements));

        var session = _session;
        var changedVehicles = vehicles is not null && VehiclesDiffer(session.Vehicles, vehicles);
        var publication = ContentPublishEdit.For(voxels, composites);
        // Equal semantic digests leave world evolution unchanged, so no content.publish edit is recorded.
        if (replacements.Count == 0 && !changedVehicles && session.CurrentVoxels is { } published &&
            ContentPublishEdit.For(published, session.CurrentComposites).ContentDigest == publication.ContentDigest)
            return ContentWorldReloadPlan.Accept(null);
        return ContentWorldReloadPlan.Accept(() => session.PublishContent(
            publication,
            () => session.ApplyContentReload(voxels, replacements, changedVehicles ? vehicles : null)));
    }

    public static bool TryAdaptVehicles(
        IReadOnlyList<CompositeInstance> vehicles,
        VoxelCatalogSnapshot voxels,
        CompositeCatalogSnapshot composites,
        out IReadOnlyList<CompositeInstance> adapted,
        out WorldSaveStatus status)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(voxels);
        ArgumentNullException.ThrowIfNull(composites);
        var diagnostics = new List<ContentDiagnostic>();
        var next = AdaptVehicles(vehicles, voxels, composites, diagnostics);
        if (diagnostics.Count == 0)
        {
            adapted = next;
            status = WorldSaveStatus.Loaded;
            return true;
        }

        adapted = Array.Empty<CompositeInstance>();
        status = diagnostics.Any(item =>
            item.Code is ContentDiagnosticCodes.UnresolvedVoxelReference
                or ContentDiagnosticCodes.UnresolvedCompositeReference
                or ContentDiagnosticCodes.UnresolvedArchetypeReference)
            ? WorldSaveStatus.MissingDefinition
            : WorldSaveStatus.InvalidDelta;
        return false;
    }

    private static List<CompositeInstance> AdaptVehicles(
        IReadOnlyList<CompositeInstance> vehicles,
        VoxelCatalogSnapshot voxels,
        CompositeCatalogSnapshot composites,
        List<ContentDiagnostic> diagnostics)
    {
        var adapted = new List<CompositeInstance>(vehicles.Count);
        foreach (var vehicle in vehicles)
        {
            CompositeCatalogEntry? compositeEntry = null;
            if (vehicle.SaveLocalDefinition is null &&
                !composites.TryGet(vehicle.DefinitionId, out compositeEntry))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.UnresolvedCompositeReference,
                    "A manufactured vehicle is missing from the candidate composite catalog.",
                    jsonPath: vehicle.DefinitionId.Value));
                continue;
            }

            if (vehicle.SaveLocalRevision is not null &&
                !string.Equals(vehicle.SaveLocalRevision, vehicle.LastUsedRevision, StringComparison.Ordinal))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.UnsupportedVehicleAdaptation,
                    "Save-local vehicle layout revision is inconsistent.",
                    jsonPath: vehicle.DefinitionId.Value));
            }

            if (!composites.TryGet(vehicle.ArchetypeId, out var archetypeEntry))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.UnresolvedArchetypeReference,
                    "A manufactured vehicle archetype is missing from the candidate catalog.",
                    jsonPath: vehicle.ArchetypeId.Value));
                continue;
            }

            if (ConstructionValidator.EvaluateIdentity(vehicle, archetypeEntry.Archetype, voxels) !=
                IdentityRetentionStatus.Retained)
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.IncompatibleVoxelState,
                    "A manufactured vehicle can no longer retain its identity under the candidate rules.",
                    jsonPath: vehicle.DefinitionId.Value));
                continue;
            }

            var parts = new List<CompositePart>(vehicle.Parts.Count);
            var partsValid = true;
            foreach (var part in vehicle.Parts)
            {
                if (!voxels.TryGet(part.DefinitionId, out var partEntry))
                {
                    diagnostics.Add(new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.UnresolvedVoxelReference,
                        "A vehicle part is missing from the candidate catalog.",
                        jsonPath: part.DefinitionId.Value));
                    partsValid = false;
                    continue;
                }

                if (!part.Orientation.IsAllowedBy(partEntry.Definition.AllowedOrientations))
                {
                    diagnostics.Add(new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.IncompatibleVoxelState,
                        "A vehicle part keeps an orientation the candidate definition does not allow.",
                        jsonPath: part.DefinitionId.Value));
                    partsValid = false;
                    continue;
                }

                parts.Add(new CompositePart(
                    part.Id,
                    part.Cell,
                    part.DefinitionId,
                    part.Orientation,
                    Math.Min(part.CurrentDurability, partEntry.Definition.MaxDurability),
                    partEntry.SemanticRevision));
            }

            if (!partsValid)
                continue;

            // Change detection only: stamp the composite revision we resolved against.
            // Save-local layouts keep their own revision so blueprint edits cannot rewrite them.
            string lastUsedRevision;
            if (vehicle.SaveLocalDefinition is not null)
            {
                if (string.IsNullOrEmpty(vehicle.SaveLocalRevision))
                {
                    diagnostics.Add(new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.UnsupportedVehicleAdaptation,
                        "Save-local vehicle layout is missing its revision.",
                        jsonPath: vehicle.DefinitionId.Value));
                    continue;
                }

                lastUsedRevision = vehicle.SaveLocalRevision;
            }
            else
            {
                lastUsedRevision = compositeEntry!.SemanticRevision;
            }
            adapted.Add(new CompositeInstance(
                vehicle.Id,
                vehicle.DefinitionId,
                lastUsedRevision,
                vehicle.ArchetypeId,
                archetypeEntry.SemanticRevision,
                vehicle.SizeClass,
                vehicle.Transform,
                parts,
                vehicle.Contacts,
                vehicle.SaveLocalRevision,
                vehicle.SaveLocalDefinition));
        }

        return adapted;
    }

    private static void ValidateAssemblies(
        VoxelDevelopmentSession session,
        IReadOnlyList<CompositeInstance> vehicles,
        VoxelCatalogSnapshot voxels,
        List<ContentDiagnostic> diagnostics)
    {
        foreach (var assembly in session.QueryTrainAssemblies())
        {
            var members = assembly.VehicleIds
                .Select(id => vehicles.FirstOrDefault(item => item.Id == id))
                .ToArray();
            if (members.Any(item => item is null))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.UnsupportedVehicleAdaptation,
                    "A train assembly is missing a member under the candidate catalog.",
                    jsonPath: assembly.Id.Value.ToString()));
                continue;
            }

            if (assembly.Running)
            {
                var locomotive = assembly.ActiveLocomotiveId is { } locoId
                    ? members.FirstOrDefault(item => item!.Id == locoId)
                    : null;
                if (locomotive is null)
                {
                    diagnostics.Add(new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.UnsupportedVehicleAdaptation,
                        "A train assembly is missing its locomotive under the candidate catalog.",
                        jsonPath: assembly.Id.Value.ToString()));
                    continue;
                }

                if (!StraightTrainMotion.TryResolveLocomotive(locomotive, voxels, out _, out _))
                {
                    diagnostics.Add(new ContentDiagnostic(
                        ContentDiagnosticSeverity.Error,
                        ContentDiagnosticCodes.UnsupportedVehicleAdaptation,
                        "A train assembly cannot keep its required propulsion or control under the candidate catalog.",
                        jsonPath: locomotive.DefinitionId.Value));
                }

                continue;
            }

            if (assembly.Speed > 0 &&
                !StraightTrainMotion.TryResolveTrackContact(members!, voxels, out _))
            {
                diagnostics.Add(new ContentDiagnostic(
                    ContentDiagnosticSeverity.Error,
                    ContentDiagnosticCodes.UnsupportedVehicleAdaptation,
                    "A coasting assembly cannot resolve track_contact coast deceleration under the candidate catalog.",
                    jsonPath: assembly.Id.Value.ToString()));
            }
        }
    }

    private static bool VehiclesDiffer(
        IReadOnlyList<CompositeInstance> current,
        IReadOnlyList<CompositeInstance> next)
    {
        if (current.Count != next.Count)
            return true;
        for (var index = 0; index < current.Count; index++)
        {
            var left = current[index];
            var right = next[index];
            if (left.Id != right.Id ||
                !string.Equals(left.LastUsedRevision, right.LastUsedRevision, StringComparison.Ordinal) ||
                !string.Equals(left.LastUsedArchetypeRevision, right.LastUsedArchetypeRevision, StringComparison.Ordinal) ||
                left.Parts.Count != right.Parts.Count)
                return true;
            for (var partIndex = 0; partIndex < left.Parts.Count; partIndex++)
            {
                var leftPart = left.Parts[partIndex];
                var rightPart = right.Parts[partIndex];
                if (leftPart.Id != rightPart.Id ||
                    leftPart.CurrentDurability != rightPart.CurrentDurability ||
                    !string.Equals(leftPart.LastUsedRevision, rightPart.LastUsedRevision, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }
}
