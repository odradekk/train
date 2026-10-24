using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Editing;

internal sealed class EditGeometry
{
    private sealed record GridGeometry(VoxelGrid Grid, ImmutableArray<SceneCell> Cells,
        Dictionary<GridPosition, DesignGeometry.MeshGroups> Chunks);
    private Dictionary<EditElementId, GridGeometry> grids = [];

    internal RenderedDesign Build(EditObject root, EditContent content, EditSessionId session,
        EditRevision revision, int? highestY, Func<EditElementId, SceneObjectId> objectId,
        Func<SceneObjectId, GridPosition, SceneObjectId> modelId,
        ImmutableArray<int> rootPath = default, EditElementId rootElement = default)
    {
        Dictionary<EditElementId, GridGeometry> nextGrids = [];
        var objects = ImmutableArray.CreateBuilder<SceneObject>();
        var picking = ImmutableArray.CreateBuilder<PickGrid>();
        var problems = ImmutableArray.CreateBuilder<SceneProblem>();
        var identity = new ScenePose(Vector3.Zero, Quaternion.Identity);
        Add(root, default, null, identity, identity, rootPath.IsDefault ? [] : rootPath);
        grids = nextGrids;
        return new(objects.ToImmutable(), picking.ToImmutable(), problems.ToImmutable());

        void Add(EditObject value, EditElementId element, SceneObjectId? parent, ScenePose pose,
            ScenePose composed, ImmutableArray<int> path)
        {
            var id = objectId(element);
            var cells = ImmutableArray.CreateBuilder<SceneCell>();
            var models = new List<SceneCell>();
            var ids = new Dictionary<GridPosition, EditElementId>();
            var palette = new (CellProperties Properties, ImmutableArray<SceneProblem> Problems)?[value.Palette.Length];
            for (var i = 0; i < value.Cells.Length; i++)
            {
                var cell = value.Cells[i];
                if (!ids.TryAdd(cell.At, cell.Id))
                {
                    problems.Add(new("occupancy_conflict", SceneStage.Edit, null, null, null, new DesignElementPath.Cell(path, i),
                        "Multiple cells occupy the same position."));
                    continue;
                }
                if (!Visible(cell.At, composed)) continue;
                var entry = palette[cell.PaletteIndex];
                if (entry is null) palette[cell.PaletteIndex] = entry = Properties(value.Palette[cell.PaletteIndex], path);
                var (properties, diagnostics) = entry.Value;
                if (!diagnostics.IsEmpty)
                {
                    var cellPath = new DesignElementPath.Cell(path, i);
                    problems.AddRange(diagnostics.Select(problem => problem with { Element = cellPath }));
                }
                var sceneCell = new SceneCell(cell.At, properties);
                cells.Add(sceneCell);
                if (properties.Appearance?.Presentation is VoxelPresentation.Mesh) models.Add(sceneCell);
                if (properties.Appearance is null)
                    objects.Add(new(objectId(cell.Id), id, SceneGeometry.Placement(cell.At, properties.Orientation), null,
                        [new SceneVisual.Missing(properties.MissingReference!, true)], [], []));
            }
            AddGrid(parent is null ? rootElement : element, id, parent, pose, composed, cells, models,
                at => new PickSubject.Editing(session, revision, ids[at]));
            for (var i = 0; i < value.Children.Length; i++)
            {
                var child = value.Children[i];
                var (at, orientation) = EditLayout.Placement(child.Item);
                var childPose = SceneGeometry.Placement(at, orientation);
                var childComposed = SceneGeometry.Compose(composed, childPose);
                var childPath = path.Add(i);
                if (child.Copy is { } copy)
                {
                    Add(copy, child.Id, id, childPose, childComposed, childPath);
                    continue;
                }
                var composite = (NestedDesignItem.FunctionalCompositeReference)child.Item;
                var cached = content.Composite(composite.Definition);
                var childId = objectId(child.Id);
                var elementPath = new DesignElementPath.FunctionalComposite(path, i);
                problems.AddRange(cached.Problems.Select(problem => problem with { Element = elementPath }));
                if (!cached.Complete)
                {
                    var reference = new ContentReference.Core(new CoreDefinitionReference.Composite(composite.Definition));
                    objects.Add(new(childId, id, childPose, null,
                        Visible(default, childComposed) ? [new SceneVisual.Missing(reference, false)] : [], [], []));
                    if (cached.Problems.IsEmpty) problems.Add(EditContent.Problem(reference) with { Element = elementPath });
                    continue;
                }
                var compositeCells = ImmutableArray.CreateBuilder<SceneCell>();
                var compositeModels = new List<SceneCell>();
                var compositePalette = new Dictionary<VoxelPaletteEntry,
                    (CellProperties Properties, ImmutableArray<SceneProblem> Problems)>();
                foreach (var voxel in cached.Voxels)
                {
                    if (!Visible(voxel.LocalAt, childComposed)) continue;
                    var key = new VoxelPaletteEntry(voxel.Voxel, voxel.LocalOrientation, voxel.Shape, composite.Paint ?? voxel.Paint);
                    if (!compositePalette.TryGetValue(key, out var entry))
                        compositePalette.Add(key, entry = Properties(key, childPath));
                    problems.AddRange(entry.Problems);
                    var sceneCell = new SceneCell(voxel.LocalAt, entry.Properties);
                    compositeCells.Add(sceneCell);
                    if (entry.Properties.Appearance?.Presentation is VoxelPresentation.Mesh) compositeModels.Add(sceneCell);
                }
                AddGrid(child.Id, childId, id, childPose, childComposed, compositeCells, compositeModels,
                    _ => new PickSubject.Editing(session, revision, child.Id));
            }
        }

        void AddGrid(EditElementId element, SceneObjectId id, SceneObjectId? parent, ScenePose pose, ScenePose composed,
            ImmutableArray<SceneCell>.Builder cells, List<SceneCell> models, Func<GridPosition, PickSubject> resolveSubject)
        {
            var values = cells.ToImmutable();
            var grid = new VoxelGrid(values);
            grids.TryGetValue(element, out var previous);
            var sameHeight = previous is not null && previous.Grid.Bounds?.Minimum.Y == grid.Bounds?.Minimum.Y &&
                previous.Grid.Bounds?.Maximum.Y == grid.Bounds?.Maximum.Y;
            HashSet<GridPosition> changed = [];
            if (sameHeight)
            {
                foreach (var cell in values)
                    if (previous!.Grid.Find(cell.At) is not { } old || !SameAppearance(old, cell)) Change(cell.At);
                foreach (var cell in previous!.Cells)
                    if (!grid.Contains(cell.At)) Change(cell.At);
            }
            Dictionary<GridPosition, DesignGeometry.MeshGroups> chunks = [];
            foreach (var chunk in grid.Chunks)
                chunks.Add(chunk, sameHeight && !changed.Contains(chunk) && previous!.Chunks.TryGetValue(chunk, out var cached)
                    ? cached : DesignGeometry.MeshChunk(grid, chunk));
            nextGrids.Add(element, new(grid, values, chunks));
            // 完整生成先交出所有立方体，再交出其他形状；增量拼接保持同一次序。
            var visuals = chunks.Values.SelectMany(chunk => chunk.Cubes)
                .Concat(chunks.Values.SelectMany(chunk => chunk.Shapes)).ToImmutableArray();
            objects.Add(new(id, parent, pose, null, visuals, [], []));
            objects.AddRange(DesignGeometry.Models(grid, id, null, cell => modelId(id, cell.At), true, cells: models));
            if (cells.Count > 0) picking.Add(new(id, composed, grid, resolveSubject));

            void Change(GridPosition at)
            {
                changed.Add(VoxelGrid.Chunk(at));
                for (var axis = 0; axis < 3; axis++)
                {
                    var x = axis == 0 ? 1 : 0;
                    var y = axis == 1 ? 1 : 0;
                    var z = axis == 2 ? 1 : 0;
                    changed.Add(VoxelGrid.Chunk(new(at.X + x, at.Y + y, at.Z + z)));
                    changed.Add(VoxelGrid.Chunk(new(at.X - x, at.Y - y, at.Z - z)));
                }
            }
        }

        bool Visible(GridPosition at, ScenePose pose) => highestY is null ||
            Math.Floor((pose.Position + Vector3.Transform(new Vector3(at.X + .5f, at.Y + .5f, at.Z + .5f), pose.Rotation)).Y) <= highestY;

        (CellProperties Properties, ImmutableArray<SceneProblem> Problems) Properties(VoxelPaletteEntry entry, ImmutableArray<int> path)
        {
            var lookup = content.Appearance(entry.Voxel);
            var appearance = lookup is ContentLookup<VoxelAppearanceProjection>.Found found ? found.Value : null;
            var reference = new ContentReference.Core(new CoreDefinitionReference.Voxel(entry.Voxel));
            return (new(entry.Orientation, appearance, entry.Shape, entry.Paint, null, path, appearance is null ? reference : null),
                EditContent.MissingProblems(lookup, reference));
        }
    }

