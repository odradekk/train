using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class VoxelShapes(VoxelGrid grid)
{
    private sealed record Face(ImmutableArray<Vector3> Corners, Vector3 Normal, GridFace? Boundary);
    private sealed record Shape(ImmutableArray<Face> Faces, int Coverage);
    private static readonly Dictionary<string, Shape> shapes = new(StringComparer.Ordinal)
    {
        ["half"] = new(Box(new(0, 0, 0), new(1, .5f, 1)), 1 << (int)GridFace.NegativeY),
        ["plate"] = new(Box(new(0, 0, 0), new(1, .125f, 1)), 1 << (int)GridFace.NegativeY),
        ["beam"] = new(Box(new(.375f, 0, .375f), new(.625f, 1, .625f)), 0),
        ["wedge"] = new([
            Polygon(-Vector3.UnitY, new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)),
            Polygon(-Vector3.UnitZ, new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0)),
            Polygon(-Vector3.UnitX, new(0, 0, 0), new(0, 0, 1), new(0, 1, 0)),
            Polygon(Vector3.UnitX, new(1, 0, 0), new(1, 1, 0), new(1, 0, 1)),
            Polygon(new(0, 1, 1), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1), new(1, 1, 0))],
            (1 << (int)GridFace.NegativeY) | (1 << (int)GridFace.NegativeZ)),
        ["corner-wedge"] = new([
            Polygon(-Vector3.UnitY, new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)),
            Polygon(-Vector3.UnitX, new(0, 0, 0), new(0, 0, 1), new(0, 1, 0)),
            Polygon(-Vector3.UnitZ, new(0, 0, 0), new(0, 1, 0), new(1, 0, 0)),
            Polygon(new(1, 1, 0), new(0, 1, 0), new(1, 0, 1), new(1, 0, 0)),
            Polygon(new(0, 1, 1), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1))],
            1 << (int)GridFace.NegativeY),
        ["cylinder"] = new(Cylinder(), 0)
    };
    private readonly Dictionary<(GridPosition Chunk, SceneMaterial Material),
        (List<SceneVertex> Vertices, List<int> Indices)> groups = [];

    internal static string Name(VoxelShape? selected, VoxelAppearanceProjection? appearance) => selected?.Name ??
        (appearance?.Presentation is VoxelPresentation.Cube { Shapes.IsDefaultOrEmpty: false } cube ? cube.Shapes[0].Name : "cube");

    internal static int Coverage(string name, VoxelOrientation orientation)
    {
        if (name == "cube") return 63;
        var coverage = shapes[name].Coverage;
        if (coverage == 0) return 0;
        var axes = SceneGeometry.Axes(orientation);
        var result = 0;
        for (var face = GridFace.NegativeX; face <= GridFace.PositiveZ; face++)
            if ((coverage & (1 << (int)face)) != 0)
            {
                var normal = SceneGeometry.Normal(face);
                var rotated = normal.X * axes.Right + normal.Y * axes.Up + normal.Z * axes.Forward;
                result |= 1 << (int)AxisFace(rotated);
            }
        return result;
    }

    internal void Add(SceneCell cell, float damage = 0)
    {
        if (cell.Appearance?.Presentation is not VoxelPresentation.Cube cube) return;
        var shape = shapes[cell.Properties.ShapeName];
        var axes = SceneGeometry.Axes(cell.Orientation);
        foreach (var face in shape.Faces)
        {
            if (face.Boundary is { } boundary)
            {
                var outside = Rotate(SceneGeometry.Normal(boundary));
                if (grid.Occludes(Offset(outside), AxisFace(-outside))) continue;
            }
            var projection = Rotate(SceneGeometry.Normal(AxisFace(face.Normal)));
            var normal = Rotate(face.Normal);
            var front = projection == axes.Forward && cube.Front != cube.Side;
            var texture = projection == axes.Up ? cube.Top : projection == -axes.Up ? cube.Bottom : front ? cube.Front : cube.Side;
            var imageUp = projection == axes.Up || projection == -axes.Up ? axes.Forward : axes.Up;
            var textureU = Vector3.Cross(imageUp, projection);
            var textureV = -imageUp;
            var span = front ? 1 : cube.TileSpan;
            var material = new SceneMaterial.Voxel(texture, cube.Material, cell.Paint);
            var key = (VoxelGrid.Chunk(cell.At), (SceneMaterial)material);
            if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = ([], []));
            var u = projection.X != 0 ? Vector3.UnitZ : Vector3.UnitX;
            var v = Vector3.Cross(projection, u);
            var edges = ExposedFaceEdges.None;
            if (!grid.Contains(Offset(-u))) edges |= ExposedFaceEdges.MinimumU;
            if (!grid.Contains(Offset(u))) edges |= ExposedFaceEdges.MaximumU;
            if (!grid.Contains(Offset(-v))) edges |= ExposedFaceEdges.MinimumV;
            if (!grid.Contains(Offset(v))) edges |= ExposedFaceEdges.MaximumV;
            var first = group.Vertices.Count;
            foreach (var point in face.Corners)
            {
                var corner = new Vector3(.5f) + Rotate(point - new Vector3(.5f));
                var position = new Vector3(cell.At.X, cell.At.Y, cell.At.Z) + corner;
                var textureUv = new Vector2(DesignGeometry.TextureCoordinate(cell.At, corner, textureU, span),
                    DesignGeometry.TextureCoordinate(cell.At, corner, textureV, span));
                var center = new Vector3(.5f) + projection * .5f;
                var faceUv = new Vector2(Vector3.Dot(corner - center, u) + .5f, Vector3.Dot(corner - center, v) + .5f);
                var bounds = grid.Bounds!;
                var height = (position.Y - bounds.Minimum.Y) / (bounds.Maximum.Y - bounds.Minimum.Y);
                group.Vertices.Add(new(position, normal, textureUv, faceUv, edges, Math.Clamp(height, 0, 1), damage));
            }
            for (var index = 1; index < face.Corners.Length - 1; index++)
            {
                group.Indices.Add(first);
                group.Indices.Add(first + index);
                group.Indices.Add(first + index + 1);
            }
        }
        Vector3 Rotate(Vector3 point) => point.X * axes.Right + point.Y * axes.Up + point.Z * axes.Forward;
        GridPosition Offset(Vector3 offset) => new(cell.At.X + (int)offset.X, cell.At.Y + (int)offset.Y, cell.At.Z + (int)offset.Z);
    }

    internal IEnumerable<SceneVisual> Build(SurfaceState surface) => groups.Select(group =>
        (SceneVisual)new SceneVisual.Geometry(new(group.Key.Material, group.Value.Vertices.ToImmutableArray(),
            group.Value.Indices.ToImmutableArray()), surface));

    private static GridFace AxisFace(Vector3 normal)
    {
        var absolute = Vector3.Abs(normal);
        if (absolute.Y >= absolute.X && absolute.Y >= absolute.Z)
            return normal.Y < 0 ? GridFace.NegativeY : GridFace.PositiveY;
        if (absolute.X >= absolute.Z) return normal.X < 0 ? GridFace.NegativeX : GridFace.PositiveX;
        return normal.Z < 0 ? GridFace.NegativeZ : GridFace.PositiveZ;
    }

    private static Face Polygon(Vector3 normal, params Vector3[] corners)
    {
        GridFace? boundary = null;
        for (var face = GridFace.NegativeX; face <= GridFace.PositiveZ; face++)
        {
            var direction = SceneGeometry.Normal(face);
            var plane = (int)face % 2 == 0 ? 0 : 1;
            if (corners.All(point => Vector3.Dot(point, Vector3.Abs(direction)) == plane)) boundary = face;
        }
        return new(corners.ToImmutableArray(), Vector3.Normalize(normal), boundary);
    }

    private static ImmutableArray<Face> Box(Vector3 minimum, Vector3 maximum)
    {
        var faces = ImmutableArray.CreateBuilder<Face>();
        for (var face = GridFace.NegativeX; face <= GridFace.PositiveZ; face++)
        {
            var normal = SceneGeometry.Normal(face);
            var u = normal.X != 0 ? Vector3.UnitZ : Vector3.UnitX;
            var v = Vector3.Cross(normal, u);
            var center = new Vector3(.5f) + normal * .5f;
            var corners = new[] { center - u * .5f - v * .5f, center + u * .5f - v * .5f,
                center + u * .5f + v * .5f, center - u * .5f + v * .5f };
            faces.Add(Polygon(normal, corners.Select(point => minimum + point * (maximum - minimum)).ToArray()));
        }
        return faces.ToImmutable();
    }

    private static ImmutableArray<Face> Cylinder()
    {
        var bottom = Enumerable.Range(0, 12).Select(index => new Vector3(
            .5f + .5f * (float)Math.Cos(index * Math.Tau / 12), 0,
            .5f + .5f * (float)Math.Sin(index * Math.Tau / 12))).ToArray();
        var top = bottom.Select(point => point + Vector3.UnitY).ToArray();
        var faces = ImmutableArray.CreateBuilder<Face>();
        faces.Add(Polygon(-Vector3.UnitY, bottom));
        faces.Add(Polygon(Vector3.UnitY, top.Reverse().ToArray()));
        for (var index = 0; index < bottom.Length; index++)
        {
            var next = (index + 1) % bottom.Length;
            var angle = (index + .5) * Math.Tau / 12;
            var normal = new Vector3((float)Math.Cos(angle), 0, (float)Math.Sin(angle));
            faces.Add(Polygon(normal, bottom[index], top[index], top[next], bottom[next]));
        }
        return faces.ToImmutable();
    }
}
