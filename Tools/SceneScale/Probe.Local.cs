using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.SceneScale;

internal static partial class Probe
{
    internal sealed record LocalObservation(double seconds, int triangles,
        string triangleWorldCoordinatesMaterialSha256, int railSteelTriangles, string railSteelSha256,
        int heightColumns, int heightFound, int heightMissing, int foggedRegionsIntersecting, string terrainHeightSha256,
        string objectSetSha256 = "", int objectCount = 0,
        int nonRailTriangles = 0, string nonRailTriangleWorldCoordinatesMaterialSha256 = "")
    {
        internal bool CanRepeatAt(double seconds) => seconds - this.seconds >= 10;
        internal bool SameContent(LocalObservation other) => this with { seconds = other.seconds } == other;
    }

    internal sealed record LocalCoverage(SceneReadiness Readiness, int FoggedRegionsIntersecting,
        int HeightColumns, int HeightFound)
    {
        public int HeightMissing => HeightColumns - HeightFound;
        public bool Complete => Readiness == SceneReadiness.Ready && FoggedRegionsIntersecting == 0 && HeightMissing == 0;
    }

    internal sealed record LocalCapture(HashSet<string> Triangles, HashSet<string> Rails, LocalObservation Observation,
        LocalGeometry Geometry, HashSet<string> NonRailTriangles);
    internal sealed record LocalStabilitySample(int cameraStage, Vector2 cameraOffsetCells,
        LocalObservation first, LocalObservation? second, bool? equal,
        LocalGeometry? firstGeometry = null, LocalGeometry? secondGeometry = null,
        double? unchangedSinceSeconds = null);

