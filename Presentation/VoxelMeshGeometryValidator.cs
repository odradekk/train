using Godot;
using Train.Domain.Voxels;

namespace Train.Presentation;

internal static class VoxelMeshGeometryValidator
{
    public static void Validate(Godot.Mesh mesh, int expectedFaceCount)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.GetSurfaceCount() < 1)
            throw new InvalidOperationException("Voxel mesh has no surfaces.");

        var allVertices = new List<Vector3>();
        var allNormals = new List<Vector3>();
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            if (mesh.SurfaceGetMaterial(surface) is not BaseMaterial3D material ||
                material.CullMode != BaseMaterial3D.CullModeEnum.Back)
            {
                throw new InvalidOperationException("Voxel mesh must retain back-face culling.");
            }

            var arrays = mesh.SurfaceGetArrays(surface);
            allVertices.AddRange(arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array());
            allNormals.AddRange(arrays[(int)Godot.Mesh.ArrayType.Normal].AsVector3Array());
        }

        var vertices = allVertices.ToArray();
        var normals = allNormals.ToArray();
        if (vertices.Length != expectedFaceCount * 6 || normals.Length != vertices.Length)
            throw new InvalidOperationException("Voxel mesh does not contain two triangles per exposed face.");

        var trianglesByFace = new Dictionary<FaceKey, List<Triangle>>();
        for (var index = 0; index < vertices.Length; index += 3)
        {
            var normal = normals[index];
            if (normal != normals[index + 1] || normal != normals[index + 2])
                throw new InvalidOperationException("Voxel mesh triangle normals are inconsistent.");

            var triangle = new Triangle(vertices[index], vertices[index + 1], vertices[index + 2]);
            var cross = (triangle.B - triangle.A).Cross(triangle.C - triangle.A);
            if (cross.Dot(normal) >= -0.0001f)
                throw new InvalidOperationException("Voxel mesh triangle is not clockwise when viewed from outside.");
            if (!Mathf.IsEqualApprox(cross.Length(), 1.0f))
                throw new InvalidOperationException("Voxel mesh triangle does not cover half of a unit face.");

            var key = FaceKey.From(normal, triangle);
            if (!trianglesByFace.TryGetValue(key, out var triangles))
            {
                triangles = new List<Triangle>();
                trianglesByFace.Add(key, triangles);
            }
            triangles.Add(triangle);
        }

        if (trianglesByFace.Count != expectedFaceCount)
            throw new InvalidOperationException("Voxel mesh triangles do not map one-to-one to exposed faces.");

        foreach (var triangles in trianglesByFace.Values)
            ValidateUnitFaceCoverage(triangles);
    }

    public static bool HasPublishedFace(Godot.Mesh mesh, WorldVoxelCoord position, VoxelFaceNormal normal)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var expectedNormal = WorldNormal(normal);
        var (minimum, maximum) = FaceBounds(position, normal);
        foreach (var (a, b, c) in PublishedTriangles(mesh))
        {
            if (!OnBounds(a, minimum, maximum) ||
                !OnBounds(b, minimum, maximum) ||
                !OnBounds(c, minimum, maximum))
            {
                continue;
            }

            var geometric = (b - a).Cross(c - a);
            if (geometric.Length() < 0.5f)
                continue;
            // SurfaceTool writes clockwise triangles when viewed from outside, so
            // (B-A)×(C-A) points inward. The published outward normal is the opposite.
            var outward = (-geometric).Normalized();
            if (NormalsEqual(outward, expectedNormal))
                return true;
        }

        return false;
    }

    public static bool HasAnyPublishedFace(Godot.Mesh mesh, WorldVoxelCoord position)
    {
        foreach (var normal in Enum.GetValues<VoxelFaceNormal>())
        {
            if (HasPublishedFace(mesh, position, normal))
                return true;
        }

        return false;
    }

    public static IReadOnlyDictionary<(int X, int Y, int Z), Vector2> ReadWorldTopUvs(
        Godot.Mesh mesh,
        WorldVoxelCoord cell)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var corners = new Dictionary<(int X, int Y, int Z), Vector2>();
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var arrays = mesh.SurfaceGetArrays(surface);
            var vertices = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
            var uvs = arrays[(int)Godot.Mesh.ArrayType.TexUV].AsVector2Array();
            if (vertices.Length != uvs.Length || vertices.Length % 3 != 0)
                continue;
            for (var index = 0; index < vertices.Length; index += 3)
            {
                if (!OnWorldTop(vertices[index], cell) ||
                    !OnWorldTop(vertices[index + 1], cell) ||
                    !OnWorldTop(vertices[index + 2], cell))
                    continue;
                for (var offset = 0; offset < 3; offset++)
                {
                    var vertex = vertices[index + offset];
                    var dx = (int)Mathf.Round(vertex.X - cell.X);
                    var dz = (int)Mathf.Round(vertex.Z - cell.Z);
                    corners[(dx, 1, dz)] = uvs[index + offset];
                }
            }
        }

        if (corners.Count != 4)
            throw new InvalidOperationException($"Generated mesh is missing world-top UVs for {cell}.");
        return corners;
    }

    private static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> PublishedTriangles(Godot.Mesh mesh)
    {
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var arrays = mesh.SurfaceGetArrays(surface);
            var vertices = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
            if (vertices.Length == 0)
                continue;
            var indices = ReadIndices(arrays);
            var count = indices.Length > 0 ? indices.Length : vertices.Length;
            if (count % 3 != 0)
                continue;
            for (var index = 0; index < count; index += 3)
            {
                var first = indices.Length > 0 ? indices[index] : index;
                var second = indices.Length > 0 ? indices[index + 1] : index + 1;
                var third = indices.Length > 0 ? indices[index + 2] : index + 2;
                if (first < 0 || second < 0 || third < 0 ||
                    first >= vertices.Length || second >= vertices.Length || third >= vertices.Length)
                    continue;
                yield return (vertices[first], vertices[second], vertices[third]);
            }
        }
    }

    private static int[] ReadIndices(Godot.Collections.Array arrays)
    {
        if (arrays.Count <= (int)Godot.Mesh.ArrayType.Index)
            return Array.Empty<int>();
        var values = arrays[(int)Godot.Mesh.ArrayType.Index];
        if (values.VariantType is Variant.Type.Nil or Variant.Type.Max)
            return Array.Empty<int>();
        try
        {
            return values.AsInt32Array() ?? Array.Empty<int>();
        }
        catch (InvalidCastException)
        {
            return Array.Empty<int>();
        }
    }

    private static Vector3 WorldNormal(VoxelFaceNormal normal) => normal switch
    {
        VoxelFaceNormal.PositiveX => Vector3.Right,
        VoxelFaceNormal.NegativeX => Vector3.Left,
        VoxelFaceNormal.PositiveY => Vector3.Up,
        VoxelFaceNormal.NegativeY => Vector3.Down,
        VoxelFaceNormal.PositiveZ => Vector3.Back,
        VoxelFaceNormal.NegativeZ => Vector3.Forward,
        _ => throw new ArgumentOutOfRangeException(nameof(normal), normal, "Unknown face normal.")
    };

    private static (Vector3 Minimum, Vector3 Maximum) FaceBounds(WorldVoxelCoord position, VoxelFaceNormal normal) =>
        normal switch
        {
            VoxelFaceNormal.PositiveX => (
                new Vector3(position.X + 1, position.Y, position.Z),
                new Vector3(position.X + 1, position.Y + 1, position.Z + 1)),
            VoxelFaceNormal.NegativeX => (
                new Vector3(position.X, position.Y, position.Z),
                new Vector3(position.X, position.Y + 1, position.Z + 1)),
            VoxelFaceNormal.PositiveY => (
                new Vector3(position.X, position.Y + 1, position.Z),
                new Vector3(position.X + 1, position.Y + 1, position.Z + 1)),
            VoxelFaceNormal.NegativeY => (
                new Vector3(position.X, position.Y, position.Z),
                new Vector3(position.X + 1, position.Y, position.Z + 1)),
            VoxelFaceNormal.PositiveZ => (
                new Vector3(position.X, position.Y, position.Z + 1),
                new Vector3(position.X + 1, position.Y + 1, position.Z + 1)),
            VoxelFaceNormal.NegativeZ => (
                new Vector3(position.X, position.Y, position.Z),
                new Vector3(position.X + 1, position.Y + 1, position.Z)),
            _ => throw new ArgumentOutOfRangeException(nameof(normal), normal, "Unknown face normal.")
        };

    private static bool NormalsEqual(Vector3 first, Vector3 second) =>
        Mathf.IsEqualApprox(first.X, second.X) &&
        Mathf.IsEqualApprox(first.Y, second.Y) &&
        Mathf.IsEqualApprox(first.Z, second.Z);

    private static bool OnBounds(Vector3 point, Vector3 minimum, Vector3 maximum) =>
        point.X >= minimum.X - 0.01f && point.X <= maximum.X + 0.01f &&
        point.Y >= minimum.Y - 0.01f && point.Y <= maximum.Y + 0.01f &&
        point.Z >= minimum.Z - 0.01f && point.Z <= maximum.Z + 0.01f;

    private static bool OnWorldTop(Vector3 vertex, WorldVoxelCoord cell) =>
        Mathf.IsEqualApprox(vertex.Y, cell.Y + 1) &&
        vertex.X >= cell.X - 0.01f && vertex.X <= cell.X + 1.01f &&
        vertex.Z >= cell.Z - 0.01f && vertex.Z <= cell.Z + 1.01f;

    private static void ValidateUnitFaceCoverage(List<Triangle> triangles)
    {
        if (triangles.Count != 2)
            throw new InvalidOperationException("An exposed voxel face is not made from exactly two triangles.");

        var edgeUses = new Dictionary<EdgeKey, int>();
        foreach (var triangle in triangles)
        {
            AddEdge(edgeUses, triangle.A, triangle.B);
            AddEdge(edgeUses, triangle.B, triangle.C);
            AddEdge(edgeUses, triangle.C, triangle.A);
        }

        var perimeterEdges = edgeUses.Where(edge => edge.Value == 1).Select(edge => edge.Key).ToArray();
        var sharedEdges = edgeUses.Where(edge => edge.Value == 2).Select(edge => edge.Key).ToArray();
        if (perimeterEdges.Length != 4 ||
            perimeterEdges.Any(edge => !Mathf.IsEqualApprox(edge.First.DistanceSquaredTo(edge.Second), 1.0f)) ||
            sharedEdges.Length != 1 ||
            !Mathf.IsEqualApprox(sharedEdges[0].First.DistanceSquaredTo(sharedEdges[0].Second), 2.0f))
        {
            throw new InvalidOperationException("Voxel mesh triangles overlap or leave a gap in a unit face.");
        }
    }

    private static void AddEdge(Dictionary<EdgeKey, int> edges, Vector3 first, Vector3 second)
    {
        var key = EdgeKey.From(first, second);
        edges[key] = edges.GetValueOrDefault(key) + 1;
    }

    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C);

    private readonly record struct FaceKey(Vector3 Normal, Vector3 Minimum, Vector3 Maximum)
    {
        public static FaceKey From(Vector3 normal, Triangle triangle) =>
            new(
                normal,
                new Vector3(
                    Mathf.Min(triangle.A.X, Mathf.Min(triangle.B.X, triangle.C.X)),
                    Mathf.Min(triangle.A.Y, Mathf.Min(triangle.B.Y, triangle.C.Y)),
                    Mathf.Min(triangle.A.Z, Mathf.Min(triangle.B.Z, triangle.C.Z))),
                new Vector3(
                    Mathf.Max(triangle.A.X, Mathf.Max(triangle.B.X, triangle.C.X)),
                    Mathf.Max(triangle.A.Y, Mathf.Max(triangle.B.Y, triangle.C.Y)),
                    Mathf.Max(triangle.A.Z, Mathf.Max(triangle.B.Z, triangle.C.Z))));
    }

    private readonly record struct EdgeKey(Vector3 First, Vector3 Second)
    {
        public static EdgeKey From(Vector3 first, Vector3 second) =>
            Compare(first, second) <= 0 ? new(first, second) : new(second, first);

        private static int Compare(Vector3 first, Vector3 second)
        {
            var x = first.X.CompareTo(second.X);
            if (x != 0)
                return x;
            var y = first.Y.CompareTo(second.Y);
            return y != 0 ? y : first.Z.CompareTo(second.Z);
        }
    }
}
