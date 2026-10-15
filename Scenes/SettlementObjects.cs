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
    private readonly Dictionary<VoxelDefinitionId, (VoxelAppearanceProjection Appearance, ImmutableArray<SceneVisual> Visuals)> resourceVisuals = [];
    private readonly Func<string, SceneObjectId> objectId;
    private readonly Func<IContentView, InstanceView, BuiltObject> build;
    private readonly Dictionary<BuildingId, BuiltObject> ready = [];
    private readonly Dictionary<BuildingId, Task<BuiltObject>> pending = [];
    private readonly SceneVehicles parked = new();
    private readonly CancellationTokenSource cancellation = new();

    internal SettlementObjects(Func<string, SceneObjectId> objectId,
        Func<IContentView, InstanceView, BuiltObject>? build = null)
    {
        this.objectId = objectId;
        this.build = build ?? ((content, instance) => WorldSceneSession.BuildObject(content, instance,
            new(Vector3.Zero, Quaternion.Identity)));
    }

    internal Task[] PendingWork => pending.Values.Cast<Task>().Concat(parked.PendingWork).ToArray();

    private static SettlementView? Settlement(SceneWorldSnapshot snapshot, NodeLayout layout)
    {
        var node = snapshot.Core.World.Nodes.Single(item => item.Id == layout.Node);
        return node.Occupant is NodeOccupant.Settlement settlement &&
            snapshot.Core.FindSettlement(settlement.Id) is Lookup<SettlementView>.Found found ? found.Value : null;
    }

    internal RenderedDesign Present(SceneWorldSnapshot snapshot, NodeLayout layout, ScenePoint origin,
        ImmutableArray<SceneFog>.Builder fog)
    {
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var settlement = Settlement(snapshot, layout);
        var buildings = new Dictionary<BuildingId, (InstanceView Instance, SettlementPlotLayout Plot)>();
        if (settlement is not null && layout.Template is { } template)
            foreach (var plot in settlement.Plots)
                if (plot.Occupant is PlotOccupant.Building building &&
                    template.Plots.FirstOrDefault(item => item.Number == plot.TemplatePlotNumber) is { } placement &&
                    snapshot.Core.FindInstance(new InstanceId.Building(building.Id)) is Lookup<InstanceView>.Found instance)
                    buildings.Add(building.Id, (instance.Value, placement));

        foreach (var id in ready.Keys.Where(id => !buildings.ContainsKey(id)).ToArray()) ready.Remove(id);
        foreach (var pair in pending.Where(pair => pair.Value.IsCompleted).ToArray())
        {
            pending.Remove(pair.Key);
            var body = pair.Value.GetAwaiter().GetResult();
            if (buildings.TryGetValue(pair.Key, out var current) && WorldSceneSession.Matches(body, current.Instance))
                ready[pair.Key] = body;
        }
        foreach (var pair in buildings.OrderBy(pair => pair.Key.Value))
        {
            var (instance, plot) = pair.Value;
            var placement = SceneGeometry.Placement(plot.At, plot.Orientation);
            var pose = placement with { Position = placement.Position - RailwayCoordinates.Relative(origin, default) };
            var hasBody = ready.TryGetValue(pair.Key, out var body);
            if (hasBody)
                Add(SceneGeometry.Place(WorldSceneSession.Attach(body!.Geometry, "building:" + pair.Key.Value + ":", objectId).Geometry, pose));
            else objects.Add(new(objectId("building:" + pair.Key.Value + ":"), null, pose, new WorldObjectId.Instance(instance.Id), [], [], []));
            if ((!hasBody || !WorldSceneSession.Matches(body!, instance)) && !pending.ContainsKey(pair.Key) && pending.Count < RailwayCoordinates.MaxConcurrentBuilds)
            {
                var content = snapshot.Content;
                pending.Add(pair.Key, Task.Run(() => build(content, instance), cancellation.Token));
            }
            if (!ready.TryGetValue(pair.Key, out var shown) || !WorldSceneSession.Matches(shown, instance))
            {
                if (snapshot.Content.FindDesign(instance.Design) is ContentLookup<DesignEvaluation>.Found evaluation &&
                    evaluation.Value is not DesignEvaluation.Failed && DesignGeometry.Projection(evaluation.Value).OccupiedBounds is { } bounds)
                    fog.Add(new(new(new(bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z),
                        new(bounds.Maximum.X + 1, bounds.Maximum.Y + 1, bounds.Maximum.Z + 1)), Pose: pose));
            }
        }

        var vehicles = (settlement?.ParkedVehicles ?? []).OrderBy(id => id.Value).Take(ParkedVehicleLimit).ToArray();
        parked.Retain(vehicles.Select(id => (InstanceId)new InstanceId.Vehicle(id)));
        var used = new double[layout.Parking.Length];
        foreach (var id in vehicles)
        {
            if (snapshot.Core.FindInstance(new InstanceId.Vehicle(id)) is not Lookup<InstanceView>.Found found) continue;
            var instance = found.Value;
            if (snapshot.Content.FindDesign(instance.Design) is not ContentLookup<DesignEvaluation>.Found design ||
                RailwayVehicles.Length(design.Value) is not > 0) continue;
            var length = RailwayVehicles.Length(design.Value)!.Value;
            var lineIndex = 0;
            while (lineIndex < layout.Parking.Length && used[lineIndex] + length > layout.Parking[lineIndex].Length) lineIndex++;
            if (lineIndex == layout.Parking.Length) continue;
            var line = layout.Parking[lineIndex];
            var center = RailwayCoordinates.Add(line.Start, line.Forward * (float)(used[lineIndex] + length / 2.0));
            used[lineIndex] += length + 4;
            var pose = new ScenePose(RailwayCoordinates.Relative(center, origin), RailwayCoordinates.Rotation(line.Forward));
            if (parked.Reconcile(snapshot.Content, instance, objectId) is { } rendered)
                Add(RailwayVehicles.Place(rendered, pose, 0));
            else if (design.Value is not DesignEvaluation.Failed)
            {
                var loading = SceneVehicles.Loading(instance, design.Value, pose, objectId);
                objects.Add(loading.Object);
                fog.Add(loading.Fog);
            }
        }
        Resources(snapshot, layout, origin, objects, problems);
        return new(objects.ToImmutable(), picking.ToImmutable(), problems.ToImmutable());

        void Add(RenderedDesign rendered)
        {
            objects.AddRange(rendered.Objects);
            picking.AddRange(rendered.Picking);
            problems.AddRange(rendered.Diagnostics);
        }
    }

    internal static ImmutableArray<NodeLayout.Stop> Platforms(SceneWorldSnapshot snapshot, NodeLayout layout,
        ImmutableArray<SceneProblem>.Builder problems)
    {
        var stops = new HashSet<NodeLayout.Stop>();
        var settlement = Settlement(snapshot, layout);
        if (settlement is not null)
            foreach (var plot in settlement.Plots)
            {
                if (plot.Size is not (PlotSize.SmallStation or PlotSize.LargeStation) ||
                    plot.Occupant is not PlotOccupant.Building building ||
                    snapshot.Core.FindInstance(new InstanceId.Building(building.Id)) is not Lookup<InstanceView>.Found instance ||
                    snapshot.Content.FindDesign(instance.Value.Design) is not ContentLookup<DesignEvaluation>.Found found ||
                    found.Value is DesignEvaluation.Failed || instance.Value.Structure.EffectiveCurrent <= 0) continue;
                var parts = found.Value switch
                {
                    DesignEvaluation.Complete { Core: { } core } => core.Design.Parts,
                    DesignEvaluation.Partial partial => partial.KnownSubtotal.Parts,
                    _ => []
                };
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
                        var pose = new ScenePose(Vector3.Zero, Quaternion.Identity);
                        for (var depth = 1; depth <= nested.NestedItemIndices.Length; depth++)
                        {
                            var path = nested.NestedItemIndices.Take(depth);
                            var parent = scene.NestedObjects.First(item => item.NestedItemIndices.SequenceEqual(path));
                            pose = SceneGeometry.Compose(pose, SceneGeometry.Placement(parent.At, parent.Orientation));
                        }
                        localZ = pose.Position.Z;
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
                var x = Math.Floor(layout.Bounds.Minimum.X + (layout.Bounds.Maximum.X - layout.Bounds.Minimum.X) *
                    RailwayRandom.Unit(snapshot.Core.World.WorldSeed, node.Id.Value, candidate, key));
                var z = Math.Floor(layout.Bounds.Minimum.Y + (layout.Bounds.Maximum.Y - layout.Bounds.Minimum.Y) *
                    RailwayRandom.Unit(snapshot.Core.World.WorldSeed, node.Id.Value, candidate, key ^ 0xCA013FACUL));
                var point = new ScenePoint(x, 0, z);
                if (layout.ContainsPlot(point) || !layout.ClearOfTracks(point, 17)) continue;
                point = point with { Y = Math.Floor(RailwayTerrain.NodeGround(snapshot, node, point)) + 1 };
                if (!resourceVisuals.TryGetValue(good.Definition, out var cached) || cached.Appearance != appearance.Value)
                {
                    var grid = new VoxelGrid([new(new(0, 0, 0), new(VoxelOrientation.PxPy, appearance.Value, null, null, null, []))]);
                    resourceVisuals[good.Definition] = cached = (appearance.Value, DesignGeometry.Meshes(grid, false));
                }
                objects.Add(new(objectId("resource:" + good.Definition.Value + ":" + candidate), null,
                    new(RailwayCoordinates.Relative(point, origin), Quaternion.Identity), null,
                    cached.Visuals, [], []));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        try { await Task.WhenAll(pending.Values); }
        catch (Exception) { /* Observe owned work when closing. */ }
        await parked.DisposeAsync();
        cancellation.Dispose();
    }
}