    internal static LocalCapture CaptureLocal(Dictionary<SceneObjectId, SceneObject> objects, ScenePoint origin,
        Vector2 focusCenter, Dictionary<(int X, int Z), double> heights, LocalCoverage coverage, double seconds,
        LocalGeometryCache? cache = null)
    {
        cache ??= new();
        cache.Retain(objects);
        var triangles = new HashSet<string>(StringComparer.Ordinal);
        var rails = new HashSet<string>(StringComparer.Ordinal);
        var nonRailTriangles = new HashSet<string>(StringComparer.Ordinal);
        var fullyInsideTriangles = new HashSet<string>(StringComparer.Ordinal);
        var crossingTriangles = new HashSet<string>(StringComparer.Ordinal);
        var fullyInsideNonRailTriangles = new HashSet<string>(StringComparer.Ordinal);
        var crossingNonRailTriangles = new HashSet<string>(StringComparer.Ordinal);
        var inventory = new List<LocalGeometryObject>();
        foreach (var item in objects.Values)
        {
            var pose = WorldPose(item, objects, origin);
            var geometry = cache.GeometryFor(item, pose, focusCenter);
            if (geometry.Triangles.Count == 0) continue;
            triangles.UnionWith(geometry.Triangles);
            rails.UnionWith(geometry.Rails);
            nonRailTriangles.UnionWith(geometry.NonRailTriangles);
            (geometry.FullyInside ? fullyInsideTriangles : crossingTriangles).UnionWith(geometry.Triangles);
            (geometry.FullyInside ? fullyInsideNonRailTriangles : crossingNonRailTriangles).UnionWith(geometry.NonRailTriangles);
            inventory.Add(new(item.Id, item.Parent, item.Subject?.ToString(), pose, geometry.Bounds!, geometry.Materials,
                geometry.FullyInside ? "fully_inside" : "crossing_boundary", geometry.Triangles.Count, geometry.TriangleSha256,
                geometry.NonRailTriangles.Count, geometry.NonRailTriangleSha256));
        }
        var details = new LocalGeometry(inventory.OrderBy(item => item.id.Value).ToArray(),
            new(inventory.Count(item => item.category == "fully_inside"), fullyInsideTriangles.Count, Digest(fullyInsideTriangles),
                fullyInsideNonRailTriangles.Count, Digest(fullyInsideNonRailTriangles)),
            new(inventory.Count(item => item.category == "crossing_boundary"), crossingTriangles.Count, Digest(crossingTriangles),
                crossingNonRailTriangles.Count, Digest(crossingNonRailTriangles)));
        return new(triangles, rails, new(seconds, triangles.Count, Digest(triangles), rails.Count, Digest(rails),
            coverage.HeightColumns, coverage.HeightFound, coverage.HeightMissing, coverage.FoggedRegionsIntersecting,
            Digest(heights.Select(p => $"{p.Key.X},{p.Key.Z},{Q(p.Value)}")),
            Digest(inventory.Select(item => item.id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))), inventory.Count,
            nonRailTriangles.Count, Digest(nonRailTriangles)), details, nonRailTriangles);
    }

    private static string Digest(IEnumerable<string> keys) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', keys.OrderBy(x => x, StringComparer.Ordinal)))));
    private static long Q(double value) => (long)Math.Round(value * 1000, MidpointRounding.AwayFromZero);

    private static bool LocalTriangleIntersects(Vector3 a3, Vector3 b3, Vector3 c3, Vector2 focusCenter)
    {
        var a = new Vector2(a3.X, a3.Z) - focusCenter;
        var b = new Vector2(b3.X, b3.Z) - focusCenter;
        var c = new Vector2(c3.X, c3.Z) - focusCenter;
        const float rr = 192.001f * 192.001f;
        if (a.LengthSquared() <= rr || b.LengthSquared() <= rr || c.LengthSquared() <= rr) return true;
        float Cross(Vector2 p, Vector2 q) => p.X * q.Y - p.Y * q.X;
        float Edge(Vector2 p, Vector2 q)
        {
            var d = q - p;
            var t = d.LengthSquared() == 0 ? 0 : Math.Clamp(-Vector2.Dot(p, d) / d.LengthSquared(), 0, 1);
            return (p + t * d).LengthSquared();
        }
        var area = Cross(b - a, c - a);
        var ab = Cross(b - a, -a);
        var bc = Cross(c - b, -b);
        var ca = Cross(a - c, -c);
        return Math.Abs(area) > 1e-8 && (ab >= 0 && bc >= 0 && ca >= 0 || ab <= 0 && bc <= 0 && ca <= 0) || Edge(a, b) <= rr || Edge(b, c) <= rr || Edge(c, a) <= rr;
    }

    private static ScenePose WorldPose(SceneObject item, Dictionary<SceneObjectId, SceneObject> objects, ScenePoint origin)
    {
        if (item.Parent is not { } parent) return new(item.Pose.Position + new Vector3((float)origin.X, (float)origin.Y, (float)origin.Z), item.Pose.Rotation);
        var pose = WorldPose(objects[parent], objects, origin);
        return new(pose.Position + Vector3.Transform(item.Pose.Position, pose.Rotation), Quaternion.Normalize(pose.Rotation * item.Pose.Rotation));
    }

    internal static List<(int X, int Z)> LocalColumns(Vector2 focusCenter)
    {
        var columns = new List<(int X, int Z)>();
        for (int z = (int)Math.Floor(focusCenter.Y - 192); z <= Math.Ceiling(focusCenter.Y + 192); z++)
            for (int x = (int)Math.Floor(focusCenter.X - 192); x <= Math.Ceiling(focusCenter.X + 192); x++)
                if (Vector2.DistanceSquared(new(x + .5f, z + .5f), focusCenter) <= 192 * 192) columns.Add((x, z));
        return columns;
    }

    internal static LocalCoverage ObserveLocalCoverage(ISceneFrame frame, Vector2 focusCenter,
        IReadOnlyList<(int X, int Z)> columns, Dictionary<(int X, int Z), double> heights)
    {
        heights.Clear();
        var fogged = frame.Update.Environment.FoggedRegions.Count(region =>
        {
            var dx = Math.Max(region.Minimum.X + frame.Origin.X - focusCenter.X,
                Math.Max(0, focusCenter.X - region.Maximum.X - frame.Origin.X));
            var dz = Math.Max(region.Minimum.Z + frame.Origin.Z - focusCenter.Y,
                Math.Max(0, focusCenter.Y - region.Maximum.Z - frame.Origin.Z));
            return dx * dx + dz * dz <= 192 * 192;
        });
        if (frame.Readiness == SceneReadiness.Ready && fogged == 0)
            foreach (var (x, z) in columns)
                if (frame.TerrainHeight(x + .5 - frame.Origin.X, z + .5 - frame.Origin.Z) is Lookup<double>.Found height)
                    heights[(x, z)] = height.Value + frame.Origin.Y;
        return new(frame.Readiness, fogged, columns.Count, heights.Count);
    }

    internal static void ApplyUpdate(Dictionary<SceneObjectId, SceneObject> objects, SceneUpdate update)
    {
        if (update.Base is null) objects.Clear();
        else if (update.ShiftExistingRoots != Vector3.Zero)
            foreach (var root in objects.Values.Where(o => o.Parent is null).ToArray())
                objects[root.Id] = root with { Pose = root.Pose with { Position = root.Pose.Position + update.ShiftExistingRoots } };
        foreach (var id in update.Removed) objects.Remove(id);
        foreach (var item in update.Upserts) objects[item.Id] = item;
    }
}
