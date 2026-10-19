using System.Collections.Immutable;
using System.Numerics;
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

    [TestMethod]
    public void PlacedModelsOccupyTranslatedCellsAndRepeat()
    {
        var line = Model("test:lair-line", [new(0, 2, 0), new(1, 2, 0), new(2, 2, 0)], new(new(0, 0), new(2, 0), 5));
        var bend = Model("test:lair-bend", [new(0, 4, 0), new(0, 4, 1), new(1, 4, 1)], null);
        var first = Build(World(Found(line)));
        var repeat = Build(World(Found(line)));
        var other = Build(World(Found(bend)));
        AssertModel(first, line, 5);
        AssertModel(repeat, line, 5);
        AssertModel(other, bend, 4);
        CollectionAssert.AreEqual(LairCells(first), LairCells(repeat));
        CollectionAssert.AreNotEqual(LairCells(first), LairCells(other));
        var chunk = new GridColumn(0, 0);
        var inputs = NodeScene.Prepare(World(Found(line)), first.Layout).ChunkInputs(chunk);
        Assert.AreEqual(inputs, NodeScene.Prepare(World(Found(line)), repeat.Layout).ChunkInputs(chunk));
        Assert.AreNotEqual(inputs, NodeScene.Prepare(World(Found(bend)), other.Layout).ChunkInputs(chunk));
        Assert.IsTrue(Count(inputs, line.Design.Value) >= 2, inputs);
        var empty = NodeLayout.Build(World(new ContentLookup<LairAppearanceProjection>.NotFound(), occupant: false), NodeFixtures.NodeId);
        Assert.IsFalse(NodeScene.Prepare(World(new ContentLookup<LairAppearanceProjection>.NotFound(), occupant: false), empty)
            .ChunkInputs(chunk).Contains(line.Design.Value, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ClearanceIgnoresBuiltTrackCountAndDistantChunksMatchAnEmptyNode()
    {
        var model = Model("test:lair-line", [new(0, 2, 0), new(1, 2, 0), new(2, 2, 0)], new(new(0, 0), new(2, 0), 5));
        var bare = Build(World(Found(model), tracks: 0, trackLimit: 2));
        var laid = Build(World(Found(model), tracks: 1, trackLimit: 2));
        Assert.IsTrue(bare.Layout.Entries.Any(entry => entry.TrackLimit == 2));
        Assert.IsTrue(bare.Layout.Entries.Any(entry => Math.Abs(Vector3.Dot(entry.Outward, bare.Layout.StationForward)) < 0.2));
        Assert.AreEqual(bare.Layout.Lair, laid.Layout.Lair);
        CollectionAssert.AreEqual(LairCells(bare), LairCells(laid));
        AssertFirstCandidate(bare.Layout, model.Scene);
        CompareFar(bare, Build(World(Found(model), occupant: false, tracks: 0, trackLimit: 2)));
        CompareFar(laid, Build(World(Found(model), occupant: false, tracks: 1, trackLimit: 2)));
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
        var missing = frame.Diagnostics.Single(problem => problem.Code == "content_missing");
        Assert.AreEqual(reference, missing.Content);
        Assert.AreEqual(new WorldObjectId.Node(NodeFixtures.NodeId), missing.Subject);
        Assert.AreEqual(SceneStage.Build, missing.Stage);
        Assert.AreEqual(reported ? diagnostic.Source!.Package : null, missing.Package);
        Assert.AreEqual(reported ? 1 : 0, frame.Diagnostics.Count(problem => problem.Code == diagnostic.Code));
    }

    private static void AssertModel(NodeScene.Built built, SceneModelProjection model, int height)
    {
        Assert.AreEqual(0, built.Layout.Problems.Length);
        AssertFirstCandidate(built.Layout, model.Scene);
        var shift = built.Layout.Lair!;
        var expected = model.Scene.Voxels.Select(voxel =>
            $"{voxel.At.X + shift.ShiftX},{voxel.At.Y},{voxel.At.Z + shift.ShiftZ},{voxel.Orientation}").Order().ToArray();
        CollectionAssert.AreEqual(expected, LairCells(built));
        foreach (var voxel in model.Scene.Voxels)
        {
            var column = new GridColumn(voxel.At.X + shift.ShiftX, voxel.At.Z + shift.ShiftZ);
            var origin = new GridColumn(
                (int)Math.Floor(column.X / (double)VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize,
                (int)Math.Floor(column.Z / (double)VoxelGrid.ChunkSize) * VoxelGrid.ChunkSize);
            Assert.AreEqual((double)height, built.Parts[NodeScene.ChunkKey(origin)].Heights[column], $"Column {column}.");
        }
    }

    private static void AssertFirstCandidate(NodeLayout layout, DesignSceneProjection scene)
    {
        var right = Vector3.Cross(Vector3.UnitY, layout.StationForward);
        var stepX = (int)Math.Round(right.X);
        var stepZ = (int)Math.Round(right.Z);
        var shift = layout.Lair!;
        Assert.AreEqual(0, shift.ShiftX * layout.StationForward.X + shift.ShiftZ * layout.StationForward.Z, 1e-6);
        var samples = Samples(layout);
        (int X, int Z)? expected = null;
        foreach (var sign in new[] { 1, -1 })
        {
            for (var step = sign > 0 ? 0 : 1; step <= 512; step++)
            {
                var x = stepX * sign * step;
                var z = stepZ * sign * step;
                var moved = Region(scene, x, z);
                if (Past(moved, stepX * sign, stepZ * sign, layout)) break;
                if (Fits(moved, layout) && Clears(moved, samples)) { expected = (x, z); break; }
            }
            if (expected is not null) break;
        }
        Assert.AreEqual(expected, (shift.ShiftX, shift.ShiftZ),
            $"bounds {layout.Bounds.Minimum}..{layout.Bounds.Maximum} forward {layout.StationForward}");
        var placed = Region(scene, shift.ShiftX, shift.ShiftZ);
        Assert.IsTrue(Fits(placed, layout));
        Assert.IsTrue(samples.All(sample => Distance(placed, sample) + 1e-4 >= Clearance));
    }

    private static void CompareFar(NodeScene.Built lair, NodeScene.Built empty)
    {
        var placed = lair.Layout.Lair!;
        var region = Region(placed.Model.Scene, placed.ShiftX, placed.ShiftZ);
        var compared = 0;
        foreach (var (key, part) in empty.Parts.Where(pair => pair.Key.StartsWith("terrain:", StringComparison.Ordinal)))
        {
            var chunk = Chunk(key);
            if (chunk.X < region.Right + 8 && chunk.X + VoxelGrid.ChunkSize > region.Left - 8 &&
                chunk.Z < region.Front + 8 && chunk.Z + VoxelGrid.ChunkSize > region.Back - 8) continue;
            compared++;
            var actual = lair.Parts[key];
            CollectionAssert.AreEquivalent(part.Heights.ToArray(), actual.Heights.ToArray(), key);
            CollectionAssert.AreEquivalent(Cells(part), Cells(actual), key);
        }
        Assert.IsTrue(compared > 0);
    }

    private static async Task<SceneFrame> Present(SceneWorldSnapshot snapshot, NodeScene.Built built)
    {
        await using var vehicles = new SceneVehicles();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), SceneFixtures.NextId(), vehicles, new(),
            _ => null, (_, _, _, _) => built);
        await scene.BuildWork!;
        var stamp = new SceneFrameStamp(new(Guid.NewGuid()), new(1), new(1), 1);
        var request = new SceneFrameRequest(1, null, SceneDebugLayer.None);
        scene.Present(snapshot, request, stamp, null, default);
        await SceneFixtures.AwaitPendingWork(scene.PendingWork);
        return scene.Present(snapshot, request, stamp, null, default);
    }

    private static NodeScene.Built Build(SceneWorldSnapshot snapshot) =>
        NodeFixtures.Build(snapshot, NodeLayout.Build(snapshot, NodeFixtures.NodeId));

    private static SceneWorldSnapshot World(ContentLookup<LairAppearanceProjection> lair, bool occupant = true,
        int tracks = 1, int? trackLimit = null)
    {
        var content = NodeFixtures.Content(rules: RailwayFixtures.Rules with { MinimumCurveRadiusMeters = Radius },
            lairs: new Dictionary<LairTypeId, ContentLookup<LairAppearanceProjection>> { [Type] = lair },
            voxelAppearances: new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>>
            { [LairVoxel] = new ContentLookup<VoxelAppearanceProjection>.Found(SceneFixtures.Cube with { Definition = LairVoxel }) });
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [], tracks: tracks, trackLimit: trackLimit,
            content: content, platformLengthMeters: 0);
        var node = snapshot.Core.World.Nodes.Single(item => item.Id == NodeFixtures.NodeId) with
        {
            Occupant = occupant ? new NodeOccupant.Lair(Id) : new NodeOccupant.Empty()
        };
        return RailwayFixtures.ReplaceCore(snapshot, node, lairs: occupant
            ? new Dictionary<LairId, Lookup<LairView>> { [Id] = new Lookup<LairView>.Found(new(Id, NodeFixtures.NodeId, Type, [])) }
            : new Dictionary<LairId, Lookup<LairView>>());
    }

    private static ContentLookup<LairAppearanceProjection> Found(SceneModelProjection model) =>
        new ContentLookup<LairAppearanceProjection>.Found(new(Type, model, new(new(model.Design.Value, ""), null, null, false)));

    private static SceneModelProjection Model(string name, GridPosition[] cells, FlattenRegion? flatten)
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

    private static string[] LairCells(NodeScene.Built built) => built.Parts.Values
        .SelectMany(part => part.Geometry.Geometry.Picking.SelectMany(pick => pick.Grid.Cells))
        .Where(cell => cell.Appearance?.Definition == LairVoxel)
        .Select(cell => $"{cell.At.X},{cell.At.Y},{cell.At.Z},{cell.Orientation}")
        .Order()
        .ToArray();

    private static string[] Cells(NodeScene.Part part) => part.Geometry.Geometry.Picking
        .SelectMany(pick => pick.Grid.Cells)
        .Select(cell => $"{cell.At.X},{cell.At.Y},{cell.At.Z},{cell.Appearance?.Definition.Value},{cell.Orientation}")
        .Order()
        .ToArray();

    private static List<(double X, double Z)> Samples(NodeLayout layout)
    {
        var routes = layout.Entries.SelectMany(entry => Enumerable.Range(1, entry.TrackLimit)
                .SelectMany(count => layout.EntryRoutes(entry, count)))
            .Concat(layout.StationRoutes(true)).Concat(layout.TurnRoutes());
        var samples = new List<(double X, double Z)>();
        foreach (var (sample, length) in routes)
            for (var arc = 0.0; arc < length + 2; arc += 2)
            {
                var point = sample(Math.Min(arc, length)).Position;
                samples.Add((point.X, point.Z));
            }
        return samples;
    }

    private static (int Left, int Right, int Back, int Front) Region(DesignSceneProjection scene, int shiftX, int shiftZ)
    {
        var occupied = scene.OccupiedBounds!;
        var left = occupied.Minimum.X;
        var right = occupied.Maximum.X + 1;
        var back = occupied.Minimum.Z;
        var front = occupied.Maximum.Z + 1;
        if (scene.RootFlatten is { } flatten)
        {
            left = Math.Min(left, flatten.From.X);
            right = Math.Max(right, flatten.To.X + 1);
            back = Math.Min(back, flatten.From.Z);
            front = Math.Max(front, flatten.To.Z + 1);
        }
        return (left + shiftX, right + shiftX, back + shiftZ, front + shiftZ);
    }

    private static bool Fits((int Left, int Right, int Back, int Front) region, NodeLayout layout)
    {
        var innerLeft = (int)layout.Bounds.Minimum.X + NodeLayout.GroundMargin;
        var innerRight = (int)layout.Bounds.Maximum.X - NodeLayout.GroundMargin;
        var innerBack = (int)layout.Bounds.Minimum.Y + NodeLayout.GroundMargin;
        var innerFront = (int)layout.Bounds.Maximum.Y - NodeLayout.GroundMargin;
        return region.Left - 8 >= innerLeft && region.Right + 8 <= innerRight &&
            region.Back - 8 >= innerBack && region.Front + 8 <= innerFront;
    }

    private static bool Clears((int Left, int Right, int Back, int Front) region, List<(double X, double Z)> samples) =>
        samples.All(sample => Distance(region, sample) + 1e-4 >= Clearance);

    private static double Distance((int Left, int Right, int Back, int Front) region, (double X, double Z) sample)
    {
        var dx = sample.X < region.Left ? region.Left - sample.X : sample.X > region.Right ? sample.X - region.Right : 0;
        var dz = sample.Z < region.Back ? region.Back - sample.Z : sample.Z > region.Front ? sample.Z - region.Front : 0;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static bool Past((int Left, int Right, int Back, int Front) region, int stepX, int stepZ, NodeLayout layout)
    {
        var innerLeft = (int)layout.Bounds.Minimum.X + NodeLayout.GroundMargin;
        var innerRight = (int)layout.Bounds.Maximum.X - NodeLayout.GroundMargin;
        var innerBack = (int)layout.Bounds.Minimum.Y + NodeLayout.GroundMargin;
        var innerFront = (int)layout.Bounds.Maximum.Y - NodeLayout.GroundMargin;
        if (stepX > 0 && region.Left - 8 >= innerRight) return true;
        if (stepX < 0 && region.Right + 8 <= innerLeft) return true;
        if (stepZ > 0 && region.Back - 8 >= innerFront) return true;
        if (stepZ < 0 && region.Front + 8 <= innerBack) return true;
        return false;
    }

    private static GridColumn Chunk(string key)
    {
        var parts = key.Split(':');
        return new(int.Parse(parts[1]), int.Parse(parts[2]));
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
