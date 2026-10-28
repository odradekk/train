using System.Numerics;
using Train.Scenes.Contracts;

namespace Train.SceneScale;

internal static partial class Probe
{
    // category 只描述全部公开 Geometry 顶点是否落在半径 192 的圆内，不加容差，不推断产品对象种类。
    internal sealed record LocalGeometry(LocalGeometryObject[] objects,
        LocalGeometryCategory fully_inside, LocalGeometryCategory crossing_boundary);
    internal sealed record LocalGeometryCategory(int objects, int triangles, string triangleWorldCoordinatesMaterialSha256,
        int nonRailTriangles = 0, string nonRailTriangleWorldCoordinatesMaterialSha256 = "");
    internal sealed record LocalGeometryObject(SceneObjectId id, SceneObjectId? parent, string? subject,
        ScenePose worldPose, SceneBounds worldBounds, string[] materials, string category,
        int localTriangles, string triangleWorldCoordinatesMaterialSha256,
        int nonRailTriangles = 0, string nonRailTriangleWorldCoordinatesMaterialSha256 = "");

    internal sealed record CachedLocalGeometry(SceneMesh[] Meshes, ScenePose Pose, Vector2 Focus,
        SceneBounds? Bounds, bool FullyInside, string[] Materials, HashSet<string> Triangles,
        HashSet<string> Rails, string TriangleSha256, HashSet<string> NonRailTriangles, string NonRailTriangleSha256);

    internal sealed class LocalGeometryCache
    {
        private readonly Dictionary<SceneObjectId, CachedLocalGeometry> entries = [];

        internal CachedLocalGeometry GeometryFor(SceneObject item, ScenePose worldPose, Vector2 focusCenter)
        {
            var meshes = item.Visuals.OfType<SceneVisual.Geometry>().Select(geometry => geometry.Mesh);
            if (entries.TryGetValue(item.Id, out var cached) && cached.Pose == worldPose && cached.Focus == focusCenter &&
                meshes.SequenceEqual(cached.Meshes, ReferenceEqualityComparer.Instance)) return cached;
            var built = BuildLocalGeometry(meshes.ToArray(), worldPose, focusCenter);
            entries[item.Id] = built;
            return built;
        }

        internal void Retain(IReadOnlyDictionary<SceneObjectId, SceneObject> objects)
        {
            foreach (var id in entries.Keys.Where(id => !objects.ContainsKey(id)).ToArray()) entries.Remove(id);
        }
    }

    private static CachedLocalGeometry BuildLocalGeometry(SceneMesh[] meshes, ScenePose pose, Vector2 focusCenter)
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var hasVertices = false;
        var fullyInside = true;
        var triangles = new HashSet<string>(StringComparer.Ordinal);
        var rails = new HashSet<string>(StringComparer.Ordinal);
        var nonRailTriangles = new HashSet<string>(StringComparer.Ordinal);
        Vector3 World(Vector3 point) => pose.Position + Vector3.Transform(point, pose.Rotation);
        foreach (var mesh in meshes)
            foreach (var vertex in mesh.Vertices)
            {
                var point = World(vertex.Position);
                minimum = Vector3.Min(minimum, point);
                maximum = Vector3.Max(maximum, point);
                hasVertices = true;
                fullyInside &= Vector2.DistanceSquared(new(point.X, point.Z), focusCenter) <= 192 * 192;
            }
        var bounds = hasVertices ? new SceneBounds(minimum, maximum) : null;
        if (bounds is not null)
        {
            var dx = Math.Max(bounds.Minimum.X - focusCenter.X, Math.Max(0, focusCenter.X - bounds.Maximum.X));
            var dz = Math.Max(bounds.Minimum.Z - focusCenter.Y, Math.Max(0, focusCenter.Y - bounds.Maximum.Z));
            if (dx * dx + dz * dz <= 192.001f * 192.001f)
                foreach (var mesh in meshes)
                    for (int t = 0; t < mesh.TriangleIndices.Length; t += 3)
                    {
                        var a = World(mesh.Vertices[mesh.TriangleIndices[t]].Position);
                        var b = World(mesh.Vertices[mesh.TriangleIndices[t + 1]].Position);
                        var c = World(mesh.Vertices[mesh.TriangleIndices[t + 2]].Position);
                        if (!LocalTriangleIntersects(a, b, c, focusCenter)) continue;
                        var vertices = new[] { a, b, c }.Select(v => $"{Q(v.X)},{Q(v.Y)},{Q(v.Z)}").OrderBy(x => x, StringComparer.Ordinal);
                        var key = mesh.Material.ToString() + "|" + string.Join(';', vertices);
                        triangles.Add(key);
                        if (mesh.Material is SceneMaterial.BuiltIn { Kind: BuiltInMaterial.RailSteel }) rails.Add(key);
                        if (mesh.Material is not SceneMaterial.BuiltIn { Kind: BuiltInMaterial.RailSteel or BuiltInMaterial.Sleeper })
                            nonRailTriangles.Add(key);
                    }
        }
        return new(meshes, pose, focusCenter, bounds, fullyInside,
            meshes.Select(mesh => mesh.Material.ToString()!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            triangles, rails, Digest(triangles), nonRailTriangles, Digest(nonRailTriangles));
    }
}
