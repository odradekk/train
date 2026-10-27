using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
namespace Train.SceneScale;

internal static partial class Probe
{
    private static void WriteStats(ICoreReadView view, IContentView content, double[] observedFocus)
    {
        var node = view.World.Nodes.Single(n => n.Id.Value == 2);
        var settlement = ((Lookup<SettlementView>.Found)view.FindSettlement(((NodeOccupant.Settlement)node.Occupant!).Id)).Value;
        var layout = ((ContentLookup<SettlementLayoutProjection>.Found)content.FindSettlementLayout(settlement.Id)).Value;
        var json = new JsonSerializerOptions
        {
            IncludeFields = true
        };

        var focus = new Vector2((float)observedFocus![0], (float)observedFocus![1]);
        var farChunks = new HashSet<(int, int, int)>();
        var near = new List<string>();
        long farN = 0, farF = 0;
        var objects = new List<object>();
        int business32 = 0;
        var D = new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) };
        foreach (var plot in settlement.Plots.Where(p => p.Occupant is PlotOccupant.Building))
        {
            var at = layout.Plots.Single(p => p.Number == plot.TemplatePlotNumber);
            if (at.Orientation != VoxelOrientation.PxPy) throw new Exception("Unsupported orientation");
            var instance = ((Lookup<InstanceView>.Found)view.FindInstance(new InstanceId.Building(((PlotOccupant.Building)plot.Occupant).Id))).Value;
            var design = (DesignEvaluation.Complete)((ContentLookup<DesignEvaluation>.Found)content.FindDesign(instance.Design)).Value;
            var cells = design.Scene.Voxels.Select(v => (v.At.X + at.At.X, v.At.Y + at.At.Y, v.At.Z + at.At.Z)).ToHashSet();
            long n = 0, f = 0;
            var chunks = new HashSet<(int, int, int)>();
            bool close32 = false;
            foreach (var v in design.Scene.Voxels)
            {
                int x = v.At.X + at.At.X, y = v.At.Y + at.At.Y, z = v.At.Z + at.At.Z;
                double d = (x - focus.X) * (x - focus.X) + (z - focus.Y) * (z - focus.Y);
                if (d <= 64 * 64) close32 = true;
                if (d <= 192 * 192) near.Add(JsonSerializer.Serialize(new
                {
                    x,
                    y,
                    z,
                    v.Voxel,
                    v.Orientation,
                    v.Shape,
                    v.Paint
                }, json));
                else
                {
                    n++;
                    var ch = ((int)Math.Floor(x / 32.0), (int)Math.Floor(y / 32.0), (int)Math.Floor(z / 32.0));
                    chunks.Add(ch);
                    farChunks.Add(ch);
                    foreach (var (dx, dy, dz) in D) if (!cells.Contains((x + dx, y + dy, z + dz))) f++;
                }
            }
            if (close32) business32++;
            farN += n;
            farF += f;
            objects.Add(new
            {
                plot = plot.TemplatePlotNumber,
                at = at.At,
                design = instance.Design.Value,
                totalN = cells.Count,
                farN = n,
                farC = chunks.Count,
                farF = f
            });
        }
        var ground = layout.SceneModel.Scene.Voxels.Select(v => (v.At.X, v.At.Y, v.At.Z)).ToHashSet();
        long groundF = 0;
        foreach (var (x, y, z) in ground) foreach (var (dx, dy, dz) in D) if (!ground.Contains((x + dx, y + dy, z + dz))) groundF++;
        var groundNear = layout.SceneModel.Scene.Voxels.Where(v => Math.Pow(v.At.X - focus.X, 2) + Math.Pow(v.At.Z - focus.Y, 2) <= 192 * 192).Select(v => JsonSerializer.Serialize(new
        {
            v.At,
            v.Voxel,
            v.Orientation,
            v.Shape,
            v.Paint
        }, json)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        near.Sort(StringComparer.Ordinal);
        var result = new
        {
            position = view.Position,
            node = 2,
            nodeTerrain = node.Terrain,
            worldTerrainCounts = view.World.Nodes.GroupBy(n => n.Terrain.TypeId.Value).Select(g => new
            {
                type = g.Key,
                count = g.Count()
            }).OrderBy(g => g.type).ToArray(),
            plots = layout.Plots.Length,
            publicLayoutSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(layout, json)))),
            buildingObjects = objects,
            far = new
            {
                N = farN,
                C = farChunks.Count,
                F = farF
            },
            ground = new
            {
                N = ground.Count,
                C = ground.Select(v => ((int)Math.Floor(v.Item1 / 32.0), (int)Math.Floor(v.Item2 / 32.0), (int)Math.Floor(v.Item3 / 32.0))).Distinct().Count(),
                F = groundF,
                flatten = layout.SceneModel.Scene.RootFlatten
            },
            near96 = new
            {
                N = near.Count,
                sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', near))))
            },
            groundNear96 = new
            {
                N = groundNear.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', groundNear))))
            },
            businessObjectsWithin32m = business32,
            trains = view.World.Trains.Length,
            trainStates = view.World.Trains.Select(t => ((Lookup<TrainView>.Found)view.FindTrain(t.Id)).Value).Select(t => new
            {
                t.Id,
                position = t.Position.ToString(),
                t.Activity,
                t.SpeedMetersPerSecond,
                planStops = t.Plan?.Stops.Length,
                loop = t.Plan?.Loop
            }).ToArray()
        };
        Console.WriteLine(JsonSerializer.Serialize(result, json));

    }
}
