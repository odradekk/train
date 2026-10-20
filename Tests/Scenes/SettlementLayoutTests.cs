using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class SettlementLayoutTests
{
    [TestMethod]
    [DataRow(true, 4.0, 7.0)]
    [DataRow(false, 2.0, 6.0)]
    public void TemplateGroundFlattensOccupiedAreaAndBlendsOverEightCells(bool explicitRegion, double inside, double halfway)
    {
        var scene = SceneFixtures.Projection([]) with
        {
            OccupiedBounds = new(new(0, 3, 0), new(7, 9, 7)),
            RootFlatten = explicitRegion ? new(new(0, 0), new(7, 7), 5) : null
        };
        var template = NodeFixtures.Layout with { SceneModel = NodeFixtures.Layout.SceneModel with { Scene = scene } };
        Assert.AreEqual(inside, NodeSettlement.FlattenGround(template, new(4, 0, 4), 10));
        Assert.AreEqual(halfway, NodeSettlement.FlattenGround(template, new(12, 0, 4), 10));
        Assert.AreEqual(10d, NodeSettlement.FlattenGround(template, new(16, 0, 4), 10));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void StationSlotsRespectPlotPoseAndClearance(bool large, bool rotated)
    {
        var plot = new SettlementPlotLayout(7, large ? PlotSize.LargeStation : PlotSize.SmallStation,
            new(13, 5, -29), rotated ? VoxelOrientation.PzPy : VoxelOrientation.PxPy);
        var template = NodeFixtures.Layout with { Plots = [plot] };
        var snapshot = Snapshot(template, [(600, 0, 2)]);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var slots = large ? new[] { -80, -40, 40, 80 } : new[] { -40, 40 };
        CollectionAssert.AreEqual(slots.Select(z => (double)z).ToArray(), layout.Stops.Select(stop => stop.LocalZ).ToArray());
        Assert.AreEqual(slots.Length + 1, layout.StationRoutes(false).Count(), "Empty station plots retain their station lines and the fixed bypass.");
        Assert.AreEqual(2, layout.Parking.Length);
        var length = large ? 256 : 192;
        var halfWidth = large ? 104 : 56;
        for (var index = 0; index < 2; index++)
        {
            var parking = layout.Parking[index];
            var expected = rotated
                ? new ScenePoint(14 + halfWidth + 32 + index * 40, 5, -29 - length / 2)
                : new ScenePoint(13 - length / 2, 5, -29 - halfWidth - 32 - index * 40);
            Assert.AreEqual(expected.X, parking.Start.X, 0.0001);
            Assert.AreEqual(expected.Y, parking.Start.Y, 0.0001);
            Assert.AreEqual(expected.Z, parking.Start.Z, 0.0001);
            Assert.AreEqual(length, parking.Length, 0.0001);
        }
        for (var index = 0; index < slots.Length; index++)
        {
            var stop = layout.Stops[index];
            Assert.AreEqual(7, stop.PlotNumber);
            var expected = rotated ? new ScenePoint(14 - slots[index], 5, -29) : new ScenePoint(13, 5, -29 + slots[index]);
            Assert.AreEqual(expected.X, stop.Center.X, 0.0001);
            Assert.AreEqual(expected.Y, stop.Center.Y, 0.0001);
            Assert.AreEqual(expected.Z, stop.Center.Z, 0.0001);
            Assert.IsTrue(Vector3.Distance(rotated ? Vector3.UnitZ : Vector3.UnitX, stop.Forward) < 0.0001f);
            var platforms = large ? new[] { (-64, -56), (56, 64) } : new[] { (-24, -20), (20, 24) };
            var distance = platforms.Min(platform => Math.Max(platform.Item1 - stop.LocalZ, Math.Max(0, stop.LocalZ - platform.Item2)));
            Assert.AreEqual(16d, distance, "Station tracks retain sixteen cells of platform-side clearance.");
        }

        var changedCapacity = Snapshot(template, [(600, 0, 2)], platforms: 99);
        Assert.AreEqual(layout.Inputs, NodeLayout.Build(changedCapacity, NodeFixtures.NodeId).Inputs);
        var expanded = template with { SceneModel = template.SceneModel with
        {
            Scene = template.SceneModel.Scene with { OccupiedBounds = new(new(-2000, 0, -2000), new(2000, 0, 2000)) }
        } };
        var expandedLayout = NodeLayout.Build(Snapshot(expanded, [(600, 0, 2)]), NodeFixtures.NodeId);
        Assert.AreNotEqual(layout.Inputs, expandedLayout.Inputs);
        Assert.IsTrue(expandedLayout.Bounds.Minimum.X < layout.Bounds.Minimum.X && expandedLayout.Bounds.Maximum.X > layout.Bounds.Maximum.X);
    }

    [TestMethod]
    public void CollinearStationPlotsShareThroughLinesAndDifferentAxesAreDiagnosed()
    {
        var template = BuiltIn("agricultural-town");
        var layout = NodeLayout.Build(Snapshot(template, [(600, 0, 1)]), NodeFixtures.NodeId);
        Assert.AreEqual(4, layout.Stops.Length);
        Assert.AreEqual(3, layout.StationRoutes(false).Count());
        foreach (var plot in template.Plots.Take(2))
        foreach (var localZ in new[] { -40, 40 })
        {
            var stop = layout.Stops.Single(item => item.PlotNumber == plot.Number && item.LocalZ == localZ);
            Assert.AreEqual(new ScenePoint(plot.At.X, 1, -176 + localZ), stop.Center);
        }
        var mixed = template with { Plots = template.Plots.SetItem(1, template.Plots[1] with { Orientation = VoxelOrientation.PzPy }) };
        var mismatched = NodeLayout.Build(Snapshot(mixed, [(600, 0, 1)]), NodeFixtures.NodeId);
        Assert.AreEqual(1, mismatched.Problems.Count(problem => problem.Code == "station_axis_mismatch"));
        Assert.IsTrue(mismatched.Stops.All(stop => stop.PlotNumber == 1));
    }

    [TestMethod]
    [DataRow("agricultural-town")]
    [DataRow("forestry-town")]
    [DataRow("industrial-city")]
    [DataRow("core-city")]
    [DataRow("mining-town")]
    [DataRow("oilfield-town")]
    public void BuiltInEntryRoutesAvoidPlotsAndKeepBoundarySeams(string name)
    {
        var template = BuiltIn(name);
        var directions = BuiltInDirections(name);
        var snapshot = Snapshot(template, directions);
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        Assert.IsEmpty(layout.Problems);
        Assert.AreEqual(4, layout.Stops.Length);
        Assert.AreEqual(3, layout.StationRoutes(false).Count());
        foreach (var entry in layout.Entries)
        {
            var edge = snapshot.Core.World.Edges.Single(item => item.Id == entry.Edge);
            var path = new RailwayPath(snapshot, edge.Id);
            var endpoint = path.Sample(0);
            Assert.AreEqual(Math.Round(entry.Point.X - endpoint.Position.X), entry.Point.X - endpoint.Position.X, 0.0001);
            Assert.AreEqual(Math.Round(entry.Point.Z - endpoint.Position.Z), entry.Point.Z - endpoint.Position.Z, 0.0001);
            Assert.IsTrue(Vector3.Distance(entry.Outward, endpoint.Forward) < 0.0001f);
            var routes = layout.EntryRoutes(entry, edge.Tracks).ToArray();
            var routesPerTrack = routes.Length / Math.Max(1, edge.Tracks);
            for (var number = 1; number <= edge.Tracks; number++)
            {
                var start = routes[(number - 1) * routesPerTrack].Sample(0);
                var offset = edge.Tracks == 1 ? 0 : number == 1 ? -17 : 17;
                var right = Vector3.Cross(Vector3.UnitY, -entry.Outward);
                Assert.IsTrue(Vector3.Distance(new((float)(start.Position.X - entry.Point.X), 0, (float)(start.Position.Z - entry.Point.Z)), right * offset) < 0.0001f);
                var line = path.Sample(0, -offset).Position;
                Assert.AreEqual(start.Position.X - entry.Point.X, line.X - endpoint.Position.X, 0.0001);
                Assert.AreEqual(start.Position.Z - entry.Point.Z, line.Z - endpoint.Position.Z, 0.0001);
            }
            var radius = RailwayFixtures.Rules.MinimumCurveRadiusMeters * 2;
            for (var lane = 1 - edge.TrackLimit; lane < edge.TrackLimit; lane++)
            for (var station = 0; station < 2; station++)
            {
                var curve = layout.Connection(entry.Edge, lane, station);
                var end = curve.Sample(curve.Length);
                // Both station plots share x-directed lines at -176 +/- 40.
                // Ground bounds extend by 32 cells to the connection targets.
                var expectedX = entry.Side * (name == "core-city" ? 464 : 360);
                var expected = new ScenePoint(expectedX, 1, -216 + station * 80);
                Assert.IsTrue(Vector3.Distance(RailwayCoordinates.Relative(end.Position, expected), Vector3.Zero) < .002f,
                    $"{name} edge {entry.Edge.Value} lane {lane}: endpoint {end.Position}, expected {expected}");
                Assert.IsTrue(Vector3.Distance(end.Forward, -entry.Side * Vector3.UnitX) < .0001f);
                var previous = curve.Sample(0);
                var before = previous;
                var steps = (int)Math.Ceiling(curve.Length / .5);
                for (var step = 1; step <= steps; step++)
                {
                    var sample = curve.Sample(curve.Length * step / steps);
                    var delta = RailwayCoordinates.Relative(sample.Position, previous.Position);
                    var horizontal = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
                    Assert.IsTrue(Math.Abs(delta.Y) / horizontal <= RailwayFixtures.Rules.MaximumGrade + 1e-5);
                    Assert.IsTrue(Math.Abs(sample.Forward.Y) / Math.Sqrt(sample.Forward.X * sample.Forward.X +
                        sample.Forward.Z * sample.Forward.Z) <= RailwayFixtures.Rules.MaximumGrade + 1e-5);
                    if (step > 1)
                    {
                        var a = new Vector2((float)(previous.Position.X - before.Position.X), (float)(previous.Position.Z - before.Position.Z));
                        var b = new Vector2(delta.X, delta.Z);
                        var curvature = 2 * Math.Abs(a.X * b.Y - a.Y * b.X) / (a.Length() * b.Length() * (a + b).Length());
                        Assert.IsTrue(curvature <= 1 / radius + .0001, $"{name}: curvature {curvature}");
                    }
                    before = previous; previous = sample;
                }
            }
            var reservedRoutes = Enumerable.Range(1 - edge.TrackLimit, 2 * edge.TrackLimit - 1)
                .SelectMany(lane => Enumerable.Range(0, 2).Select(station => layout.Connection(entry.Edge, lane, station)));
            foreach (var route in reservedRoutes)
            for (var arc = 0.0; arc <= route.Length + 0.5; arc += 0.5)
            {
                var point = route.Sample(Math.Min(arc, route.Length)).Position;
                foreach (var plot in template.Plots.Skip(2))
                {
                    var half = plot.Size switch { PlotSize.Small => 24, PlotSize.Medium => 48, PlotSize.Large => 96, PlotSize.Giant => 192, _ => throw new AssertFailedException("Unexpected ordinary plot size.") };
                    var dx = Math.Max(0, Math.Abs(point.X - plot.At.X) - half);
                    var dz = Math.Max(0, Math.Abs(point.Z - plot.At.Z) - half);
                    Assert.IsTrue(dx * dx + dz * dz >= 16 * 16,
                        $"{name}, edge {entry.Edge.Value}, plot {plot.Number}: corridor at {point} violates sixteen-cell clearance.");
                }
            }
        }
    }

    [TestMethod]
    [TestCategory("integration")]
    [DataRow("agricultural-town")]
    [DataRow("forestry-town")]
    [DataRow("industrial-city")]
    [DataRow("core-city")]
    [DataRow("mining-town")]
    [DataRow("oilfield-town")]
    public void BuiltInGroundCellsStayOutsidePlotsAndRailCorridors(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Train.sln"))) root = root.Parent;
        Assert.IsNotNull(root);
        var resources = Path.Combine(root.FullName, "builtin", "core", "resources");
        using var templateJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(resources, "settlement-templates", name + ".settlement-template.json")));
        var source = templateJson.RootElement;
        using var groundJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(resources, "blueprints", source.GetProperty("scene_model").GetString() + ".blueprint.json")));
        var flatten = groundJson.RootElement.GetProperty("appearance").GetProperty("flatten");
        var from = flatten.GetProperty("from");
        var to = flatten.GetProperty("to");
        var template = BuiltIn(name);
        template = template with
        {
            Plots = source.GetProperty("plots").EnumerateArray().Select(plot => new SettlementPlotLayout(
                plot.GetProperty("id").GetInt32(), ReadEnum<PlotSize>(plot.GetProperty("size")),
                ReadPosition(plot.GetProperty("at")), ReadEnum<VoxelOrientation>(plot.GetProperty("orientation")))).ToImmutableArray(),
            SceneModel = template.SceneModel with { Scene = template.SceneModel.Scene with
            {
                OccupiedBounds = new(new(from[0].GetInt32(), 0, from[1].GetInt32()), new(to[0].GetInt32(), 0, to[1].GetInt32())),
                RootFlatten = new(new(from[0].GetInt32(), from[1].GetInt32()), new(to[0].GetInt32(), to[1].GetInt32()), flatten.GetProperty("height").GetInt32())
            } }
        };
        var snapshot = Snapshot(template, BuiltInDirections(name));
        var layout = NodeLayout.Build(snapshot, NodeFixtures.NodeId);
        var routes = layout.StationRoutes(true).Concat(layout.AllTurnRoutes()).ToList();
        foreach (var entry in layout.Entries)
        {
            // The one- and two-track forms include the reserved center and both outer approach lines.
            routes.AddRange(layout.EntryRoutes(entry, 1));
            routes.AddRange(layout.EntryRoutes(entry, entry.TrackLimit));
            for (var lane = 1 - entry.TrackLimit; lane < entry.TrackLimit; lane++)
            for (var station = 0; station < layout.StationRoutes(false).Count(); station++)
            {
                var route = layout.Connection(entry.Edge, lane, station);
                routes.Add((route.Sample, route.Length));
            }
        }
        foreach (var parking in layout.Parking)
            routes.Add((arc => new(RailwayCoordinates.Add(parking.Start, parking.Forward * (float)arc), parking.Forward,
                Vector3.Cross(Vector3.UnitY, parking.Forward)), parking.Length));

        const double step = .5;
        // Half a sampling step bounds the distance to the unsampled centerline.
        var clearance = RailwayCoordinates.DecorationVoxelClearance + step / 2;
        var buckets = new Dictionary<(int X, int Z), List<Vector2>>();
        foreach (var (sample, length) in routes)
        for (var arc = 0d; arc < length + step; arc += step)
        {
            var point = sample(Math.Min(arc, length)).Position;
            var key = Bucket(point.X, point.Z);
            if (!buckets.TryGetValue(key, out var points)) buckets[key] = points = [];
            points.Add(new((float)point.X, (float)point.Z));
        }
        foreach (var cell in GroundCells(groundJson.RootElement, new(Vector3.Zero, Quaternion.Identity)))
        {
            var point = new ScenePoint(cell.X, cell.Y, cell.Z);
            Assert.IsFalse(NodeSettlement.ContainsPlot(template, point), $"{name}: ground cell {cell} overlaps a plot envelope.");
            var key = Bucket(cell.X, cell.Z);
            for (var x = key.X - 1; x <= key.X + 1; x++)
            for (var z = key.Z - 1; z <= key.Z + 1; z++)
                if (buckets.TryGetValue((x, z), out var points))
                    Assert.IsTrue(points.All(rail => Vector2.DistanceSquared(new(cell.X, cell.Z), rail) >= clearance * clearance),
                        $"{name}: ground cell {cell} overlaps a rail corridor.");
        }

        static (int X, int Z) Bucket(double x, double z) => ((int)Math.Floor(x / 32), (int)Math.Floor(z / 32));
    }

    private static IEnumerable<Vector3> GroundCells(JsonElement design, ScenePose pose)
    {
        var layout = design.GetProperty("layout");
        foreach (var cell in layout.GetProperty("cells").EnumerateArray())
            yield return pose.Position + Vector3.Transform(new(cell[0].GetInt32() + .5f, cell[1].GetInt32() + .5f, cell[2].GetInt32() + .5f), pose.Rotation);
        if (!layout.TryGetProperty("composites", out var children)) yield break;
        foreach (var child in children.EnumerateArray())
        {
            var orientation = child.TryGetProperty("orientation", out var value) ? ReadEnum<VoxelOrientation>(value) : VoxelOrientation.PxPy;
            var childPose = SceneGeometry.Compose(pose, SceneGeometry.Placement(ReadPosition(child.GetProperty("at")), orientation));
            foreach (var cell in GroundCells(child.GetProperty("design"), childPose)) yield return cell;
        }
    }

    private static GridPosition ReadPosition(JsonElement value) => new(value[0].GetInt32(), value[1].GetInt32(), value[2].GetInt32());
    private static T ReadEnum<T>(JsonElement value) where T : struct, Enum => Enum.Parse<T>(value.GetString()!.Replace("_", ""), ignoreCase: true);

    private static (double X, double Z, int Tracks)[] BuiltInDirections(string name)
    {
        // Relative neighbor positions from Core/Content basic-through-world, stage1-world-v1, seed 1 unless noted.
        return name switch
        {
            "agricultural-town" => new[] { (-265.3085649692, -182.1578229789, 2), (-81.0690387397, 510.6881578092, 1) },
            "forestry-town" => [(-60.9718018707, -381.0477076283, 2), (358.5731750499, 121.6423645788, 1), (-324.3229181812, 321.9691101042, 1)],
            // Core/Content basic-through-world, stage1-world-v1: seed 3 node 7,
            // neighbor positions minus node position (edge 4, then edge 11).
            "industrial-city" => [(-148.38793418295586, -488.0024821802351, 0), (160.36416210039397, 268.20095053938314, 1)],
            "core-city" => [(-358.5731750499, -121.6423645788, 1), (176.5248729302, 270.6175196155, 1)],
            "mining-town" => [(-170.2681302793, -363.7352263776, 2)],
            _ => [(-325.113162602, -37.2153213957, 1), (423.301398003, 71.3265811644, 1)]
        };
    }

    private static SettlementLayoutProjection BuiltIn(string name)
    {
        var sizes = name switch
        {
            "agricultural-town" => new[] { PlotSize.Medium, PlotSize.Small, PlotSize.Small, PlotSize.Small },
            "forestry-town" => [PlotSize.Medium, PlotSize.Medium, PlotSize.Small, PlotSize.Small],
            "industrial-city" or "core-city" => [PlotSize.Large, PlotSize.Medium, PlotSize.Medium, PlotSize.Large],
            "mining-town" => [PlotSize.Medium, PlotSize.Large, PlotSize.Medium, PlotSize.Large],
            _ => [PlotSize.Medium, PlotSize.Large, PlotSize.Small, PlotSize.Small]
        };
        var template = NodeFixtures.Layout with
        {
            SourceTemplate = new("core:" + name),
            SceneModel = new(new(name + "-ground"), SceneFixtures.Projection([]) with
            {
                OccupiedBounds = name == "core-city" ? new(new(-432, 0, -232), new(431, 0, 999)) : new(new(-328, 0, -232), new(327, 0, 551)),
                RootFlatten = name == "core-city" ? new(new(-432, -232), new(431, 999), 1) : new(new(-328, -232), new(327, 551), 1)
            }, new(name, "")),
            Plots = [new(1, PlotSize.SmallStation, new(-104, 1, -176), VoxelOrientation.PxPy),
                new(2, PlotSize.SmallStation, new(104, 1, -176), VoxelOrientation.PxPy),
                new(3, PlotSize.Medium, new(-224, 1, 0), VoxelOrientation.PxPy),
                new(4, PlotSize.Small, new(0, 1, 0), VoxelOrientation.PxPy),
                new(5, PlotSize.Medium, new(224, 1, 0), VoxelOrientation.PxPy),
                new(6, sizes[0], new(-224, 1, 224), VoxelOrientation.PxPy),
                new(7, sizes[1], new(0, 1, 224), VoxelOrientation.PxPy),
                new(8, sizes[2], new(224, 1, 224), VoxelOrientation.PxPy),
                new(9, sizes[3], new(-224, 1, 448), VoxelOrientation.PxPy),
                new(10, PlotSize.Small, new(0, 1, 448), VoxelOrientation.PxPy)]
        };
        return name == "core-city" ? template with
        {
            Plots = template.Plots.Add(new(11, PlotSize.Giant, new(-224, 1, 800), VoxelOrientation.PxPy))
                .Add(new(12, PlotSize.Giant, new(224, 1, 800), VoxelOrientation.PxPy))
        } : template;
    }

    private static SceneWorldSnapshot Snapshot(SettlementLayoutProjection template, (double X, double Z, int Tracks)[] directions, int platforms = 0)
    {
        var basis = NodeFixtures.Snapshot(platforms: platforms, trains: []);
        var original = (FixedContentView)basis.Content;
        var content = new FixedContentView
        {
            Generation = original.Generation, Core = original.Core,
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>> { [NodeFixtures.StationDesign] = original.FindDesign(NodeFixtures.StationDesign) },
            VoxelAppearances = new Dictionary<VoxelDefinitionId, ContentLookup<VoxelAppearanceProjection>> { [SceneFixtures.Voxel] = original.FindVoxelAppearance(SceneFixtures.Voxel) },
            Terrains = new Dictionary<TerrainTypeId, ContentLookup<TerrainAppearanceProjection>> { [RailwayFixtures.Terrain] = original.FindTerrain(RailwayFixtures.Terrain) },
            RailwayAppearances = new Dictionary<RuleDefinitionId, ContentLookup<RailwayAppearanceProjection>> { [RailwayFixtures.Rule] = original.FindRailwayAppearance(RailwayFixtures.Rule) },
            SettlementLayouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>> { [new(1)] = new ContentLookup<SettlementLayoutProjection>.Found(template) }
        };
        var center = basis.Core.World.Nodes[0];
        var nodes = ImmutableArray.Create(center).AddRange(directions.Select((direction, index) => center with
        {
            Id = new((ulong)index + 2), Position = new(direction.X, direction.Z), Occupant = new NodeOccupant.Empty(), Platforms = 0
        }));
        var edges = directions.Select((direction, index) => NodeFixtures.Edge(new((ulong)index + 1), center.Id, nodes[index + 1].Id, direction.Tracks, 2) with { LengthMeters = 1600 }).ToImmutableArray();
        return new(new FixedCoreReadView
        {
            Position = basis.Core.Position, InputIdentity = basis.Core.InputIdentity,
            World = basis.Core.World with { Nodes = nodes, Edges = edges },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node,
                edges.Where(edge => edge.Start == node.Id || edge.End == node.Id).Select(edge => edge.Id).ToImmutableArray(), 80, 0, 80))),
            Edges = edges.ToDictionary(edge => edge.Id, edge => (Lookup<EdgeView>)new Lookup<EdgeView>.Found(new(edge, [], null))),
            Settlements = new Dictionary<SettlementId, Lookup<SettlementView>> { [new(1)] = basis.Core.FindSettlement(new(1)) }
        }, content);
    }
}
