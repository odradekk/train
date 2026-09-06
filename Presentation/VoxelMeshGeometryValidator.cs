using Godot;

namespace Train.Presentation;

internal static class VoxelMeshGeometryValidator
{
    public static void Validate(Godot.Mesh mesh, int expectedFaceCount)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.SurfaceGetMaterial(0) is not BaseMaterial3D material ||
            material.CullMode != BaseMaterial3D.CullModeEnum.Back)
        {
            throw new InvalidOperationException("Voxel mesh must retain back-face culling.");
        }

        var arrays = mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = arrays[(int)Godot.Mesh.ArrayType.Normal].AsVector3Array();
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
