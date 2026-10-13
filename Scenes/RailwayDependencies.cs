using System.Collections.Immutable;
using System.Text.Json;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal enum RailwayModelRole { Decoration, Bridge, Tunnel }
internal readonly record struct RailwayModelRead(TerrainTypeId Terrain, int Slot, RailwayModelRole Role);
internal sealed class RailwayDependencies
{
    private readonly ImmutableArray<TerrainTypeId> terrains;
    private readonly ImmutableArray<RailwayModelRead> slots;
    private readonly ImmutableArray<DesignHash> models;
    private readonly ImmutableArray<VoxelDefinitionId> voxels;
    private readonly string value;
    private readonly bool startTerrain;
    private readonly bool endTerrain;
    private RailwayDependencies(SceneWorldSnapshot snapshot, RailwayPath path, int index,
        ImmutableArray<TerrainTypeId> terrains, ImmutableArray<RailwayModelRead> slots,
        ImmutableArray<DesignHash> models, ImmutableArray<VoxelDefinitionId> voxels, bool startTerrain, bool endTerrain)
    {
        this.terrains = terrains; this.slots = slots; this.models = models; this.voxels = voxels;
        this.startTerrain = startTerrain; this.endTerrain = endTerrain;
        value = Read(snapshot, path, index);
    }
    internal static RailwayDependencies Capture(SceneWorldSnapshot snapshot, RailwayPath path, int index,
        IEnumerable<TerrainTypeId> terrains, IEnumerable<RailwayModelRead> slots,
        IEnumerable<DesignHash> models, IEnumerable<VoxelDefinitionId> voxels, bool startTerrain, bool endTerrain) =>
        new(snapshot, path, index, terrains.Distinct().ToImmutableArray(), slots.Distinct().ToImmutableArray(),
            models.Distinct().ToImmutableArray(), voxels.Distinct().ToImmutableArray(), startTerrain, endTerrain);
    internal bool Matches(SceneWorldSnapshot snapshot, RailwayPath path, int index) => value == Read(snapshot, path, index);
    private string Read(SceneWorldSnapshot snapshot, RailwayPath path, int index)
    {
        var values = new List<object?> { path.Seed, path.Edge.Id, path.Edge.LengthMeters, path.Edge.Tracks, path.Edge.TrackLimit,
            path.Start.Id, path.Start.Position, path.End.Id, path.End.Position, path.Rules, index, Terrain(path.Edge.Terrain) };
        if (startTerrain) values.Add(Terrain(path.Start.Terrain));
        if (endTerrain) values.Add(Terrain(path.End.Terrain));
        foreach (var id in terrains)
        {
            if (snapshot.Content.FindTerrain(id) is ContentLookup<TerrainAppearanceProjection>.Found found)
            {
                var t = found.Value;
                values.Add(new object[] { t.Id, t.SurfaceVoxel, t.SubsurfaceVoxel, t.RockVoxel,
                    t.LocalHeightNoise, t.DecorationTargetPerSquareMeter, t.Decorations.Select(item => item.Weight).ToArray() });
            }
            else values.Add(Missing(snapshot.Content.FindTerrain(id)));
        }
        foreach (var slot in slots)
        {
            if (snapshot.Content.FindTerrain(slot.Terrain) is not ContentLookup<TerrainAppearanceProjection>.Found found) { values.Add(null); continue; }
            var t = found.Value;
            if (slot.Role == RailwayModelRole.Decoration)
                values.Add(slot.Slot < t.Decorations.Length ? t.Decorations[slot.Slot].SceneModel : null);
            else
            {
                var model = t.RailwayModels.FirstOrDefault(item => item.Tracks == path.Edge.Tracks);
                values.Add(slot.Role == RailwayModelRole.Bridge ? model?.Bridge : model?.TunnelPortal);
            }
        }
        foreach (var hash in models)
        {
            var lookup = snapshot.Content.FindDesign(hash);
            if (lookup is ContentLookup<DesignEvaluation>.Found found && found.Value is DesignEvaluation.Complete or DesignEvaluation.Partial)
            {
                var s = DesignGeometry.Projection(found.Value);
                values.Add(new object?[] { hash, s.Design, s.OccupiedBounds, s.RootFlatten,
                    s.Voxels.Select(v => new object?[] { v.At, v.Voxel, v.Orientation, v.Shape, v.Paint,
                        v.Number, v.Element, v.ObjectPath, v.LocalAt, v.LocalOrientation, v.Missing }).ToArray(),
                    s.NestedObjects.Select(item => new object[] { item.NestedItemIndices, item.At, item.Orientation, item.Details }).ToArray(),
                    s.EditLocks.Select(item => new object?[] { item.Element, Reference(item.Reference), item.Package,
                        item.BlockedOperations, Diagnostics(item.Diagnostics) }).ToArray(),
                    Diagnostics(found.Value is DesignEvaluation.Complete c ? c.Diagnostics : ((DesignEvaluation.Partial)found.Value).Diagnostics) });
            }
            else values.Add(new object[] { hash, Missing(lookup) });
        }
        foreach (var id in voxels)
        {
            var lookup = snapshot.Content.FindVoxelAppearance(id);
            values.Add(lookup is ContentLookup<VoxelAppearanceProjection>.Found found ?
                new object[] { id, found.Value.AllowedOrientations, found.Value.Collision, found.Value.Presentation, found.Value.Behaviors } :
                new object[] { id, Missing(lookup) });
        }
        return JsonSerializer.Serialize(values);
    }
    private static object[] Terrain(TerrainView value) => [value.TypeId, value.Relief, value.FeatureIds];
    private static object? Reference(ContentReference? value) => value is ContentReference.Core core ?
        new object[] { "core", core.Definition } : value;
    private static object Diagnostics(ImmutableArray<ContentDiagnostic> values) => values.Select(value =>
        new object?[] { value.Code, value.Severity, value.Stage, Reference(value.Subject), value.Source,
            value.Field, value.Element, value.Message }).ToArray();
    private static object Missing<T>(ContentLookup<T> value) where T : notnull => value switch
    {
        ContentLookup<T>.Missing missing => new object[] { "missing", Diagnostics(missing.Diagnostics) },
        ContentLookup<T>.Found { Value: DesignEvaluation.Failed failed } => new object[] { "failed", Diagnostics(failed.Diagnostics) },
        _ => new object[] { "not_found" }
    };
}
