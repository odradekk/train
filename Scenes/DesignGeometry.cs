using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed record PickGrid(SceneObjectId Object, ScenePose Pose, VoxelGrid Grid);
internal sealed record RenderedDesign(ImmutableArray<SceneObject> Objects,
    ImmutableArray<PickGrid> Picking, ImmutableArray<SceneProblem> Diagnostics);

internal static class DesignGeometry
{
    internal static ImmutableArray<PartProjection> Parts(DesignEvaluation? evaluation) => evaluation switch
    {
        DesignEvaluation.Complete { Core: { } core } => core.Design.Parts,
        DesignEvaluation.Partial partial => partial.KnownSubtotal.Parts,
        _ => []
    };

    internal static DesignSceneProjection Projection(DesignEvaluation evaluation) => evaluation switch
    {
        DesignEvaluation.Complete complete => complete.Scene,
        DesignEvaluation.Partial partial => partial.Scene,
        _ => throw new ArgumentException("The evaluation has no interpretable design.", nameof(evaluation))
    };

    internal static RenderedDesign Build(IContentView content, DesignEvaluation evaluation,
        WorldObjectId? subject, ScenePose rootPose, Func<string, SceneObjectId> id,
        ImmutableHashSet<PartId>? destroyed = null, DesignHash? worldDesign = null, WorldLocation? terrainLocation = null,
        ulong wearSeed = 0, StructureDamage.Report? damage = null)
    {
        var diagnostics = evaluation switch
        {
            DesignEvaluation.Complete complete => complete.Diagnostics,
            DesignEvaluation.Partial partial => partial.Diagnostics,
            _ => ImmutableArray<ContentDiagnostic>.Empty
        };
        return Build(content, Projection(evaluation), diagnostics, subject, rootPose, id, destroyed, worldDesign,
            terrainLocation, wearSeed, damage);
    }

