using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class NodeSettlement
{
    internal static SettlementLayoutProjection? Read(SceneWorldSnapshot snapshot, NodeId node) =>
        snapshot.Core.World.Nodes.Single(item => item.Id == node).Occupant is NodeOccupant.Settlement owner &&
        snapshot.Content.FindSettlementLayout(owner.Id) is ContentLookup<SettlementLayoutProjection>.Found found ? found.Value : null;

    internal static (int X, int Z, int Y) Size(PlotSize size) => size switch
    {
        PlotSize.Small => (48, 48, 48), PlotSize.Medium => (96, 96, 80), PlotSize.Large => (192, 192, 128),
        PlotSize.Giant => (384, 384, 256), PlotSize.SmallStation => (192, 112, 48), PlotSize.LargeStation => (256, 208, 64),
        _ => throw new ArgumentException("Unknown plot size.", nameof(size))
    };

    internal static bool Station(SettlementPlotLayout plot) => plot.Size is PlotSize.SmallStation or PlotSize.LargeStation;
    internal static Vector3 Point(ScenePose pose, double x, double y, double z) =>
        pose.Position + Vector3.Transform(new((float)x, (float)y, (float)z), pose.Rotation);
    internal static Vector3[] Corners(SettlementPlotLayout plot)
    {
        var (x, z, _) = Size(plot.Size);
        var pose = SceneGeometry.Placement(plot.At, plot.Orientation);
        return [Point(pose, -x / 2, 0, -z / 2), Point(pose, x / 2, 0, -z / 2),
            Point(pose, -x / 2, 0, z / 2), Point(pose, x / 2, 0, z / 2)];
    }

    internal static ScenePanBounds Bounds(SettlementLayoutProjection template)
    {
        var points = template.Plots.SelectMany(Corners).Select(p => new Vector2(p.X, p.Z)).ToList();
        var scene = template.SceneModel.Scene;
        if (scene.OccupiedBounds is { } occupied)
        { points.Add(new(occupied.Minimum.X, occupied.Minimum.Z)); points.Add(new(occupied.Maximum.X + 1, occupied.Maximum.Z + 1)); }
        foreach (var (region, pose) in Flatten(scene))
        {
            points.AddRange(new[] { Point(pose, region.From.X, 0, region.From.Z), Point(pose, region.To.X + 1, 0, region.From.Z),
                Point(pose, region.From.X, 0, region.To.Z + 1), Point(pose, region.To.X + 1, 0, region.To.Z + 1) }.Select(p => new Vector2(p.X, p.Z)));
        }
        return points.Count == 0 ? new(Vector2.Zero, Vector2.Zero) : new(points.Aggregate(Vector2.Min), points.Aggregate(Vector2.Max));
    }

    internal static IEnumerable<(FlattenRegion Region, ScenePose Pose)> Flatten(DesignSceneProjection scene)
    {
        var identity = new ScenePose(Vector3.Zero, Quaternion.Identity);
        if (scene.RootFlatten is { } root) yield return (root, identity);
        else if (scene.OccupiedBounds is { } occupied)
            yield return (new(new(occupied.Minimum.X, occupied.Minimum.Z), new(occupied.Maximum.X, occupied.Maximum.Z), occupied.Minimum.Y), identity);
        var poses = new Dictionary<string, ScenePose> { [""] = identity };
        foreach (var item in scene.NestedObjects.OrderBy(item => item.NestedItemIndices.Length))
        {
            var parent = string.Join('/', item.NestedItemIndices.RemoveAt(item.NestedItemIndices.Length - 1));
            var pose = SceneGeometry.Compose(poses[parent], SceneGeometry.Placement(item.At, item.Orientation));
            poses[string.Join('/', item.NestedItemIndices)] = pose;
            if (item.Details is NestedObjectDetails.SceneModel { Flatten: { } region }) yield return (region, pose);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DesignSceneProjection,
        (FlattenRegion Region, ScenePose Pose)[]> flattenRegions = new();
    internal static double FlattenGround(SettlementLayoutProjection template, ScenePoint point, double raw) =>
        FlattenGround(template.SceneModel.Scene, 0, 0, point, raw);

    internal static double FlattenGround(DesignSceneProjection scene, int shiftX, int shiftZ, ScenePoint point, double raw)
    {
        foreach (var (region, pose) in flattenRegions.GetValue(scene, value => Flatten(value).ToArray()))
        {
            var position = pose.Position + new Vector3(shiftX, 0, shiftZ);
            var local = Vector3.Transform(new Vector3((float)point.X, 0, (float)point.Z) - position, Quaternion.Conjugate(pose.Rotation));
            var distance = Math.Max(Math.Max(region.From.X - local.X, local.X - region.To.X - 1),
                Math.Max(region.From.Z - local.Z, local.Z - region.To.Z - 1));
            var blend = Math.Clamp(distance / 8, 0, 1);
            raw = (position.Y + region.Height - 1) * (1 - blend) + raw * blend;
        }
        return raw;
    }

    internal static bool ContainsPlot(SettlementLayoutProjection template, ScenePoint point) => template.Plots.Any(plot =>
    {
        var pose = SceneGeometry.Placement(plot.At, plot.Orientation);
        var p = Vector3.Transform(new Vector3((float)point.X, (float)point.Y, (float)point.Z) - pose.Position, Quaternion.Conjugate(pose.Rotation));
        var (x, z, _) = Size(plot.Size);
        return p.X >= -x / 2 && p.X < x / 2 && p.Z >= -z / 2 && p.Z < z / 2;
    });

    internal static object Inputs(SettlementLayoutProjection? template) => template is null ? "no-settlement-layout" :
        new object?[] { template.Owner, template.Plots.OrderBy(p => p.Number).ToArray(), template.SceneModel.Scene.OccupiedBounds,
            template.SceneModel.Scene.RootFlatten, template.SceneModel.Scene.NestedObjects };
}
