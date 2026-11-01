using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static partial class RailwayTerrain
{
    private sealed partial class SegmentBuilder
    {
        private RailwaySegment Render(Dictionary<GridColumn, TerrainColumn> columns, ImmutableArray<(double Start, double End)> tunnels)
        {
            var cells = ImmutableArray.CreateBuilder<SceneCell>();
            var heights = ImmutableDictionary.CreateBuilder<GridColumn, double>();
            var outside = new Dictionary<GridColumn, double?>();
            var properties = new Dictionary<VoxelDefinitionId, CellProperties>();
            Func<VoxelDefinitionId, CellProperties> property = Property;
            var waterCount = columns.Values.Count(value => value.Water);
            var vertices = ImmutableArray.CreateBuilder<SceneVertex>(waterCount * 4);
            var indices = ImmutableArray.CreateBuilder<int>(waterCount * 6);

            foreach (var (column, value) in columns)
            {
                cancellation.ThrowIfCancellationRequested();
                var trackDistance = surface.TrackDistance(value.Offset);
                var isTunnel = value.Tunnel;
                heights.Add(column, value.Height + 1);
                var hasBed = path.Edge.Tracks > 0 && !value.Water && !isTunnel;
                (double From, double To)? excluded = null;
                if (isTunnel && Math.Abs(value.Offset) < RailwayCoordinates.TunnelShoulder(path.Edge.Tracks))
                {
                    var railY = path.Sample(value.Arc).Position.Y;
                    var surface = value.Height;
                    var upper = Math.Min(railY + RailwayCoordinates.TunnelCrownCells, surface);
                    if (upper > railY) excluded = (railY, upper);
                }
                var lowest = LowestNeighbor(columns, outside, column, column.X + origin.X, column.Z + origin.Z);
                var layers = PrepareColumn(value.Terrain, path.Rules.BallastVoxel, value.Height, trackDistance, hasBed, excluded,
                    property, lowest);
                FillColumn(cells, column, value.Height, layers.Terrain, layers.Ballast, trackDistance, hasBed, origin.Y, excluded, lowest);
                if (value.Water)
                {
                    var y = (float)(surface.WaterLevel(value.Arc) - origin.Y);
                    var first = vertices.Count;
                    foreach (var corner in new Vector2[] { new(0, 0), new(0, 1), new(1, 1), new(1, 0) })
                        vertices.Add(new(new(column.X + corner.X, y, column.Z + corner.Y), Vector3.UnitY, corner, corner, ExposedFaceEdges.None, 0, 0));
                    indices.AddRange((ReadOnlySpan<int>)[first, first + 1, first + 2, first, first + 2, first + 3]);
                }
            }
            var grid = VoxelGrid.FromBuilder(cells);
            var visuals = DesignGeometry.Meshes(grid, false).ToBuilder();
            objects.AddRange(DesignGeometry.Models(grid, terrainId, null, _ => new(nextId++), false));
            foreach (var missingTerrain in terrains.Where(item => item.Value is null))
                visuals.Add(new SceneVisual.Missing(new ContentReference.Core(new CoreDefinitionReference.Terrain(missingTerrain.Key)), false));
            foreach (var missing in cells.Where(cell => cell.MissingReference is not null))
                objects.Add(new(new(nextId++), terrainId, SceneGeometry.Placement(missing.At, missing.Orientation), null,
                [new SceneVisual.Missing(missing.MissingReference!, true)], [], []));
            foreach (var mesh in RailwayMeshes.Build(path, index, origin))
                visuals.Add(new SceneVisual.Geometry(mesh, new(false, 0, 0, false)));
            if (waterCount > 0) visuals.Add(new SceneVisual.Geometry(new(new SceneMaterial.BuiltIn(BuiltInMaterial.Water),
            vertices.MoveToImmutable(), indices.MoveToImmutable()), new(false, 0, 0, false)));
            objects.Insert(0, new(terrainId, null, new(Vector3.Zero, Quaternion.Identity), null, visuals.ToImmutable(), [], []));
            picking.Add(new(terrainId, new(Vector3.Zero, Quaternion.Identity), grid,
                TerrainSubjects(path.Edge.Id, columns.ToImmutableDictionary(item => item.Key, item => item.Value.Arc))));
            return new(index, origin, new(objects.ToImmutable(), picking.ToImmutable(), diagnostics.ToImmutable()), heights.ToImmutable(),
            RailwayDependencies.Capture(snapshot, path, index, terrainReads, modelReads, models, voxelReads,
            readsStartTerrain, readsEndTerrain), tunnels);

            CellProperties Property(VoxelDefinitionId id)
            {
                if (!properties.TryGetValue(id, out var value))
                    properties.Add(id, value = PrepareVoxel(id, Appearance, null));
                return value;
            }
        }

        private static Func<GridPosition, PickSubject> TerrainSubjects(EdgeId edge, ImmutableDictionary<GridColumn, double> arcs) =>
            at => new PickSubject.Terrain(new WorldLocation.Edge(edge, arcs[new(at.X, at.Z)] / RailwayCoordinates.CellsPerMeter));
    }
}