    internal static RenderedDesign Build(IContentView content, DesignSceneProjection projection,
        ImmutableArray<ContentDiagnostic> contentDiagnostics, WorldObjectId? subject, ScenePose rootPose,
        Func<string, SceneObjectId> id, ImmutableHashSet<PartId>? destroyed = null, DesignHash? worldDesign = null,
        WorldLocation? terrainLocation = null, ulong wearSeed = 0, StructureDamage.Report? damage = null)
    {
        SceneGeometry.Validate(rootPose);
        if (subject is WorldObjectId.Instance instance) wearSeed = InstanceWearSeed(instance.Id);
        if (projection.Voxels.IsDefault || projection.NestedObjects.IsDefault || projection.Elements.IsDefault)
            throw new ArgumentException("Design collections must be initialized.", nameof(projection));
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var diagnostics = ImmutableArray.CreateBuilder<SceneProblem>();
        AddDiagnostics(contentDiagnostics, diagnostics, subject);
        var paths = new Dictionary<string, (SceneObjectId Id, ScenePose RootPose)>();
        var appearance = new Dictionary<VoxelDefinitionId, VoxelAppearanceProjection?>();
        var voxelGroups = projection.Voxels.GroupBy(voxel => Key(voxel.ObjectPath))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var nestedObjects = projection.NestedObjects.ToDictionary(item => Key(item.NestedItemIndices));
        AddObject([], rootPose, null, rootPose);
        foreach (var nested in projection.NestedObjects.OrderBy(item => item.NestedItemIndices.Length))
        {
            if (nested.NestedItemIndices.IsDefaultOrEmpty)
                throw new ArgumentException("Nested paths must be nonempty.", nameof(projection));
            if (nested.Details is NestedObjectDetails.FunctionalComposite functional && destroyed?.Contains(functional.Number) == true)
                continue;
            if (nested.Details is NestedObjectDetails.MissingFunctionalComposite missing && destroyed?.Contains(missing.Number) == true)
                continue;
            var parentKey = Key(nested.NestedItemIndices.RemoveAt(nested.NestedItemIndices.Length - 1));
            if (!paths.TryGetValue(parentKey, out var parent)) continue;
            var pose = SceneGeometry.Placement(nested.At, nested.Orientation);
            AddObject(nested.NestedItemIndices, pose, parent.Id, SceneGeometry.Compose(parent.RootPose, pose));
        }
        return new(objects.ToImmutable(), picking.ToImmutable(), diagnostics.ToImmutable());

        void AddObject(ImmutableArray<int> path, ScenePose pose, SceneObjectId? parent, ScenePose composed)
        {
            var key = Key(path);
            var objectId = id(key);
            paths.Add(key, (objectId, composed));
            var cells = ImmutableArray.CreateBuilder<SceneCell>();
            var terrainSubject = terrainLocation is null ? null : new PickSubject.Terrain(terrainLocation);
            var cellDamage = new Dictionary<GridPosition, float>();
            foreach (var voxel in voxelGroups.GetValueOrDefault(key) ?? [])
            {
                if (destroyed?.Contains(voxel.Number) == true) continue;
                if (damage is not null && damage.Removed.Contains(new(key, voxel.LocalAt))) continue;
                if (damage is not null && damage.Damage.TryGetValue(new(key, voxel.LocalAt), out var amount))
                    cellDamage[voxel.LocalAt] = amount;
                if (!Enum.IsDefined(voxel.LocalOrientation)) throw new ArgumentException("Invalid voxel orientation.", nameof(projection));
                if (!appearance.TryGetValue(voxel.Voxel, out var value))
                {
                    var lookup = content.FindVoxelAppearance(voxel.Voxel);
                    value = lookup is ContentLookup<VoxelAppearanceProjection>.Found found ? found.Value : null;
                    if (lookup is ContentLookup<VoxelAppearanceProjection>.Missing missing)
                        AddDiagnostics(missing.Diagnostics, diagnostics, subject);
                    appearance.Add(voxel.Voxel, value);
                }
                var reference = voxel.Missing || value is null
                    ? new ContentReference.Core(new CoreDefinitionReference.Voxel(voxel.Voxel)) : null;
                PickSubject? pick = terrainSubject is not null ? terrainSubject :
                    subject is null ? null : new PickSubject.WorldObject(subject, worldDesign ?? projection.Design, voxel.Number);
                cells.Add(new(voxel.LocalAt, new(voxel.LocalOrientation, reference is null ? value : null,
                    voxel.Shape, voxel.Paint, pick, path, reference)));
                if (reference is not null)
                {
                    objects.Add(new(id(key + ":missing:" + voxel.LocalAt), objectId,
                        SceneGeometry.Placement(voxel.LocalAt, voxel.LocalOrientation), subject,
                        [new SceneVisual.Missing(reference, true)], [], []));
                    AddMissing(reference, voxel.Element);
                }
            }
            var grid = new VoxelGrid(cells.ToImmutable());
            var nested = nestedObjects.GetValueOrDefault(key);
            ImmutableArray<SceneVisual> visuals;
            if (nested?.Details is NestedObjectDetails.MissingFunctionalComposite missingComposite)
            {
                var reference = new ContentReference.Core(new CoreDefinitionReference.Composite(missingComposite.Definition));
                visuals = [new SceneVisual.Missing(reference, false)];
                AddMissing(reference, new DesignElementPath.FunctionalComposite(path.RemoveAt(path.Length - 1), path[^1]));
            }
            else
            {
                var surface = new SurfaceState(true, wearSeed, damage?.SurfaceDamage ?? 0, damage?.Scorched ?? false);
                visuals = Meshes(grid, true, wearSeed, cellDamage, surface);
                objects.AddRange(Models(grid, objectId, subject, cell => id(key + ":model:" + cell.At), true, wearSeed, surface));
            }
            var effects = damage is null ? [] : damage.Effects.Where(item => item.Path == key).Select(item => item.Effect).ToImmutableArray();
            objects.Add(new(objectId, parent, pose, subject, visuals, [], effects));
            if (damage is null)
                objects.AddRange(Lights(grid, objectId, subject, cell => id(key + ":light:" + cell.At)));
            else
                foreach (var light in damage.Lights.Where(item => item.Path == key))
                    objects.Add(new(id(key + ":light:" + light.At), objectId,
                        new(new(light.At.X + .5f, light.At.Y + .5f, light.At.Z + .5f), Quaternion.Identity),
                        subject, [], [light.Light], []));
            if (cells.Count > 0 && (subject is not null || terrainLocation is not null)) picking.Add(new(objectId, composed, grid));
        }

        void AddMissing(ContentReference reference, DesignElementPath element)
        {
            var package = diagnostics.FirstOrDefault(problem => problem.Content == reference && problem.Package is not null)?.Package
                ?? projection.EditLocks.FirstOrDefault(item => item.Reference == reference)?.Package;
            diagnostics.Add(new("content_missing", SceneStage.Build, subject, reference, package, element,
                "Missing presentation content."));
        }
    }

    internal static string Key(ImmutableArray<int> path) => string.Join('/', path);

    internal static void WriteInstance(Span<byte> identity, InstanceId instance)
    {
        var (kind, value) = instance switch
        {
            InstanceId.Vehicle vehicle => ((byte)1, vehicle.Id.Value),
            InstanceId.Building building => ((byte)2, building.Id.Value),
            _ => throw new ArgumentException("Unknown instance kind.", nameof(instance))
        };
        identity[0] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(identity[1..], value);
    }

