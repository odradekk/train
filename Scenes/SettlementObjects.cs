using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class SettlementObjects : IAsyncDisposable
{
    internal const int ParkedVehicleLimit = 8;
    private const int ConstructionCopySpacing = 48;
    private readonly Dictionary<VoxelDefinitionId, (VoxelAppearanceProjection Appearance, VoxelGrid Grid, ImmutableArray<SceneVisual> Visuals)> resourceVisuals = [];
    private readonly Func<string, SceneObjectId> objectId;
    private readonly SceneVehicles buildings;
    private readonly SceneVehicles parked;
    private readonly Dictionary<InstanceId, (BuiltGeometry Source, BuiltGeometry Smoking)> facilitySmoke = [];
    private readonly Dictionary<(VoxelDefinitionId Good, int Candidate), ScenePoint?> resourcePositions = [];
    private NodeLayout.LairPlacement? resourceLair;
    private readonly HashSet<InstanceId> retainedBuildings = [];
    private readonly HashSet<InstanceId> retainedParked = [];
    private readonly HashSet<PlotId> retainedSites = [];
    private readonly Dictionary<DesignHash, BuiltGeometry> siteGeometry = [];
    private ICoreReadView? readView;
    private Lookup<SettlementView>? settlement;
    private readonly Dictionary<InstanceId, Lookup<InstanceView>> instances = [];

    internal SettlementObjects(Func<string, SceneObjectId> objectId,
        Func<IContentView, InstanceView, BuiltObject>? build = null,
        SceneVehicles.Cache? buildingCache = null, SceneVehicles.Cache? parkedCache = null,
        Func<IContentView, InstanceView, BuiltObject>? parkedBuild = null)
    {
        this.objectId = objectId;
        buildings = new(buildingCache, build ?? ((content, instance) => WorldSceneSession.BuildObject(content, instance,
            new(Vector3.Zero, Quaternion.Identity))));
        parked = new(parkedCache, parkedBuild);
    }

    internal Task[] PendingWork => buildings.PendingWork.Concat(parked.PendingWork).ToArray();
    internal bool HasPendingBuilds => buildings.HasPendingBuilds || parked.HasPendingBuilds;

    private Dictionary<BuildingId, (InstanceView Instance, SettlementPlotLayout Plot)> Buildings(
        SceneWorldSnapshot snapshot, NodeLayout layout)
    {
        var result = new Dictionary<BuildingId, (InstanceView, SettlementPlotLayout)>();
        if (Settlement(snapshot, layout) is { } settlement && layout.Template is { } template)
            foreach (var plot in settlement.Plots)
                if (plot.Occupant is PlotOccupant.Building building &&
                    template.Plots.FirstOrDefault(item => item.Number == plot.TemplatePlotNumber) is { } placement &&
                    Instance(snapshot.Core, new InstanceId.Building(building.Id)) is Lookup<InstanceView>.Found instance)
                    result.Add(building.Id, (instance.Value, placement));
        return result;
    }

    private sealed record Placement(InstanceView Instance, ScenePose Pose, SceneBounds? Bounds,
        ContentLookup<DesignEvaluation> Design, int? Length = null);

    private static SceneBounds? BuildingBounds(ContentLookup<DesignEvaluation> design) =>
        design is ContentLookup<DesignEvaluation>.Found found && found.Value is not DesignEvaluation.Failed &&
        DesignGeometry.Projection(found.Value).OccupiedBounds is { } bounds
            ? new(new(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z),
                new(bounds.Maximum.X + 1, bounds.Maximum.Y + 1, bounds.Maximum.Z + 1)) : null;

    private Placement[] BuildingPlacements(SceneWorldSnapshot snapshot, NodeLayout layout) =>
        Buildings(snapshot, layout).OrderBy(pair => pair.Key.Value).Select(pair =>
        {
            var design = snapshot.Content.FindDesign(pair.Value.Instance.Design);
            return new Placement(pair.Value.Instance, SceneGeometry.Placement(pair.Value.Plot.At, pair.Value.Plot.Orientation),
                BuildingBounds(design), design);
        }).ToArray();

    private Placement[] ParkedPlacements(SceneWorldSnapshot snapshot, NodeLayout layout)
    {
        var result = new List<Placement>();
        var used = new double[layout.Parking.Length];
        foreach (var id in (Settlement(snapshot, layout)?.ParkedVehicles ?? []).OrderBy(id => id.Value).Take(ParkedVehicleLimit))
        {
            if (Instance(snapshot.Core, new InstanceId.Vehicle(id)) is not Lookup<InstanceView>.Found found) continue;
            var instance = found.Value;
            var lookup = snapshot.Content.FindDesign(instance.Design);
            var length = RailwayVehicles.Length((lookup as ContentLookup<DesignEvaluation>.Found)?.Value);
            if (lookup is not ContentLookup<DesignEvaluation>.Found design || design.Value is DesignEvaluation.Failed || length is not > 0)
            {
                result.Add(new(instance, new(Vector3.Zero, Quaternion.Identity), null, lookup, length));
                continue;
            }
            var lineIndex = 0;
            while (lineIndex < layout.Parking.Length && used[lineIndex] + length.Value > layout.Parking[lineIndex].Length) lineIndex++;
            if (lineIndex == layout.Parking.Length) continue;
            var line = layout.Parking[lineIndex];
            var center = RailwayCoordinates.Add(line.Start, line.Forward * (float)(used[lineIndex] + length.Value / 2.0));
            used[lineIndex] += length.Value + 4;
            var pose = new ScenePose(RailwayCoordinates.Relative(center, default), RailwayCoordinates.Rotation(line.Forward));
            result.Add(new(instance, pose, SceneVehicles.VehicleBounds(DesignGeometry.Projection(design.Value),
                (InstanceDetails.Vehicle)instance.Details), lookup, length));
        }
        return result.ToArray();
    }

    private readonly record struct ConstructionSite(PlotId Plot, ScenePose Pose, SceneBounds Bounds, int CopiesX, int CopiesZ);

    private ConstructionSite[] ConstructionPlacements(SceneWorldSnapshot snapshot, NodeLayout layout)
    {
        if (Settlement(snapshot, layout) is not { } settlement || layout.Template is not { } template) return [];
        var result = new List<ConstructionSite>();
        foreach (var plot in settlement.Plots.OrderBy(item => item.Id.Value))
        {
            if (plot.Occupant is not PlotOccupant.Construction) continue;
            if (template.Plots.FirstOrDefault(item => item.Number == plot.TemplatePlotNumber) is not { } placement) continue;
            var (x, z, _) = NodeSettlement.Size(plot.Size);
            result.Add(new(plot.Id, SceneGeometry.Placement(placement.At, placement.Orientation),
                new(new(-x / 2f, 0, -z / 2f), new(x / 2f, 4, z / 2f)),
                Math.Max(1, x / ConstructionCopySpacing), Math.Max(1, z / ConstructionCopySpacing)));
        }
        return result.ToArray();
    }

    private BuiltGeometry? ConstructionGeometry(SceneWorldSnapshot snapshot, NodeLayout layout,
        out ContentReference? reference, out ImmutableArray<ContentDiagnostic> diagnostics, out string detail)
    {
        var node = snapshot.Core.World.Nodes.Single(item => item.Id == layout.Node);
        var terrainLookup = snapshot.Content.FindTerrain(node.Terrain.TypeId);
        if (terrainLookup is not ContentLookup<TerrainAppearanceProjection>.Found terrain)
        {
            reference = new ContentReference.Core(new CoreDefinitionReference.Terrain(node.Terrain.TypeId));
            diagnostics = terrainLookup is ContentLookup<TerrainAppearanceProjection>.Missing missing ? missing.Diagnostics : [];
            detail = "Terrain appearance is missing.";
            return null;
        }
        var hash = terrain.Value.ConstructionSite;
        var design = snapshot.Content.FindDesign(hash);
        if (design is ContentLookup<DesignEvaluation>.Found found && found.Value is DesignEvaluation.Complete or DesignEvaluation.Partial)
        {
            reference = null;
            diagnostics = [];
            detail = "";
            if (!siteGeometry.TryGetValue(hash, out var built))
                siteGeometry[hash] = built = WorldSceneSession.BuildGeometry(assign =>
                    DesignGeometry.Build(snapshot.Content, found.Value, null, new(Vector3.Zero, Quaternion.Identity), assign));
            return built;
        }
        reference = new ContentReference.Design(hash);
        diagnostics = design switch
        {
            ContentLookup<DesignEvaluation>.Missing missing => missing.Diagnostics,
            ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Failed failed } => failed.Diagnostics,
            _ => []
        };
        detail = "Construction scene model is missing.";
        return null;
    }

    private static TItem[] Select<TItem, TKey>(TItem[] items, HashSet<TKey> retained, Func<TItem, TKey> key,
        Func<TItem, SceneBounds?> bounds, Func<TItem, ScenePose> pose, NodeRange? range, NodeRange? prefetch)
        where TKey : notnull
    {
        var selected = items.Where(item =>
        {
            var area = bounds(item);
            var at = pose(item);
            return range is null || area is null ||
                range.Value.Intersects(area, at) || prefetch is { } ahead && ahead.Intersects(area, at) ||
                retained.Contains(key(item)) && range.Value.Around(NodeStreaming.RetainCells).Intersects(area, at);
        }).ToArray();
        retained.Clear();
        retained.UnionWith(selected.Select(key));
        return selected.OrderBy(item =>
        {
            if (range is not { } area) return 0d;
            var at = pose(item).Position;
            return Math.Pow(at.X - area.Focus.X, 2) + Math.Pow(at.Z - area.Focus.Z, 2);
        }).ToArray();
    }

    internal void Pump(SceneWorldSnapshot snapshot, NodeLayout layout, NodeRange? range = null, bool allowBuild = true,
        NodeRange? prefetch = null)
    {
        var placed = Select(BuildingPlacements(snapshot, layout), retainedBuildings,
            item => item.Instance.Id, item => item.Bounds, item => item.Pose, range, prefetch);
        buildings.Retain(placed.Select(item => item.Instance.Id));
        foreach (var id in facilitySmoke.Keys.Where(id => !retainedBuildings.Contains(id)).ToArray()) facilitySmoke.Remove(id);
        foreach (var item in placed) buildings.Prepare(snapshot.Content, item.Instance, allowBuild);
        var vehicles = Select(ParkedPlacements(snapshot, layout), retainedParked,
            item => item.Instance.Id, item => item.Bounds, item => item.Pose, range, prefetch);
        parked.Retain(vehicles.Select(item => item.Instance.Id));
        foreach (var item in vehicles)
            if (item.Bounds is not null) parked.Prepare(snapshot.Content, item.Instance, allowBuild);
    }

    private void Observe(ICoreReadView core)
    {
        if (ReferenceEquals(readView, core)) return;
        readView = core;
        settlement = null;
        instances.Clear();
    }

    private Lookup<InstanceView> Instance(ICoreReadView core, InstanceId id, bool cacheProjections = true)
    {
        if (!cacheProjections) return core.FindInstance(id);
        Observe(core);
        if (!instances.TryGetValue(id, out var instance))
            instances.Add(id, instance = core.FindInstance(id));
        return instance;
    }

    private SettlementView? Settlement(SceneWorldSnapshot snapshot, NodeLayout layout, bool cacheProjections = true)
    {
        if (cacheProjections) Observe(snapshot.Core);
        var node = snapshot.Core.World.Nodes.Single(item => item.Id == layout.Node);
        return node.Occupant is NodeOccupant.Settlement owner &&
            (cacheProjections ? (settlement ??= snapshot.Core.FindSettlement(owner.Id)) : snapshot.Core.FindSettlement(owner.Id))
                is Lookup<SettlementView>.Found found ? found.Value : null;
    }

    internal RenderedDesign Present(SceneWorldSnapshot snapshot, NodeLayout layout, ScenePoint origin,
        ImmutableArray<SceneFog>.Builder fog, Func<string, SceneObjectId?> findId,
        NodeRange? range = null, IReadOnlyCollection<GridColumn>? terrainChunks = null,
        NodeRange? prefetch = null, PresentationState? presentation = null, double seconds = 0)
    {
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        Pump(snapshot, layout, range, prefetch: prefetch);
        var allBuildings = BuildingPlacements(snapshot, layout);
        var placed = Select(allBuildings, retainedBuildings, item => item.Instance.Id, item => item.Bounds, item => item.Pose, range, prefetch);
        Fog(allBuildings, placed, item => item.Instance.Id, item => item.Bounds, item => item.Pose);
        foreach (var item in placed)
        {
            var instance = item.Instance;
            var buildingId = ((InstanceId.Building)instance.Id).Id;
            var pose = item.Pose with { Position = item.Pose.Position - RailwayCoordinates.Relative(origin, default) };
            var body = buildings.Prepare(snapshot.Content, instance);
            var hasBody = body is not null;
            if (hasBody)
            {
                var geometry = body!.Geometry;
                if (Settlement(snapshot, layout)?.Facilities.Any(facility =>
                        facility.Key.Building == buildingId && facility.State == ProductionState.Running) == true &&
                    item.Bounds is { } occupied)
                {
                    if (!facilitySmoke.TryGetValue(instance.Id, out var cached) || !ReferenceEquals(cached.Source, geometry))
                        facilitySmoke[instance.Id] = cached = (geometry, WithFacilityActivity(geometry, instance, occupied));
                    geometry = cached.Smoking;
                }
                else facilitySmoke.Remove(instance.Id);
                Add(SceneGeometry.Place(WorldSceneSession.Attach(geometry, "building:" + buildingId.Value + ":", objectId).Geometry, pose));
                presentation?.ApplyTurrets(objects, instance, "building:" + buildingId.Value + ":", findId, seconds, snapshot, origin);
            }
            else
            {
                objects.Add(new(objectId("building:" + buildingId.Value + ":"), null, pose, new WorldObjectId.Instance(instance.Id), [], [], []));
                if (snapshot.Content.FindDesign(instance.Design) is ContentLookup<DesignEvaluation>.Found evaluation &&
                    evaluation.Value is not DesignEvaluation.Failed && DesignGeometry.Projection(evaluation.Value).OccupiedBounds is { } bounds)
                    fog.Add(new(new(new(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z),
                        new(bounds.Maximum.X + 1, bounds.Maximum.Y + 1, bounds.Maximum.Z + 1)), Pose: pose));
            }
        }

        var shift = RailwayCoordinates.Relative(origin, default);
        var sites = ConstructionPlacements(snapshot, layout);
        var placedSites = Select(sites, retainedSites, item => item.Plot, item => item.Bounds, item => item.Pose, range, prefetch);
        Fog(sites, placedSites, item => item.Plot, item => item.Bounds, item => item.Pose);
        BuiltGeometry? siteBuilt = null;
        ContentReference? missingReference = null;
        var missingDiagnostics = ImmutableArray<ContentDiagnostic>.Empty;
        var missingDetail = "";
        if (placedSites.Length > 0)
            siteBuilt = ConstructionGeometry(snapshot, layout, out missingReference, out missingDiagnostics, out missingDetail);
        foreach (var item in placedSites)
        {
            if (siteBuilt is null)
            {
                objects.Add(new(objectId("construction:" + item.Plot.Value + ":missing"), null,
                    item.Pose with { Position = item.Pose.Position - shift }, null,
                    [new SceneVisual.Missing(missingReference!, false)], [], []));
                RailwayTerrain.ReportMissing(missingReference!, missingDiagnostics, missingDetail, problems);
                continue;
            }
            var index = 0;
            for (var iz = 0; iz < item.CopiesZ; iz++)
            for (var ix = 0; ix < item.CopiesX; ix++, index++)
            {
                var local = new Vector3((ix - (item.CopiesX - 1) * 0.5f) * ConstructionCopySpacing, 0,
                    (iz - (item.CopiesZ - 1) * 0.5f) * ConstructionCopySpacing);
                var pose = new ScenePose(item.Pose.Position + Vector3.Transform(local, item.Pose.Rotation) - shift, item.Pose.Rotation);
                var copy = SceneGeometry.Place(WorldSceneSession.Attach(siteBuilt,
                    "construction:" + item.Plot.Value + ":" + index + ":", objectId).Geometry, pose);
                objects.AddRange(copy.Objects);
                picking.AddRange(copy.Picking);
                problems.AddRange(copy.Diagnostics);
            }
        }

        var allParked = ParkedPlacements(snapshot, layout);
        var vehicles = Select(allParked, retainedParked, item => item.Instance.Id, item => item.Bounds, item => item.Pose, range, prefetch);
        Fog(allParked, vehicles, item => item.Instance.Id, item => item.Bounds, item => item.Pose);
        foreach (var item in vehicles)
        {
            var instance = item.Instance;
            var pose = item.Pose with { Position = item.Pose.Position - RailwayCoordinates.Relative(origin, default) };
            if (item.Bounds is null)
            {
                var unplaced = SceneVehicles.Unplaced(snapshot.Content, instance, item.Design, item.Length, pose, objectId);
                problems.AddRange(unplaced.Diagnostics);
                continue;
            }
            var design = (ContentLookup<DesignEvaluation>.Found)item.Design;
            if (parked.Reconcile(snapshot.Content, instance, objectId) is { } rendered)
            {
                Add(RailwayVehicles.Place(rendered, pose, 0));
                presentation?.ApplyTurrets(objects, instance, "vehicle:" + instance.Id + ":", findId, seconds, snapshot, origin);
            }
            else
            {
                var loading = SceneVehicles.Loading(instance, design.Value, pose, objectId);
                objects.Add(loading.Object);
                fog.Add(loading.Fog);
            }
        }
        Resources(snapshot, layout, origin, objects, problems);
        return new(objects.ToImmutable(), picking.ToImmutable(), problems.ToImmutable());

        void Fog<TItem, TKey>(TItem[] all, TItem[] selected, Func<TItem, TKey> key, Func<TItem, SceneBounds?> bounds,
            Func<TItem, ScenePose> pose) where TKey : notnull
        {
            if (terrainChunks is null || terrainChunks.Count == 0) return;
            var chosen = selected.Select(key).ToHashSet();
            foreach (var item in all)
            {
                if (chosen.Contains(key(item)) || bounds(item) is not { } area) continue;
                var at = pose(item);
                var region = new SceneFog(area, Pose: at);
                if (NodeStreaming.OverlapsTerrain(region.WorldBounds, terrainChunks))
                    fog.Add(region with { Pose = at with
                    { Position = at.Position - RailwayCoordinates.Relative(origin, default) } });
            }
        }

        void Add(RenderedDesign rendered)
        {
            objects.AddRange(rendered.Objects);
            picking.AddRange(rendered.Picking);
            problems.AddRange(rendered.Diagnostics);
        }
    }

    private static BuiltGeometry WithFacilityActivity(BuiltGeometry source, InstanceView instance, SceneBounds bounds)
    {
        var root = source.Geometry.Objects.Single(item => item.Parent is null);
        var lightId = new SceneObjectId(source.Keys.Keys.Max(id => id.Value) + 1);
        var centerX = (bounds.Minimum.X + bounds.Maximum.X) / 2f;
        var light = new SceneObject(lightId, root.Id,
            new(new(centerX, bounds.Maximum.Y - 1, bounds.Maximum.Z + .2f), Quaternion.Identity), root.Subject, [],
            [new(new("#ffd08a"), 12, StructureDamage.StructureLightDamage(instance.Structure),
                StructureDamage.Seed(instance.Id, 0, default, 7))], []);
        var smoking = source.Geometry.Objects.Select(item => item.Id != root.Id ? item : item with
        {
            Effects = item.Effects.Add(new(PersistentEffectKind.FacilitySmoke,
                new(centerX, bounds.Maximum.Y, (bounds.Minimum.Z + bounds.Maximum.Z) / 2f),
                .25f, StructureDamage.Seed(instance.Id, 0, default, 6)))
        }).Append(light).ToImmutableArray();
        return new(source.Geometry with { Objects = smoking }, source.Keys.Add(lightId, ":light:facility"));
    }

    internal ImmutableArray<NodeLayout.Stop> Platforms(SceneWorldSnapshot snapshot, NodeLayout layout,
        ImmutableArray<SceneProblem>.Builder problems, bool cacheProjections = true)
    {
        var stops = new HashSet<NodeLayout.Stop>();
        var settlement = Settlement(snapshot, layout, cacheProjections);
        if (settlement is not null)
            foreach (var plot in settlement.Plots)
            {
                if (plot.Size is not (PlotSize.SmallStation or PlotSize.LargeStation) ||
                    plot.Occupant is not PlotOccupant.Building building ||
                    Instance(snapshot.Core, new InstanceId.Building(building.Id), cacheProjections) is not Lookup<InstanceView>.Found instance ||
                    snapshot.Content.FindDesign(instance.Value.Design) is not ContentLookup<DesignEvaluation>.Found found ||
                    found.Value is DesignEvaluation.Failed || instance.Value.Structure.EffectiveCurrent <= 0) continue;
                var parts = DesignGeometry.Parts(found.Value);
                var scene = DesignGeometry.Projection(found.Value);
                var destroyed = WorldSceneSession.Destroyed(instance.Value);
                foreach (var part in parts.Where(part => !part.Missing && !destroyed.Contains(part.Id) &&
                    instance.Value.Parts.Any(damage => damage.Part == part.Id && damage.Durability.EffectiveCurrent > 0) &&
                    part.Capabilities.Any(capability => capability is PartCapability.Platform)))
                {
                    double? localZ = null;
                    var element = scene.Elements.FirstOrDefault(item => item.Number == part.Id && !item.Missing);
                    if (element?.OccupiedCells is ComputedValue<ImmutableArray<GridPosition>>.Known { Value.IsEmpty: false } cells)
                        localZ = cells.Value.Average(cell => (double)cell.Z);
                    else if (scene.Voxels.FirstOrDefault(voxel => voxel.Number == part.Id && !voxel.Missing) is { } voxel)
                        localZ = voxel.At.Z;
                    else if (scene.NestedObjects.FirstOrDefault(item => item.Details is NestedObjectDetails.FunctionalComposite composite && composite.Number == part.Id) is { } nested)
                    {
                        localZ = SceneGeometry.NestedPose(scene, nested.NestedItemIndices).Position.Z;
                    }
                    if (localZ is null) continue;
                    var stop = layout.Stops.Where(stop => stop.PlotNumber == plot.TemplatePlotNumber)
                        .OrderBy(stop => Math.Abs(stop.LocalZ - localZ.Value)).ThenBy(stop => stop.LocalZ).FirstOrDefault();
                    if (stop is not null) stops.Add(stop);
                }
            }
        var expected = snapshot.Core.World.Nodes.Single(node => node.Id == layout.Node).Platforms;
        if (stops.Count != expected)
        {
            problems.Add(new("station_platform_mismatch", SceneStage.Reconcile, new WorldObjectId.Node(layout.Node),
                null, null, null, "Built station platform parts do not match the authoritative platform count."));
            return [];
        }
        return stops.OrderBy(stop => stop.PlotNumber).ThenBy(stop => stop.LocalZ).ToImmutableArray();
    }

    private void Resources(SceneWorldSnapshot snapshot, NodeLayout layout, ScenePoint origin,
        ImmutableArray<SceneObject>.Builder objects, ImmutableArray<SceneProblem>.Builder problems)
    {
        var node = snapshot.Core.World.Nodes.Single(item => item.Id == layout.Node);
        if (resourceLair != layout.Lair)
        {
            resourcePositions.Clear();
            resourceLair = layout.Lair;
        }
        foreach (var resource in node.Resources)
        {
            if (resource.Quantity <= 0 || resource.Good is not GoodId.Voxel good) continue;
            var reference = new ContentReference.Core(new CoreDefinitionReference.Voxel(good.Definition));
            var lookup = snapshot.Content.FindVoxelAppearance(good.Definition);
            if (lookup is not ContentLookup<VoxelAppearanceProjection>.Found appearance)
            {
                RailwayTerrain.ReportMissing(reference, lookup is ContentLookup<VoxelAppearanceProjection>.Missing missing ? missing.Diagnostics : [],
                    "Missing resource voxel appearance.", problems, new WorldObjectId.Node(layout.Node));
                continue;
            }
            var count = resource.Renewal is ResourceRenewal.Renewable renewable
                ? (int)Math.Ceiling(8 * Math.Clamp((double)resource.Quantity / Math.Max(1, renewable.Maximum), 0, 1)) : 8;
            var key = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(good.Definition.Value)));
            for (var candidate = 0; candidate < count; candidate++)
            {
                var positionKey = (good.Definition, candidate);
                if (!resourcePositions.TryGetValue(positionKey, out var position))
                {
                    var x = Math.Floor(layout.Bounds.Minimum.X + (layout.Bounds.Maximum.X - layout.Bounds.Minimum.X) *
                        RailwayRandom.Unit(snapshot.Core.World.WorldSeed, node.Id.Value, candidate, key));
                    var z = Math.Floor(layout.Bounds.Minimum.Y + (layout.Bounds.Maximum.Y - layout.Bounds.Minimum.Y) *
                        RailwayRandom.Unit(snapshot.Core.World.WorldSeed, node.Id.Value, candidate, key ^ 0xCA013FACUL));
                    var candidatePoint = new ScenePoint(x, 0, z);
                    position = layout.ContainsPlot(candidatePoint) || InLair(layout, candidatePoint) ||
                        !layout.ClearOfTracks(candidatePoint, RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth)
                        ? null : candidatePoint with { Y = Math.Floor(RailwayTerrain.NodeGround(snapshot, node, candidatePoint, layout.Template)) + 1 };
                    resourcePositions.Add(positionKey, position);
                }
                if (position is not { } point) continue;
                if (!resourceVisuals.TryGetValue(good.Definition, out var cached) || cached.Appearance != appearance.Value)
                {
                    var grid = new VoxelGrid([new(new(0, 0, 0), new(VoxelOrientation.PxPy, appearance.Value, null,
                        BuiltInResourcePaints.For(good.Definition), null, []))]);
                    resourceVisuals[good.Definition] = cached = (appearance.Value, grid, DesignGeometry.Meshes(grid, false));
                }
                var objectKey = "resource:" + good.Definition.Value + ":" + candidate;
                var resourceId = objectId(objectKey);
                objects.Add(new(resourceId, null,
                    new(RailwayCoordinates.Relative(point, origin), Quaternion.Identity), null,
                    cached.Visuals, [], []));
                objects.AddRange(DesignGeometry.Models(cached.Grid, resourceId, null,
                    cell => objectId(objectKey + ":model:" + cell.At), false));
            }
        }
    }

    private static bool InLair(NodeLayout layout, ScenePoint point)
    {
        if (layout.Lair is not { } lair || NodeLair.Region(lair) is not { } region) return false;
        var blend = NodeSettlement.FlattenBlend;
        return point.X >= region.Minimum.X - blend && point.X < region.Maximum.X + blend &&
            point.Z >= region.Minimum.Y - blend && point.Z < region.Maximum.Y + blend;
    }

    public async ValueTask DisposeAsync()
    {
        readView = null;
        settlement = null;
        instances.Clear();
        await buildings.DisposeAsync();
        await parked.DisposeAsync();
    }
}
