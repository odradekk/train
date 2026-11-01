using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed partial class NodeScene
{
    internal sealed record Preparation(SceneWorldSnapshot Snapshot, Built Global,
        TerrainAppearanceProjection? Terrain, VoxelDefinitionId BallastVoxel,
        Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?> Appearances,
        Dictionary<GridColumn, ImmutableArray<SceneVoxelProjection>> ModelChunks,
        NodeBedIndex Beds,
        Dictionary<GridColumn, HashSet<GridColumn>> BoundaryChunks, string TerrainInputs,
        ImmutableDictionary<string, RailSegment> Rails,
        Dictionary<GridColumn, double> BoundarySurfaces,
        bool HasChunks = true)
    {
        internal string ChunkInputs(GridColumn from, CancellationToken token = default) =>
            ChunkInputs(from, Beds.ForChunk(from, token));

        private string ChunkInputs(GridColumn from, Dictionary<GridColumn, RailBed> bed)
        {
            var boundary = BoundaryChunks.GetValueOrDefault(from) ?? [];
            var surfaces = SurfacesNear(from);
            var model = ModelChunks.GetValueOrDefault(from, []);
            return RailwayDependencies.Values(new object?[] { TerrainInputs, Global.Layout.Template?.SceneModel.Design, from,
                model.Select(voxel => voxel.Voxel).Distinct().Select(id => Appearances[id]).ToArray(),
                bed.OrderBy(item => item.Key.X).ThenBy(item => item.Key.Z).ToArray(),
                boundary.OrderBy(item => item.X).ThenBy(item => item.Z).ToArray(),
                surfaces.OrderBy(item => item.Key.X).ThenBy(item => item.Key.Z).ToArray() });
        }

        internal Part BuildChunk(GridColumn from, Part? previous = null, CancellationToken token = default) =>
            BuildChunk(from, Beds.ForChunk(from, token), previous, token);

        internal Part BuildChunk(GridColumn from, Dictionary<GridColumn, RailBed> bed,
            Part? previous = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var inputs = ChunkInputs(from, bed);
            return previous?.Inputs == inputs ? previous : TerrainPart(Snapshot,
                Snapshot.Core.World.Nodes.Single(node => node.Id == Global.Layout.Node), Global.Layout, Terrain, BallastVoxel, Appearances,
                bed, BoundaryChunks.GetValueOrDefault(from) ?? [],
                ModelChunks.GetValueOrDefault(from, []), from, SurfacesNear(from), token) with { Inputs = inputs };
        }

        private Dictionary<GridColumn, double> SurfacesNear(GridColumn from)
        {
            var surfaces = new Dictionary<GridColumn, double>();
            foreach (var key in ChunkNeighborhood(from))
            {
                if (!BoundaryChunks.TryGetValue(key, out var columns)) continue;
                foreach (var column in columns)
                    if (NearChunk(from, column)) surfaces[column] = BoundarySurfaces[column];
            }
            return surfaces;
        }

        private static IEnumerable<GridColumn> ChunkNeighborhood(GridColumn from)
        {
            var size = VoxelGrid.ChunkSize;
            for (var z = from.Z - size; z <= from.Z + size; z += size)
            for (var x = from.X - size; x <= from.X + size; x += size)
                yield return new(x, z);
        }

        private static bool NearChunk(GridColumn from, GridColumn column)
        {
            var size = VoxelGrid.ChunkSize;
            return column.X >= from.X - 1 && column.X <= from.X + size &&
                column.Z >= from.Z - 1 && column.Z <= from.Z + size;
        }
    }

}
