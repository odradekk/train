using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeLairTests
{
    private static readonly VoxelDefinitionId LairVoxel = new("test:lair-block");
    private static readonly LairTypeId Type = new("test:nest");
    private static readonly LairId Id = new(1);
    private const double Clearance = RailwayCoordinates.BedBaseHalfWidth + RailwayCoordinates.BedBlendWidth;
    private const double Radius = 4;
    private static readonly SceneFrameRequest Request = new(1, null, SceneDebugLayer.None);

    [TestMethod]
    public void PlacedModelsOccupyTranslatedCellsAndRepeat()
    {
        var line = Model("test:lair-line", [new(0, 2, 0), new(1, 2, 0), new(2, 2, 0)], new(new(0, 0), new(2, 0), 5));
        var bend = Model("test:lair-bend", [new(0, 4, 0), new(0, 4, 1), new(1, 4, 1)], null);
        var first = Build(World(Found(line)));
        var repeat = Build(World(Found(line)));
        var other = Build(World(Found(bend)));
        AssertModel(first, line, 5, rightSide: true);
        AssertModel(repeat, line, 5, rightSide: true);
        AssertModel(other, bend, 4, rightSide: true);
        CollectionAssert.AreEqual(LairCells(first), LairCells(repeat));
        CollectionAssert.AreNotEqual(LairCells(first), LairCells(other));
        var chunk = new GridColumn(0, 0);
        var inputs = NodeScene.Prepare(World(Found(line)), first.Layout).ChunkInputs(chunk);
        Assert.AreEqual(inputs, NodeScene.Prepare(World(Found(line)), repeat.Layout).ChunkInputs(chunk));
        Assert.AreNotEqual(inputs, NodeScene.Prepare(World(Found(bend)), other.Layout).ChunkInputs(chunk));
    }

    [TestMethod]
    public void ClearanceIgnoresBuiltTrackCountAndDistantChunksMatchAnEmptyNode()
    {
        var model = Model("test:lair-line", [new(0, 2, 0), new(1, 2, 0), new(2, 2, 0)], new(new(0, 0), new(2, 0), 5));
        var bare = Build(World(Found(model), tracks: 0, trackLimit: 2, sameSideEntry: true));
        var partial = Build(World(Found(model), tracks: 1, trackLimit: 2, sameSideEntry: true));
        var full = Build(World(Found(model), tracks: 2, trackLimit: 2, sameSideEntry: true));
        Assert.AreEqual(3, bare.Layout.Entries.Length);
        Assert.IsTrue(bare.Layout.AllTurnRoutes().Any());
        Assert.AreEqual(bare.Layout.Lair, partial.Layout.Lair);
        Assert.AreEqual(bare.Layout.Lair, full.Layout.Lair);
        CollectionAssert.AreEqual(LairCells(bare), LairCells(partial));
        CollectionAssert.AreEqual(LairCells(bare), LairCells(full));
        AssertPlacement(bare.Layout, model.Scene, rightSide: false);
        CompareFar(bare, Build(World(Found(model), occupant: false, tracks: 0, trackLimit: 2, sameSideEntry: true)));
    }

    [TestMethod]
    public async Task LairPoseMatchesBakedModelAndFrameOrigin()
    {
        var model = Model("test:lair-anchor", [new(3, 2, -4)], null);
        var snapshot = World(Found(model), light: true);
        var built = Build(snapshot);
        var origin = new ScenePoint(17, 3, -29);
        var frame = await Present(snapshot, built, origin);
        var anchor = frame.Objects.Single(item => item.Subject == new WorldObjectId.Lair(Id));
        var cell = built.Parts.Values.SelectMany(CellsOf).Single(item => item.Appearance?.Definition == LairVoxel);
        var local = model.Scene.Voxels.Single().At;
        var pose = ((Lookup<ScenePose>.Found)frame.FindPose(new WorldObjectId.Lair(Id))).Value;
        Assert.AreEqual(new Vector3((float)(cell.At.X - local.X - origin.X), (float)-origin.Y,
            (float)(cell.At.Z - local.Z - origin.Z)), pose.Position);
        Assert.AreEqual(Quaternion.Identity, pose.Rotation);
        Assert.IsNull(anchor.Parent);
        Assert.IsTrue(anchor.Visuals.IsEmpty && anchor.Lights.IsEmpty && anchor.Effects.IsEmpty);
        var eye = frame.Objects.Single(item => !item.Lights.IsEmpty);
        var parent = frame.Objects.Single(item => item.Id == eye.Parent);
        Assert.AreEqual(new Vector3((float)(cell.At.X + .5 - origin.X), (float)(cell.At.Y + .5 - origin.Y),
            (float)(cell.At.Z + .5 - origin.Z)), SceneGeometry.Compose(parent.Pose, eye.Pose).Position);
        Assert.AreEqual(0, eye.Lights.Single().Damage);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingLairContentMatchesEmptyTerrainAndReportsTheGap(bool reported)
    {
        var reference = new ContentReference.Core(new CoreDefinitionReference.Lair(Type));
        var diagnostic = new ContentDiagnostic("missing_lair_blueprint", ContentDiagnosticSeverity.Error, "lookup", reference,
            new(new PackageId("lair-package"), "lairs.json"), "scene_model", null, "The lair blueprint is absent.");
        ContentLookup<LairAppearanceProjection> lookup = reported
            ? new ContentLookup<LairAppearanceProjection>.Missing([diagnostic])
            : new ContentLookup<LairAppearanceProjection>.NotFound();
        var snapshot = World(lookup);
        var built = Build(snapshot);
        var empty = Build(World(lookup, occupant: false));
        Assert.AreEqual(0, LairCells(built).Length);
        CollectionAssert.AreEquivalent(empty.Parts.Keys.ToArray(), built.Parts.Keys.ToArray());
        foreach (var key in empty.Parts.Keys) NodeFixtures.AssertPartEqual(empty.Parts[key], built.Parts[key]);
        var frame = await Present(snapshot, built);
        Assert.IsInstanceOfType<Lookup<ScenePose>.Missing>(frame.FindPose(new WorldObjectId.Lair(Id)));
        var missing = frame.Diagnostics.Single(problem => problem.Code == "content_missing");
        Assert.AreEqual(reference, missing.Content);
        Assert.AreEqual(new WorldObjectId.Node(NodeFixtures.NodeId), missing.Subject);
        Assert.AreEqual(SceneStage.Build, missing.Stage);
        Assert.AreEqual(reported ? diagnostic.Source!.Package : null, missing.Package);
        Assert.AreEqual(reported ? 1 : 0, frame.Diagnostics.Count(problem => problem.Code == diagnostic.Code));
    }

    [TestMethod]
    public async Task MissingLairReportFollowsTheCurrentOccupant()
    {
        var reference = new ContentReference.Core(new CoreDefinitionReference.Lair(Type));
        var lookup = new ContentLookup<LairAppearanceProjection>.NotFound();
        var missing = World(lookup);
        var empty = World(lookup, occupant: false);
        var built = Build(missing);
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(missing, NodeFixtures.NodeId, new(), SceneFixtures.NextId(), vehicles, new(),
            _ => null, (_, _, _, _) => built);
        await scene.BuildWork!;
        scene.Present(missing, Request, NodeFixtures.NextStamp(scene), null, default);
        await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        var opened = scene.Present(missing, Request, NodeFixtures.NextStamp(scene), null, default);
        Assert.IsTrue(HasLairGap(opened, reference));
        Assert.IsFalse(opened.Node!.Problems.Any(problem => problem.Content == reference));
        var cleared = scene.Present(empty, Request, NodeFixtures.NextStamp(scene), opened, default);
        Assert.IsFalse(HasLairGap(cleared, reference));
        var appeared = scene.Present(missing, Request, NodeFixtures.NextStamp(scene), cleared, default);
        Assert.IsTrue(HasLairGap(appeared, reference));
    }

    [TestMethod]
    public async Task ClearingAndPlacingALairRefreshesTerrainAndResources()
    {
        var model = Model("test:lair-line", [new(0, 2, 0), new(1, 2, 0), new(2, 2, 0)], new(new(0, 0), new(2, 0), 5));
        var lair = World(Found(model), resources: true, light: true);
        var empty = World(Found(model), occupant: false, resources: true, light: true);
        var layout = NodeLayout.Build(lair, NodeFixtures.NodeId);
        var shift = layout.Lair!;
        var column = new GridColumn(shift.ShiftX, shift.ShiftZ);
        var modelChunk = ChunkOf(column);
        var far = new GridColumn(modelChunk.X, modelChunk.Z - VoxelGrid.ChunkSize * 2);
        var built = new System.Collections.Concurrent.ConcurrentDictionary<GridColumn, NodeScene.Part>();
        var ids = new Dictionary<string, SceneObjectId>();
        var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.Sleeper),
            [new SceneVertex(Vector3.Zero, Vector3.UnitY, Vector2.Zero, Vector2.Zero, ExposedFaceEdges.None, 0, 0)], [0, 0, 0]);
        NodeScene.Part Stub(NodeScene.Preparation data, GridColumn from) => new(data.ChunkInputs(from),
            WorldSceneSession.BuildGeometry(id => new([new(id(""), null, SceneFixtures.Identity, null,
                [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], [])], [], [])),
            ImmutableDictionary<GridColumn, double>.Empty);
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(lair, NodeFixtures.NodeId, new(), key =>
            ids.TryGetValue(key, out var id) ? id : ids[key] = new((ulong)ids.Count + 1), vehicles, new(), _ => null,
            buildChunk: (data, from, old, _) =>
            {
                if (from != modelChunk && from != far) return old?.Inputs == data.ChunkInputs(from) ? old : Stub(data, from);
                var part = data.BuildChunk(from, old);
                built[from] = part;
                return part;
            },
            buildRail: (data, segment, old, _) => old?.Inputs == segment.Inputs ? old : new(segment.Inputs,
                WorldSceneSession.BuildGeometry(id => new([new(id(""), null, SceneFixtures.Identity, null,
                    [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], [])], [], [])),
                ImmutableDictionary<GridColumn, double>.Empty));
        var opened = await Settle(scene, lair);
        var anchor = opened.Objects.Single(item => item.Subject == new WorldObjectId.Lair(Id));
        Assert.AreEqual(3, opened.Objects.Sum(item => item.Lights.Length));
        Assert.IsTrue(LairCells(built[modelChunk]).Length > 0);
        Assert.AreEqual(5, ((Lookup<double>.Found)opened.TerrainHeight(column.X + 0.5, column.Z + 0.5)).Value);
        var cleared = await Refresh(scene, empty, opened);
        Assert.AreEqual(0, cleared.Objects.Sum(item => item.Lights.Length));
        Assert.IsInstanceOfType<Lookup<ScenePose>.Missing>(cleared.FindPose(new WorldObjectId.Lair(Id)));
        var clearedModel = built[modelChunk];
        var clearedFar = built[far];
        var returned = await Refresh(scene, lair, cleared);
        CollectionAssert.AreEqual(opened.Objects.Where(item => !item.Lights.IsEmpty).Select(item => item.Id).ToArray(),
            returned.Objects.Where(item => !item.Lights.IsEmpty).Select(item => item.Id).ToArray());
        Assert.AreEqual(anchor.Id, returned.Objects.Single(item => item.Subject == new WorldObjectId.Lair(Id)).Id);
        Assert.IsNotNull(opened.Node!.Lair);
        Assert.IsNull(cleared.Node!.Lair);
        Assert.AreEqual(opened.Node.Lair, returned.Node!.Lair);
        Assert.AreEqual(0, LairCells(clearedModel).Length);
        Assert.IsTrue(LairCells(built[modelChunk]).Length > 0);
        var emptyLayout = NodeLayout.Build(empty, NodeFixtures.NodeId);
        var emptyPrepared = NodeScene.Prepare(empty, emptyLayout);
        var emptyModel = emptyPrepared.BuildChunk(modelChunk);
        AssertSameChunk(emptyModel, clearedModel);
        AssertSameChunk(emptyPrepared.BuildChunk(far), clearedFar);
        Assert.AreEqual(emptyModel.Heights[column],
            ((Lookup<double>.Found)cleared.TerrainHeight(column.X + 0.5, column.Z + 0.5)).Value);
        CollectionAssert.AreEquivalent(await ResourcePoses(empty, emptyLayout), ResourcePoses(cleared, ids));
    }

    [TestMethod]
    public async Task ResourceCandidatesInsideTheLairRegionAreRejected()
    {
        var model = Model("test:lair-line", [new(0, 2, 0), new(1, 2, 0), new(2, 2, 0)], new(new(0, 0), new(2, 0), 5));
        var snapshot = World(Found(model), resources: true);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var region = NodeLair.Region(layout.Lair!)!;
        const int blend = 8;
        (ulong Seed, int Candidate, ScenePoint Point) inside = default;
        (int Candidate, ScenePoint Point) outside = default;
        var found = false;
        for (ulong seed = 1; seed < 250_000 && !found; seed++)
        {
            (int Candidate, ScenePoint Point)? inRegion = null;
            (int Candidate, ScenePoint Point)? clear = null;
            for (var candidate = 0; candidate < 8; candidate++)
            {
                var point = CandidatePoint(layout, seed, candidate);
                if (layout.ContainsPlot(point) || !layout.ClearOfTracks(point, Clearance)) continue;
                if (InExpanded(region, point, blend) && !InExpanded(region, point, 0)) inRegion ??= (candidate, point);
                else if (!InExpanded(region, point, blend)) clear ??= (candidate, point);
            }
            if (inRegion is { } blocked && clear is { } shown)
            {
                inside = (seed, blocked.Candidate, blocked.Point);
                outside = shown;
                found = true;
            }
        }
        Assert.IsTrue(found, "No seed places one clear candidate in the flatten blend and another outside it.");
        var seeded = RailwayFixtures.ReplaceCore(snapshot, worldSeed: inside.Seed);
        var ids = new Dictionary<string, SceneObjectId>();
        await using var objects = new SettlementObjects(key =>
            ids.TryGetValue(key, out var id) ? id : ids[key] = new((ulong)ids.Count + 1));
        var rendered = objects.Present(seeded, layout, default, ImmutableArray.CreateBuilder<SceneFog>(), _ => null);
        Assert.IsFalse(Shown(rendered, inside.Point), $"Candidate {inside.Candidate} at {inside.Point} overlaps the lair.");
        Assert.IsTrue(Shown(rendered, outside.Point), $"Candidate {outside.Candidate} at {outside.Point} should remain.");
    }

    private static void AssertModel(NodeScene.Built built, SceneModelProjection model, int height, bool rightSide)
    {
        Assert.AreEqual(0, built.Layout.Problems.Length);
        AssertPlacement(built.Layout, model.Scene, rightSide);
        var shift = built.Layout.Lair!;
        var expected = model.Scene.Voxels.Select(voxel =>
            $"{voxel.At.X + shift.ShiftX},{voxel.At.Y},{voxel.At.Z + shift.ShiftZ},{voxel.Orientation}").Order().ToArray();
        CollectionAssert.AreEqual(expected, LairCells(built));
        foreach (var voxel in model.Scene.Voxels)
        {
            var column = new GridColumn(voxel.At.X + shift.ShiftX, voxel.At.Z + shift.ShiftZ);
            Assert.AreEqual((double)height, built.Parts[NodeScene.ChunkKey(ChunkOf(column))].Heights[column], $"Column {column}.");
        }
    }

    private static void AssertPlacement(NodeLayout layout, DesignSceneProjection scene, bool rightSide)
    {
        var shift = layout.Lair!;
        Assert.AreEqual(0, shift.ShiftX * layout.StationForward.X + shift.ShiftZ * layout.StationForward.Z, 1e-6);
        var footprint = NodeSettlement.Bounds(scene);
        Assert.IsNotNull(footprint);
        var placed = Translate(footprint, shift.ShiftX, shift.ShiftZ);
        var samples = ClearanceSamples(layout);
        Assert.IsTrue(Fits(placed, layout));
        Assert.IsTrue(samples.All(sample => Distance(placed, sample) + 1e-4 >= Clearance));
        var right = Vector3.Cross(Vector3.UnitY, layout.StationForward);
        var rx = (int)Math.Round(right.X);
        var rz = (int)Math.Round(right.Z);
        var lateral = shift.ShiftX * rx + shift.ShiftZ * rz;
        if (rightSide) Assert.IsTrue(lateral > 0, $"shift {shift.ShiftX},{shift.ShiftZ}");
        Assert.AreNotEqual(0, lateral);
        var closer = Translate(footprint, shift.ShiftX - rx * Math.Sign(lateral), shift.ShiftZ - rz * Math.Sign(lateral));
        Assert.IsFalse(Fits(closer, layout) && samples.All(sample => Distance(closer, sample) + 1e-4 >= Clearance));
    }

    private static void CompareFar(NodeScene.Built lair, NodeScene.Built empty)
    {
        var placed = lair.Layout.Lair!;
        var region = Translate(NodeSettlement.Bounds(placed.Model.Scene)!, placed.ShiftX, placed.ShiftZ);
        var compared = 0;
        foreach (var (key, part) in empty.Parts.Where(pair => pair.Key.StartsWith("terrain:", StringComparison.Ordinal)))
        {
            var chunk = Chunk(key);
            if (chunk.X < region.Maximum.X + 8 && chunk.X + VoxelGrid.ChunkSize > region.Minimum.X - 8 &&
                chunk.Z < region.Maximum.Y + 8 && chunk.Z + VoxelGrid.ChunkSize > region.Minimum.Y - 8) continue;
            compared++;
            var actual = lair.Parts[key];
            CollectionAssert.AreEquivalent(part.Heights.ToArray(), actual.Heights.ToArray(), key);
            CollectionAssert.AreEquivalent(Cells(part), Cells(actual), key);
        }
        Assert.IsTrue(compared > 0);
    }

    private static async Task<SceneFrame> Present(SceneWorldSnapshot snapshot, NodeScene.Built built, ScenePoint origin = default)
    {
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), SceneFixtures.NextId(), vehicles, new(),
            _ => null, (_, _, _, _) => built);
        await scene.BuildWork!;
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        scene.Present(snapshot, Request, stamp, null, origin);
        await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        return scene.Present(snapshot, Request, stamp, null, origin);
    }

    private static async Task<SceneFrame> Refresh(NodeScene scene, SceneWorldSnapshot snapshot, SceneFrame displayed)
    {
        scene.Present(snapshot, Request, NodeFixtures.NextStamp(scene), displayed, default);
        await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        var held = scene.Present(snapshot, Request, NodeFixtures.NextStamp(scene), displayed, default);
        return await Settle(scene, snapshot, held);
    }

    private static async Task<SceneFrame> Settle(NodeScene scene, SceneWorldSnapshot snapshot, SceneFrame? displayed = null)
    {
        if (scene.BuildWork is not null) await scene.BuildWork;
        SceneFrame? frame = displayed;
        while (true)
        {
            var work = scene.PendingWork;
            await SceneFixtures.AwaitPendingWork(work);
            var next = scene.Present(snapshot, Request, NodeFixtures.NextStamp(scene), displayed, default);
            var changed = frame is null || !frame.Objects.Select(item => item.Id).ToHashSet().SetEquals(next.Objects.Select(item => item.Id));
            frame = next;
            if (frame.Readiness is SceneReadiness.Partial or SceneReadiness.Ready) displayed = frame;
            if (work.Length == 0 && scene.PendingWork.Length == 0 && !changed && frame.Readiness is not SceneReadiness.Loading) break;
        }
        return frame;
    }

    private static void AssertSameChunk(NodeScene.Part expected, NodeScene.Part actual)
    {
        CollectionAssert.AreEquivalent(expected.Heights.ToArray(), actual.Heights.ToArray());
        CollectionAssert.AreEquivalent(Cells(expected), Cells(actual));
    }

    private static string[] ResourcePoses(SceneFrame frame, Dictionary<string, SceneObjectId> ids)
    {
        var keys = ids.ToDictionary(pair => pair.Value, pair => pair.Key);
        return frame.Objects.Where(item => keys.TryGetValue(item.Id, out var key) &&
                key.StartsWith("resource:", StringComparison.Ordinal) && !key.Contains(":model:", StringComparison.Ordinal))
            .Select(item => $"{keys[item.Id]}:{item.Pose.Position.X},{item.Pose.Position.Y},{item.Pose.Position.Z}")
            .Order()
            .ToArray();
    }

    private static async Task<string[]> ResourcePoses(SceneWorldSnapshot snapshot, NodeLayout layout)
    {
        var ids = new Dictionary<string, SceneObjectId>();
        await using var objects = new SettlementObjects(key =>
            ids.TryGetValue(key, out var id) ? id : ids[key] = new((ulong)ids.Count + 1));
        var rendered = objects.Present(snapshot, layout, default, ImmutableArray.CreateBuilder<SceneFog>(), _ => null);
        var keys = ids.ToDictionary(pair => pair.Value, pair => pair.Key);
        return rendered.Objects.Where(item => keys.TryGetValue(item.Id, out var key) &&
                key.StartsWith("resource:", StringComparison.Ordinal) && !key.Contains(":model:", StringComparison.Ordinal))
            .Select(item => $"{keys[item.Id]}:{item.Pose.Position.X},{item.Pose.Position.Y},{item.Pose.Position.Z}")
            .Order()
            .ToArray();
    }

    private static bool HasLairGap(SceneFrame frame, ContentReference reference) =>
        frame.Diagnostics.Any(problem => problem.Code == "content_missing" && problem.Content == reference);

    private static bool InExpanded(ScenePanBounds region, ScenePoint point, int blend) =>
        point.X >= region.Minimum.X - blend && point.X < region.Maximum.X + blend &&
        point.Z >= region.Minimum.Y - blend && point.Z < region.Maximum.Y + blend;

    private static bool Shown(RenderedDesign rendered, ScenePoint point) => rendered.Objects.Any(item =>
        Math.Abs(item.Pose.Position.X - point.X) < 0.01 && Math.Abs(item.Pose.Position.Z - point.Z) < 0.01);

    private static ScenePoint CandidatePoint(NodeLayout layout, ulong seed, int candidate)
    {
        var key = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(SceneFixtures.Voxel.Value)));
        var width = layout.Bounds.Maximum.X - layout.Bounds.Minimum.X;
        var depth = layout.Bounds.Maximum.Y - layout.Bounds.Minimum.Y;
        var x = Math.Floor(layout.Bounds.Minimum.X + width * RailwayRandom.Unit(seed, layout.Node.Value, candidate, key));
        var z = Math.Floor(layout.Bounds.Minimum.Y + depth * RailwayRandom.Unit(seed, layout.Node.Value, candidate, key ^ 0xCA013FACUL));
        return new(x, 0, z);
    }

    internal static NodeScene.Built Build(SceneWorldSnapshot snapshot) =>
        NodeFixtures.Build(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));

    internal static SceneWorldSnapshot World(ContentLookup<LairAppearanceProjection> lair, bool occupant = true,
        int tracks = 1, int? trackLimit = null, bool sameSideEntry = false, bool resources = false, bool light = false)
    {
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = Radius },
            lairs: new Dictionary<LairTypeId, ContentLookup<LairAppearanceProjection>> { [Type] = lair },
            voxelAppearances: new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [LairVoxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with
                { Definition = LairVoxel, Behaviors = new(false, light ? new(new("#ffffff"), 4) : null) }) });
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], tracks: tracks, trackLimit: trackLimit,
            content: content, platformLengthMeters: 0, sameSideEntry: sameSideEntry);
        var node = snapshot.Core.World.Nodes.Single(item => item.Id == NodeFixtures.NodeId) with
        {
            Occupant = occupant ? new NodeOccupant.Lair(Id) : new NodeOccupant.Empty()
        };
        if (resources)
            node = node with { Resources = [new(new GoodId.Voxel(SceneFixtures.Voxel), 1, new ResourceRenewal.NonRenewable())] };
        return RailwayFixtures.ReplaceCore(snapshot, node, lairs: occupant
            ? new Dictionary<LairId, Lookup<LairView>> { [Id] = new Lookup<LairView>.Found(new(Id, NodeFixtures.NodeId, Type, [])) }
            : new Dictionary<LairId, Lookup<LairView>>());
    }

    internal static ContentLookup<LairAppearanceProjection> Found(SceneModelProjection model) =>
        new ContentLookup<LairAppearanceProjection>.Found(new(Type, model, new(new(model.Design.Value, ""), null, null, false)));

    internal static SceneModelProjection Model(string name, GridPosition[] cells, FlattenRegion? flatten)
    {
        var voxels = cells.Select(at => SceneFixtures.Cell(at) with { Voxel = LairVoxel }).ToImmutableArray();
        return new(new(name), SceneFixtures.Projection(voxels) with
        {
            Design = new(name),
            OccupiedBounds = new(new(cells.Min(at => at.X), cells.Min(at => at.Y), cells.Min(at => at.Z)),
                new(cells.Max(at => at.X), cells.Max(at => at.Y), cells.Max(at => at.Z))),
            RootFlatten = flatten
        }, new(name, ""));
    }

    internal static string[] LairCells(NodeScene.Built built) => built.Parts.Values.SelectMany(CellsOf).Where(cell => cell.Appearance?.Definition == LairVoxel)
        .Select(cell => $"{cell.At.X},{cell.At.Y},{cell.At.Z},{cell.Orientation}").Order().ToArray();

    private static string[] LairCells(NodeScene.Part part) => CellsOf(part).Where(cell => cell.Appearance?.Definition == LairVoxel)
        .Select(cell => $"{cell.At.X},{cell.At.Y},{cell.At.Z},{cell.Orientation}").Order().ToArray();

    private static string[] Cells(NodeScene.Part part) => CellsOf(part)
        .Select(cell => $"{cell.At.X},{cell.At.Y},{cell.At.Z},{cell.Appearance?.Definition.Value},{cell.Orientation}")
        .Order().ToArray();

    private static IEnumerable<SceneCell> CellsOf(NodeScene.Part part) =>
        part.Geometry.Geometry.Picking.SelectMany(pick => pick.Grid.Cells);

    private static List<(double X, double Z)> ClearanceSamples(NodeLayout layout)
    {
        var routes = layout.Entries.SelectMany(entry => Enumerable.Range(1, entry.TrackLimit)
                .SelectMany(count => layout.EntryRoutes(entry, count)))
            .Concat(layout.StationRoutes(true)).Concat(layout.AllTurnRoutes());
        var samples = new List<(double X, double Z)>();
        foreach (var (sample, length) in routes)
            for (var arc = 0.0; arc < length + 2; arc += 2)
            {
                var point = sample(Math.Min(arc, length)).Position;
                samples.Add((point.X, point.Z));
            }
        return samples;
    }

    private static ScenePanBounds Translate(ScenePanBounds bounds, int x, int z) =>
        new(bounds.Minimum + new Vector2(x, z), bounds.Maximum + new Vector2(x, z));

    private static bool Fits(ScenePanBounds region, NodeLayout layout)
    {
        var margin = new Vector2(NodeLayout.GroundMargin, NodeLayout.GroundMargin);
        var inner = new ScenePanBounds(layout.Bounds.Minimum + margin, layout.Bounds.Maximum - margin);
        return region.Minimum.X - 8 >= inner.Minimum.X && region.Minimum.Y - 8 >= inner.Minimum.Y &&
            region.Maximum.X + 8 <= inner.Maximum.X && region.Maximum.Y + 8 <= inner.Maximum.Y;
    }

    private static double Distance(ScenePanBounds region, (double X, double Z) sample)
    {
        var dx = sample.X < region.Minimum.X ? region.Minimum.X - sample.X :
            sample.X > region.Maximum.X ? sample.X - region.Maximum.X : 0;
        var dz = sample.Z < region.Minimum.Y ? region.Minimum.Y - sample.Z :
            sample.Z > region.Maximum.Y ? sample.Z - region.Maximum.Y : 0;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static GridColumn ChunkOf(GridColumn column) => new(
        (int)Math.Floor(column.X / (double)VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
        (int)Math.Floor(column.Z / (double)VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);

    private static GridColumn Chunk(string key)
    {
        var parts = key.Split(':');
        return new(int.Parse(parts[1]), int.Parse(parts[2]));
    }
}