    // 拾取修订和元素路径会变化，但不参与网格生成。
    private static bool SameAppearance(SceneCell left, SceneCell right) =>
        ReferenceEquals(left.Properties, right.Properties) ||
        left.Appearance == right.Appearance && left.Orientation == right.Orientation && left.Shape == right.Shape &&
        left.Paint == right.Paint && left.MissingReference == right.MissingReference;

    internal static ImmutableArray<SceneDebugLine> SweepLines(DesignEvaluation? evaluation,
        ImmutableArray<ImmutableArray<int>> selectedPaths, ScenePose enteredPose)
    {
        if (evaluation is null or DesignEvaluation.Failed) return [];
        var lines = ImmutableArray.CreateBuilder<SceneDebugLine>();
        var inverse = Quaternion.Conjugate(enteredPose.Rotation);
        foreach (var sweep in DesignGeometry.Projection(evaluation).RotationSweeps)
        {
            if (!selectedPaths.Any(path => EditLayout.SamePath(path, sweep.NestedItemIndices))) continue;
            Add(sweep.SweptCells, new(1, .75f, 0, 1));
            Add(sweep.ConflictCells, new(1, .15f, .15f, 1));
        }
        return lines.ToImmutable();

        void Add(ComputedValue<ImmutableArray<GridPosition>> cells, Vector4 color)
        {
            if (cells is not ComputedValue<ImmutableArray<GridPosition>>.Known known) return;
            foreach (var cell in known.Value)
            for (var corner = 0; corner < 8; corner++)
            for (var axis = 0; axis < 3; axis++)
            {
                if ((corner & (1 << axis)) != 0) continue;
                lines.Add(new(Point(corner), Point(corner | (1 << axis)), color));
                Vector3 Point(int index) => Vector3.Transform(new Vector3(cell.X + (index & 1),
                    cell.Y + ((index >> 1) & 1), cell.Z + ((index >> 2) & 1)) - enteredPose.Position, inverse);
            }
        }
    }
}
