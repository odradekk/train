using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed record PickGrid(SceneObjectId Object, ScenePose Pose, VoxelGrid Grid);
internal sealed record RenderedDesign(ImmutableArray<SceneObject> Objects,
    ImmutableArray<PickGrid> Picking, ImmutableArray<SceneProblem> Diagnostics);

internal static class DesignGeometry
{
    internal static DesignSceneProjection Projection(DesignEvaluation evaluation) => evaluation switch
    {
        DesignEvaluation.Complete complete => complete.Scene,
        DesignEvaluation.Partial partial => partial.Scene,
        _ => throw new ArgumentException("The evaluation has no interpretable design.", nameof(evaluation))
    };

    internal static RenderedDesign Build(IContentView content, DesignEvaluation evaluation,
        WorldObjectId? subject, ScenePose rootPose, Func<string, SceneObjectId> id,
        ImmutableHashSet<PartId>? destroyed = null, DesignHash? worldDesign = null, WorldLocation? terrainLocation = null)
    {
        SceneGeometry.Validate(rootPose);
        var projection = Projection(evaluation);
        if (projection.Voxels.IsDefault || projection.NestedObjects.IsDefault || projection.Elements.IsDefault)
            throw new ArgumentException("Design collections must be initialized.", nameof(evaluation));
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var diagnostics = ImmutableArray.CreateBuilder<SceneProblem>();
        var contentDiagnostics = evaluation switch
        {
            DesignEvaluation.Complete complete => complete.Diagnostics,
            DesignEvaluation.Partial partial => partial.Diagnostics,
            _ => ImmutableArray<ContentDiagnostic>.Empty
        };
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
                throw new ArgumentException("Nested paths must be nonempty.", nameof(evaluation));
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
            foreach (var voxel in voxelGroups.GetValueOrDefault(key) ?? [])
            {
                if (destroyed?.Contains(voxel.Number) == true) continue;
                if (!Enum.IsDefined(voxel.LocalOrientation)) throw new ArgumentException("Invalid voxel orientation.", nameof(evaluation));
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
                PickSubject? pick = terrainLocation is not null ? new PickSubject.Terrain(terrainLocation) :
                    subject is null ? null : new PickSubject.WorldObject(subject, worldDesign ?? projection.Design, voxel.Number);
                cells.Add(new(voxel.LocalAt, voxel.LocalOrientation, reference is null ? value : null, voxel.Shape, voxel.Paint, pick, path, reference));
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
            else visuals = Meshes(grid, true);
            objects.Add(new(objectId, parent, pose, subject, visuals, [], []));
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

    internal static void AddDiagnostics(ImmutableArray<ContentDiagnostic> source,
        ImmutableArray<SceneProblem>.Builder target, WorldObjectId? subject)
    {
        if (source.IsDefault) throw new ArgumentException("Diagnostics must be initialized.", nameof(source));
        foreach (var problem in source)
            target.Add(new(problem.Code, SceneStage.Build, subject, problem.Subject,
                problem.Source?.Package, problem.Element, problem.Message));
    }

    internal static ImmutableArray<SceneVisual> Meshes(VoxelGrid grid, bool wear)
    {
        var groups = new Dictionary<(GridPosition Chunk, SceneMaterial Material), (List<SceneVertex> Vertices, List<int> Indices)>();
        foreach (var cell in grid.Cells)
        {
            if (!cell.Occludes) continue;
            var cube = (VoxelPresentation.Cube)cell.Appearance!.Presentation;
            var axes = SceneGeometry.Axes(cell.Orientation);
            for (var face = GridFace.NegativeX; face <= GridFace.PositiveZ; face++)
            {
                var normal = SceneGeometry.Normal(face);
                var at = cell.At;
                if (grid.Occludes(new(at.X + (int)normal.X, at.Y + (int)normal.Y, at.Z + (int)normal.Z))) continue;
                var front = normal == axes.Forward && cube.Front != cube.Side;
                var texture = normal == axes.Up ? cube.Top : normal == -axes.Up ? cube.Bottom : front ? cube.Front : cube.Side;
                var imageUp = normal == axes.Up || normal == -axes.Up ? axes.Forward : axes.Up;
                var textureU = Vector3.Cross(imageUp, normal);
                var textureV = -imageUp;
                var span = front ? 1 : cube.TileSpan;
                var material = new SceneMaterial.Voxel(texture, cube.Material, cell.Paint);
                var key = (VoxelGrid.Chunk(at), (SceneMaterial)material);
                if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = ([], []));
                var u = face is GridFace.NegativeY or GridFace.PositiveY ? Vector3.UnitX :
                    face is GridFace.NegativeX or GridFace.PositiveX ? Vector3.UnitZ : Vector3.UnitX;
                var v = Vector3.Cross(normal, u);
                var center = new Vector3(0.5f) + normal * 0.5f;
                var first = group.Vertices.Count;
                var edges = ExposedFaceEdges.None;
                if (Empty(-u)) edges |= ExposedFaceEdges.MinimumU;
                if (Empty(u)) edges |= ExposedFaceEdges.MaximumU;
                if (Empty(-v)) edges |= ExposedFaceEdges.MinimumV;
                if (Empty(v)) edges |= ExposedFaceEdges.MaximumV;
                foreach (var uv in new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) })
                {
                    var corner = center + u * (uv.X - 0.5f) + v * (uv.Y - 0.5f);
                    var position = new Vector3(at.X, at.Y, at.Z) + corner;
                    var textureUv = new Vector2(TextureCoordinate(at, corner, textureU, span),
                        TextureCoordinate(at, corner, textureV, span));
                    var bounds = grid.Bounds!;
                    var height = (position.Y - bounds.Minimum.Y) / (bounds.Maximum.Y - bounds.Minimum.Y);
                    group.Vertices.Add(new(position, normal, textureUv, uv, edges, Math.Clamp(height, 0, 1), 0));
                }
                group.Indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
                bool Empty(Vector3 offset) => !grid.Contains(new(at.X + (int)offset.X, at.Y + (int)offset.Y, at.Z + (int)offset.Z));
            }
        }
        return groups.Select(group => (SceneVisual)new SceneVisual.Geometry(
            new(group.Key.Material, group.Value.Vertices.ToImmutableArray(), group.Value.Indices.ToImmutableArray()),
            new(wear, 0, 0, false))).ToImmutableArray();
    }

    private static float TextureCoordinate(GridPosition at, Vector3 corner, Vector3 axis, int span)
    {
        var coordinate = axis.X != 0 ? at.X : axis.Y != 0 ? at.Y : at.Z;
        var positive = axis.X + axis.Y + axis.Z > 0;
        var minimum = positive ? (long)coordinate : -(long)coordinate - 1;
        var offset = Vector3.Dot(corner, axis) + (positive ? 0 : 1);
        // Reduce integer coordinates before converting to float, so distant cells retain their texture phase.
        return (((minimum % span + span) % span) + offset) / span;
    }
}
