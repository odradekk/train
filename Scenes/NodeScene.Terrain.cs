using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed partial class NodeScene
{
    private static Part TerrainPart(SceneWorldSnapshot snapshot, NodeMapItem node, NodeLayout layout,
        TerrainAppearanceProjection? terrain, VoxelDefinitionId ballastVoxel, Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?> appearances,
        Dictionary<GridColumn, RailBed> bed, HashSet<GridColumn> boundaryColumns, ImmutableArray<SceneVoxelProjection> model,
        GridColumn from, Dictionary<GridColumn, double> boundarySurfaces, CancellationToken token)
    {
        var cellCount = terrain is null ? 0 :
            (VoxelGrid.ChunkSize * VoxelGrid.ChunkSize - boundaryColumns.Count) * RailwayCoordinates.TerrainDepthCells;
        var cells = ImmutableArray.CreateBuilder<SceneCell>(cellCount);
        var heights = new Dictionary<GridColumn, double>(VoxelGrid.ChunkSize * VoxelGrid.ChunkSize);
        var subject = new PickSubject.Terrain(new WorldLocation.Node(node.Id));
        var layers = RailwayTerrain.PrepareLayers(terrain, id => appearances.GetValueOrDefault(id), subject);
        var ballast = bed.Count > 0 ? RailwayTerrain.PrepareVoxel(ballastVoxel, id => appearances.GetValueOrDefault(id), subject) : null;
        var bounds = layout.Bounds;
        var left = (int)bounds.Minimum.X;
        var right = (int)bounds.Maximum.X;
        var back = (int)bounds.Minimum.Y;
        var front = (int)bounds.Maximum.Y;
        var computed = new Dictionary<GridColumn, (int Height, double Distance)>();
        for (var z = from.Z; z < from.Z + VoxelGrid.ChunkSize; z++)
        for (var x = from.X; x < from.X + VoxelGrid.ChunkSize; x++)
        {
            token.ThrowIfCancellationRequested();
            var column = new GridColumn(x, z);
            if (boundaryColumns.Contains(column)) continue;
            var (height, distance) = At(column);
            heights.Add(column, height + 1);
            RailwayTerrain.FillColumn(cells, column, height, layers, ballast, distance, double.IsFinite(distance),
                lowestNeighbor: Lowest(column));
        }
        var modelCells = ImmutableArray.CreateBuilder<SceneCell>();
        foreach (var voxel in model)
        {
            var column = new GridColumn(voxel.At.X, voxel.At.Z);
            if (bed.TryGetValue(column, out var rail) && rail.Distance < RailwayCoordinates.DecorationVoxelClearance &&
                voxel.At.Y >= Math.Floor(rail.Height) - 3 && voxel.At.Y < rail.Height + 42) continue;
            var appearance = appearances.GetValueOrDefault(voxel.Voxel);
            var missing = voxel.Missing || appearance is null ? new ContentReference.Core(new CoreDefinitionReference.Voxel(voxel.Voxel)) : null;
            modelCells.Add(new(voxel.At, new(voxel.Orientation, appearance, voxel.Shape, voxel.Paint, subject, [], missing)));
        }
        var occupied = modelCells.Select(cell => cell.At).ToHashSet();
        cells.RemoveAll(cell => occupied.Contains(cell.At));
        var finalCount = cells.Count + modelCells.Count;
        if (finalCount > cells.Capacity) cells.Capacity = finalCount;
        cells.AddRange(modelCells);
        heights.TrimExcess();
        return GridPart(cells.DrainToImmutable(), heights.AsReadOnly(), []);

        (int Height, double Distance) At(GridColumn column)
        {
            if (!computed.TryGetValue(column, out var value))
                computed[column] = value = NodeColumn(snapshot, node, layout, bed, column);
            return value;
        }

        double? Lowest(GridColumn column)
        {
            double? lowest = null;
            void Consider(GridColumn neighbor)
            {
                if (neighbor.X < left || neighbor.X >= right || neighbor.Z < back || neighbor.Z >= front) return;
                var surface = boundarySurfaces.TryGetValue(neighbor, out var owned) ? owned : At(neighbor).Height;
                if (lowest is null || surface < lowest) lowest = surface;
            }
            Consider(new(column.X + 1, column.Z));
            Consider(new(column.X - 1, column.Z));
            Consider(new(column.X, column.Z + 1));
            Consider(new(column.X, column.Z - 1));
            return lowest;
        }
    }

    private static (int Height, double Distance) NodeColumn(SceneWorldSnapshot snapshot, NodeMapItem node, NodeLayout layout,
        Dictionary<GridColumn, RailBed> bed, GridColumn column)
    {
        var point = new ScenePoint(column.X + 0.5, 0, column.Z + 0.5);
        var rail = bed.GetValueOrDefault(column);
        var raw = TerrainSurface.NodeHeight(snapshot, node, layout, point, rail);
        return ((int)Math.Floor(raw), rail?.Distance ?? double.PositiveInfinity);
    }
}
