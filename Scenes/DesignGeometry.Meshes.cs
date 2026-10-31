using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static partial class DesignGeometry
{
    private const int CubeBatchVertexLimit = 8192;

    internal static ImmutableArray<SceneVisual> BatchCubes(ImmutableArray<SceneVisual> cubes)
    {
        var batches = ImmutableArray.CreateBuilder<SceneVisual>();
        foreach (var group in cubes.Cast<SceneVisual.Geometry>().GroupBy(visual => (visual.Mesh.Material, visual.Surface)))
        {
            var pending = new List<SceneVisual.Geometry>();
            var vertexCount = 0;
            foreach (var visual in group)
            {
                if (vertexCount + visual.Mesh.Vertices.Length > CubeBatchVertexLimit) Flush();
                if (visual.Mesh.Vertices.Length > CubeBatchVertexLimit) { batches.Add(visual); continue; }
                pending.Add(visual);
                vertexCount += visual.Mesh.Vertices.Length;
            }
            Flush();

            void Flush()
            {
                if (pending.Count == 0) return;
                if (pending.Count == 1) batches.Add(pending[0]);
                else
                {
                    var vertices = ImmutableArray.CreateBuilder<SceneVertex>(vertexCount);
                    var indices = ImmutableArray.CreateBuilder<int>(pending.Sum(visual => visual.Mesh.TriangleIndices.Length));
                    foreach (var visual in pending)
                    {
                        var offset = vertices.Count;
                        vertices.AddRange(visual.Mesh.Vertices);
                        foreach (var index in visual.Mesh.TriangleIndices) indices.Add(offset + index);
                    }
                    batches.Add(new SceneVisual.Geometry(new(group.Key.Material, vertices.DrainToImmutable(),
                        indices.DrainToImmutable()), group.Key.Surface));
                }
                pending.Clear();
                vertexCount = 0;
            }
        }
        return batches.DrainToImmutable();
    }

    internal static ImmutableArray<SceneVisual> Meshes(VoxelGrid grid, bool wear, ulong wearSeed = 0,
        IReadOnlyDictionary<GridPosition, float>? cellDamage = null, SurfaceState? surface = null)
    {
        var meshes = BuildMeshes(grid, grid.Cells, wear, wearSeed, cellDamage, surface);
        return meshes.Cubes.AddRange(meshes.Shapes);
    }

    internal readonly record struct MeshGroups(ImmutableArray<SceneVisual> Cubes, ImmutableArray<SceneVisual> Shapes);

    internal static MeshGroups MeshChunk(VoxelGrid grid, GridPosition chunk) =>
        BuildMeshes(grid, grid.CellsInChunk(chunk), true, 0, null, null);

    private static MeshGroups BuildMeshes(VoxelGrid grid, IEnumerable<SceneCell> cells, bool wear, ulong wearSeed,
        IReadOnlyDictionary<GridPosition, float>? cellDamage, SurfaceState? surface)
    {
        var shapes = new VoxelShapes(grid);
        var groups = new Dictionary<(GridPosition Chunk, SceneMaterial Material),
            (List<FaceQuad> Quads, Dictionary<FacePlane, uint[]> Planes)>();
        foreach (var cell in cells)
        {
            var amount = cellDamage is not null && cellDamage.TryGetValue(cell.At, out var found) ? found : 0;
            if (!cell.Occludes) { shapes.Add(cell, amount); continue; }
            var cube = (VoxelPresentation.Cube)cell.Appearance!.Presentation;
            var axes = SceneGeometry.Axes(cell.Orientation);
            for (var face = GridFace.NegativeX; face <= GridFace.PositiveZ; face++)
            {
                var normal = SceneGeometry.Normal(face);
                var at = cell.At;
                if (grid.Occludes(new(at.X + (int)normal.X, at.Y + (int)normal.Y, at.Z + (int)normal.Z),
                    (GridFace)((int)face ^ 1))) continue;
                var front = normal == axes.Forward && cube.Front != cube.Side;
                var texture = normal == axes.Up ? cube.Top : normal == -axes.Up ? cube.Bottom : front ? cube.Front : cube.Side;
                var imageUp = normal == axes.Up || normal == -axes.Up ? axes.Forward : axes.Up;
                var textureU = Vector3.Cross(imageUp, normal);
                var textureV = -imageUp;
                var span = front ? 1 : cube.TileSpan;
                var material = new SceneMaterial.Voxel(texture, cube.Material, cell.Paint);
                var key = (VoxelGrid.Chunk(at), (SceneMaterial)material);
                if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = ([], []));
                var u = normal.X != 0 ? Vector3.UnitZ : Vector3.UnitX;
                var v = Vector3.Cross(normal, u);
                var plane = new FacePlane(face, normal.X != 0 ? at.X : normal.Y != 0 ? at.Y : at.Z,
                    textureU, textureV, span, amount);
                if (!wear)
                {
                    if (!group.Planes.TryGetValue(plane, out var rows))
                        group.Planes.Add(plane, rows = new uint[VoxelGrid.ChunkSize]);
                    var column = (u.X != 0 ? at.X : at.Z) & 31;
                    var row = (v.Y != 0 ? at.Y : at.Z) & 31;
                    if (v.Y + v.Z < 0) row = 31 - row;
                    rows[row] |= 1u << column;
                    continue;
                }
                var edges = ExposedFaceEdges.None;
                if (Empty(-u)) edges |= ExposedFaceEdges.MinimumU;
                if (Empty(u)) edges |= ExposedFaceEdges.MaximumU;
                if (Empty(-v)) edges |= ExposedFaceEdges.MinimumV;
                if (Empty(v)) edges |= ExposedFaceEdges.MaximumV;
                group.Quads.Add(new(at, plane, 1, 1, edges));
                bool Empty(Vector3 offset) => !grid.Contains(new(at.X + (int)offset.X, at.Y + (int)offset.Y, at.Z + (int)offset.Z));
            }
        }
        foreach (var (key, group) in groups)
        foreach (var (plane, rows) in group.Planes)
        {
            var normal = SceneGeometry.Normal(plane.Face);
            var u = normal.X != 0 ? Vector3.UnitZ : Vector3.UnitX;
            var v = Vector3.Cross(normal, u);
            for (var row = 0; row < rows.Length; row++)
            while (rows[row] != 0)
            {
                var column = BitOperations.TrailingZeroCount(rows[row]);
                var width = 1;
                while (column + width < VoxelGrid.ChunkSize && (rows[row] & (1u << (column + width))) != 0) width++;
                var mask = (uint.MaxValue >> (VoxelGrid.ChunkSize - width)) << column;
                var height = 1;
                while (row + height < rows.Length && (rows[row + height] & mask) == mask) height++;
                for (var next = row; next < row + height; next++) rows[next] &= ~mask;
                var alongV = v.Y + v.Z < 0 ? 31 - row : row;
                var chunk = key.Chunk;
                var at = normal.X != 0 ? new GridPosition(plane.Layer, (chunk.Y << 5) + alongV, (chunk.Z << 5) + column) :
                    normal.Y != 0 ? new GridPosition((chunk.X << 5) + column, plane.Layer, (chunk.Z << 5) + alongV) :
                    new GridPosition((chunk.X << 5) + column, (chunk.Y << 5) + alongV, plane.Layer);
                group.Quads.Add(new(at, plane, width, height, ExposedFaceEdges.None));
            }
        }
        surface ??= new(wear, wearSeed, 0, false);
        var visuals = ImmutableArray.CreateBuilder<SceneVisual>(groups.Count);
        foreach (var (key, group) in groups)
        {
            var vertexCount = 0;
            var indexCount = 0;
            foreach (var quad in group.Quads)
            {
                var unit = quad.Width == 1 && quad.Height == 1;
                var perimeter = 2 * (quad.Width + quad.Height);
                vertexCount += unit ? 4 : perimeter + 1;
                indexCount += unit ? 6 : perimeter * 3;
            }
            var vertices = ImmutableArray.CreateBuilder<SceneVertex>(vertexCount);
            var indices = ImmutableArray.CreateBuilder<int>(indexCount);
            foreach (var quad in group.Quads)
                AddQuad(vertices, indices, quad.At, quad.Plane, quad.Width, quad.Height, quad.Edges);
            visuals.Add(new SceneVisual.Geometry(new(key.Material, vertices.DrainToImmutable(), indices.DrainToImmutable()), surface));
        }
        return new(visuals.DrainToImmutable(), shapes.Build(surface).ToImmutableArray());

        void AddQuad(ImmutableArray<SceneVertex>.Builder vertices, ImmutableArray<int>.Builder indices, GridPosition at, FacePlane plane,
            int width, int height, ExposedFaceEdges edges)
        {
            var normal = SceneGeometry.Normal(plane.Face);
            var u = normal.X != 0 ? Vector3.UnitZ : Vector3.UnitX;
            var v = Vector3.Cross(normal, u);
            var center = new Vector3(0.5f) + normal * 0.5f;
            var first = vertices.Count;
            if (width == 1 && height == 1)
            {
                AddVertex(0, 0); AddVertex(1, 0); AddVertex(1, 1); AddVertex(0, 1);
                indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
                indices.Add(first); indices.Add(first + 2); indices.Add(first + 3);
                return;
            }
            // Unit border segments share endpoints across rectangles, materials and chunk boundaries.
            AddVertex(width / 2f, height / 2f);
            for (var along = 0; along < width; along++) AddVertex(along, 0);
            for (var along = 0; along < height; along++) AddVertex(width, along);
            for (var along = width; along > 0; along--) AddVertex(along, height);
            for (var along = height; along > 0; along--) AddVertex(0, along);
            var perimeter = 2 * (width + height);
            for (var edge = 0; edge < perimeter; edge++)
            {
                indices.Add(first);
                indices.Add(first + 1 + edge);
                indices.Add(first + 1 + (edge + 1) % perimeter);
            }

            void AddVertex(float alongU, float alongV)
            {
                var uv = new Vector2(alongU / width, alongV / height);
                var corner = center + u * (alongU - 0.5f) + v * (alongV - 0.5f);
                var position = new Vector3(at.X, at.Y, at.Z) + corner;
                var textureUv = new Vector2(TextureCoordinate(at, corner, plane.TextureU, plane.Span),
                    TextureCoordinate(at, corner, plane.TextureV, plane.Span));
                var bounds = grid.Bounds!;
                var relativeHeight = (position.Y - bounds.Minimum.Y) / (bounds.Maximum.Y - bounds.Minimum.Y);
                vertices.Add(new(position, normal, textureUv, uv, edges, Math.Clamp(relativeHeight, 0, 1), plane.Damage));
            }
        }
    }

    private readonly record struct FacePlane(GridFace Face, int Layer, Vector3 TextureU, Vector3 TextureV, int Span, float Damage = 0);
    private readonly record struct FaceQuad(GridPosition At, FacePlane Plane, int Width, int Height, ExposedFaceEdges Edges);

}
