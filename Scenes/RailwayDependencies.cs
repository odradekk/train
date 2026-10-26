using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal enum RailwayModelRole { Decoration, Bridge, Tunnel, RepairSite }
internal readonly record struct RailwayModelRead(TerrainTypeId Terrain, int Slot, RailwayModelRole Role);
internal sealed class RailwayDependencies
{
    private static readonly JsonSerializerOptions ValueOptions = CreateValueOptions();
    internal static string Values(object? value) => JsonSerializer.Serialize(value, ValueOptions);

    private static JsonSerializerOptions CreateValueOptions()
    {
        // The converter preserves runtime variant fields; the discriminator distinguishes variants with identical fields.
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Kind != JsonTypeInfoKind.Object) return;
            var type = info.CreateJsonPropertyInfo(typeof(string), "$type");
            type.Get = value => value.GetType().FullName;
            info.Properties.Add(type);
        });
        return new() { IncludeFields = true, TypeInfoResolver = resolver, Converters = { new RuntimeVariantConverterFactory() } };
    }

    private sealed class RuntimeVariantConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type type) => type.IsClass && type.IsAbstract;
        public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(RuntimeVariantConverter<>).MakeGenericType(type))!;
    }

    private sealed class RuntimeVariantConverter<T> : JsonConverter<T> where T : class
    {
        public override T? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException("Railway dependency keys are only serialized.");
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    private readonly ImmutableArray<TerrainTypeId> terrains;
    private readonly ImmutableArray<RailwayModelRead> slots;
    private readonly ImmutableArray<DesignHash> models;
    private readonly ImmutableArray<VoxelDefinitionId> voxels;
    internal long ArrayBytes => (long)terrains.Length * Unsafe.SizeOf<TerrainTypeId>() +
        (long)slots.Length * Unsafe.SizeOf<RailwayModelRead>() + (long)models.Length * Unsafe.SizeOf<DesignHash>() +
        (long)voxels.Length * Unsafe.SizeOf<VoxelDefinitionId>();
    private readonly string value;
    internal string Key => value;
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
        var edge = path.Edge; var start = path.Start; var end = path.End; var rules = path.Rules; var seed = path.Seed;
        var values = new List<object?> { seed, edge.Id, edge.LengthMeters, edge.Tracks, edge.TrackLimit,
            start.Id, start.Position, end.Id, end.Position, rules, index, Terrain(edge.Terrain) };
        var segmentStart = index * RailwayCoordinates.SegmentLengthCells;
        var segmentEnd = Math.Min(path.LengthCells, segmentStart + RailwayCoordinates.SegmentLengthCells);
        foreach (var br in edge.Breaks.Where(item => RailwayCoordinates.BreakGapOverlaps(item.DistanceMeters, segmentStart, segmentEnd))
            .OrderBy(item => item.Id.Value).ThenBy(item => item.Track))
            values.Add(new object[] { "break", br.Id, br.Track, br.DistanceMeters });
        foreach (var work in edge.Works.Where(item => RailwayCoordinates.IsBreakRepair(item) &&
            edge.Breaks.Any(br => br.Id == item.RepairingBreak && RailwayCoordinates.BreakGapOverlaps(br.DistanceMeters, segmentStart, segmentEnd)))
            .OrderBy(item => item.Id.Value))
            values.Add(new object?[] { "repair", work.Id, work.Track, work.RepairingBreak });
        if (startTerrain) { values.Add(Terrain(start.Terrain)); values.Add(NodeLayout.ReadInputs(snapshot, start.Id)); }
        if (endTerrain) { values.Add(Terrain(end.Terrain)); values.Add(NodeLayout.ReadInputs(snapshot, end.Id)); }
        foreach (var id in terrains)
        {
            var lookup = snapshot.Content.FindTerrain(id);
            if (lookup is ContentLookup<TerrainAppearanceProjection>.Found found)
            {
                var t = found.Value;
                values.Add(new object[] { t.Id, t.SurfaceVoxel, t.SubsurfaceVoxel, t.RockVoxel,
                    t.LocalHeightNoise, t.DecorationTargetPerSquareMeter, t.Decorations.Select(item => item.Weight).ToArray() });
            }
            else values.Add(lookup);
        }
        foreach (var slot in slots)
        {
            if (snapshot.Content.FindTerrain(slot.Terrain) is not ContentLookup<TerrainAppearanceProjection>.Found found) { values.Add(null); continue; }
            var t = found.Value;
            if (slot.Role == RailwayModelRole.Decoration)
                values.Add(slot.Slot < t.Decorations.Length ? t.Decorations[slot.Slot].SceneModel : null);
            else
            {
                var model = t.RailwayModels.FirstOrDefault(item => item.Tracks == edge.Tracks);
                values.Add(slot.Role switch
                {
                    RailwayModelRole.Bridge => model?.Bridge,
                    RailwayModelRole.Tunnel => model?.TunnelPortal,
                    RailwayModelRole.RepairSite => model?.RepairSite,
                    _ => null
                });
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
                    s.EditLocks.Select(item => new object?[] { item.Element, item.Reference, item.Package,
                        item.BlockedOperations, item.Diagnostics }).ToArray(),
                    (found.Value is DesignEvaluation.Complete c ? c.Diagnostics : ((DesignEvaluation.Partial)found.Value).Diagnostics) });
            }
            else values.Add(new object[] { hash, lookup });
        }
        foreach (var id in voxels)
        {
            var lookup = snapshot.Content.FindVoxelAppearance(id);
            values.Add(lookup is ContentLookup<VoxelAppearanceProjection>.Found found ?
                new object[] { id, found.Value.AllowedOrientations, found.Value.Collision, found.Value.Presentation, found.Value.Behaviors } :
                new object[] { id, lookup });
        }
        return JsonSerializer.Serialize(values, ValueOptions);
    }
    private static object[] Terrain(TerrainView value) => [value.TypeId, value.Relief, value.FeatureIds];
}