    private static ulong InstanceWearSeed(InstanceId instance)
    {
        Span<byte> identity = stackalloc byte[9];
        WriteInstance(identity, instance);
        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(identity));
    }

    internal static ulong PlacementWearSeed(EdgeId edge, ScenePoint position)
    {
        Span<byte> identity = stackalloc byte[33];
        identity[0] = 3;
        BinaryPrimitives.WriteUInt64LittleEndian(identity[1..], edge.Value);
        BinaryPrimitives.WriteDoubleLittleEndian(identity[9..], position.X);
        BinaryPrimitives.WriteDoubleLittleEndian(identity[17..], position.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(identity[25..], position.Z);
        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(identity));
    }

    internal static IEnumerable<SceneObject> Lights(VoxelGrid grid, SceneObjectId parent, WorldObjectId? subject,
        Func<SceneCell, SceneObjectId> id)
    {
        foreach (var cell in grid.Cells)
            if (cell.MissingReference is null && cell.Appearance?.Behaviors.Light is { } light)
                yield return new(id(cell), parent,
                    new(new(cell.At.X + .5f, cell.At.Y + .5f, cell.At.Z + .5f), Quaternion.Identity),
                    subject, [], [new(light.Color, light.RangeCells, 0, 0)], []);
    }

    internal static IEnumerable<SceneObject> Models(VoxelGrid grid, SceneObjectId parent, WorldObjectId? subject,
        Func<SceneCell, SceneObjectId> id, bool wear, ulong wearSeed = 0, SurfaceState? surface = null)
    {
        foreach (var cell in grid.Cells)
        {
            if (cell.Appearance?.Presentation is not VoxelPresentation.Mesh mesh) continue;
            var placement = SceneGeometry.Placement(cell.At, cell.Orientation);
            var pose = placement with
            {
                Position = placement.Position + Vector3.Transform(new Vector3(.5f, 0, .5f), placement.Rotation)
            };
            var bounds = grid.Bounds!;
            var height = bounds.Maximum.Y - bounds.Minimum.Y;
            var up = new Vector3(Vector3.Transform(Vector3.UnitX, pose.Rotation).Y,
                Vector3.Transform(Vector3.UnitY, pose.Rotation).Y, Vector3.Transform(Vector3.UnitZ, pose.Rotation).Y);
            var heightPlane = new Vector4(up / height, (pose.Position.Y - bounds.Minimum.Y) / height);
            yield return new(id(cell), parent, pose, subject,
                [new SceneVisual.Model(mesh.Model, mesh.SurfaceMaterials, cell.Paint,
                    surface ?? new(wear, wearSeed, 0, false), heightPlane)], [], []);
        }
    }

    internal static void AddDiagnostics(ImmutableArray<ContentDiagnostic> source,
        ImmutableArray<SceneProblem>.Builder target, WorldObjectId? subject)
    {
        if (source.IsDefault) throw new ArgumentException("Diagnostics must be initialized.", nameof(source));
        foreach (var problem in source)
            target.Add(new(problem.Code, SceneStage.Build, subject, problem.Subject,
                problem.Source?.Package, problem.Element, problem.Message));
    }

    internal static ImmutableArray<SceneVisual> Meshes(VoxelGrid grid, bool wear, ulong wearSeed = 0,
        IReadOnlyDictionary<GridPosition, float>? cellDamage = null, SurfaceState? surface = null)
    {
        var shapes = new VoxelShapes(grid);
        var groups = new Dictionary<(GridPosition Chunk, SceneMaterial Material),
            (List<FaceQuad> Quads, Dictionary<FacePlane, uint[]> Planes)>();
        foreach (var cell in grid.Cells)
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
        var visuals = ImmutableArray.CreateBuilder<SceneVisual>(groups.Count + shapes.GroupCount);
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
        visuals.AddRange(shapes.Build(surface));
        return visuals.DrainToImmutable();

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

    internal static float TextureCoordinate(GridPosition at, Vector3 corner, Vector3 axis, int span)
    {
        var coordinate = axis.X != 0 ? at.X : axis.Y != 0 ? at.Y : at.Z;
        var positive = axis.X + axis.Y + axis.Z > 0;
        var minimum = positive ? (long)coordinate : -(long)coordinate - 1;
        var offset = Vector3.Dot(corner, axis) + (positive ? 0 : 1);
        // Reduce integer coordinates before converting to float, so distant cells retain their texture phase.
        return (((minimum % span + span) % span) + offset) / span;
    }
}
