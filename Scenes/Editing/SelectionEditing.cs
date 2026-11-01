using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Editing;

internal sealed partial class DesignEditSession
{
    private SceneResult<IEditPreview> PreviewSelection(EditOperation operation)
    {
        if (operation is EditOperation.RotateSelection rotation)
        {
            if (!Enum.IsDefined(rotation.Axis)) throw new ArgumentException("Invalid rotation axis.", nameof(operation));
            if (rotation.QuarterTurns is < 1 or > 3)
                return Fail<IEditPreview>("invalid_operation", "Selection rotation requires one to three quarter turns.");
        }
        if (operation is EditOperation.ShapeSelection shapeSelection) ArgumentNullException.ThrowIfNull(shapeSelection.Shape.Name);
        var path = EnteredPath;
        var current = EditLayout.At(root, path);
        var ids = selection.ToHashSet();
        var selected = current with
        {
            Cells = current.Cells.Where(cell => ids.Contains(cell.Id)).ToImmutableArray(),
            Children = current.Children.Where(child => ids.Contains(child.Id)).ToImmutableArray()
        };
        var action = operation switch
        {
            EditOperation.MoveSelection => LockedDesignOperation.Move,
            EditOperation.CopySelection => LockedDesignOperation.Copy,
            EditOperation.RotateSelection => LockedDesignOperation.Rotate,
            EditOperation.PaintSelection => LockedDesignOperation.Paint,
            _ => LockedDesignOperation.ChangeShape
        };
        var lockProblems = LockProblems(Elements);
        var conflicts = ImmutableArray.CreateBuilder<EditConflict>();
        foreach (var id in selection)
            if (lockProblems.GetValueOrDefault((id, action)) is { } problem) conflicts.Add(new(null, id, problem));
        var affected = new HashSet<GridPosition>();
        var highlight = new HashSet<GridPosition>();
        var copying = operation is EditOperation.CopySelection;
        CellEdit? appearance = operation switch
        {
            EditOperation.PaintSelection paint => new CellEdit.Paint(paint.Color),
            EditOperation.ShapeSelection shape => new CellEdit.Shape(shape.Shape),
            _ => null
        };
        EditObject target;
        if (appearance is not null)
        {
            target = ChangeAppearance(selected, appearance, at => at, affected);
        }
        else
        {
            var palette = new EditPalette(current.Palette);
            target = selected with
            {
                Cells = selected.Cells.Select(cell =>
                {
                    var value = current.Palette[cell.PaletteIndex];
                    var at = Transform(cell.At);
                    var nextValue = value;
                    if (operation is EditOperation.RotateSelection rotate &&
                        content.Appearance(value.Voxel) is ContentLookup<VoxelAppearanceProjection>.Found found)
                    {
                        nextValue = value with { Orientation = found.Value.AllowedOrientations == AllowedVoxelOrientations.None
                            ? VoxelOrientation.PxPy : EditLayout.Rotate(value.Orientation, rotate.Axis, rotate.QuarterTurns) };
                        if (!AllowsOrientation(found.Value, nextValue.Orientation))
                            conflicts.Add(new(at, cell.Id, Problem("invalid_operation", "The voxel does not support the rotated orientation.")));
                    }
                    if (copying || at != cell.At || value != nextValue)
                    {
                        if (!copying) affected.Add(cell.At);
                        affected.Add(at);
                    }
                    return new EditCell(copying ? NextElement() : cell.Id, at,
                        nextValue == value ? cell.PaletteIndex : palette.Index(nextValue));
                }).ToImmutableArray(),
                Children = selected.Children.Select(child =>
                {
                    var (at, orientation) = EditLayout.Placement(child.Item);
                    var nextAt = Transform(at);
                    var nextOrientation = operation is EditOperation.RotateSelection rotate
                        ? EditLayout.Rotate(orientation, rotate.Axis, rotate.QuarterTurns) : orientation;
                    var transformed = child with
                    {
                        Id = copying ? NextElement() : child.Id,
                        Item = EditLayout.Place(child.Item, nextAt, nextOrientation),
                        Copy = copying && child.Copy is { } copy ? CopyObject(copy) : child.Copy
                    };
                    if (copying || at != nextAt || orientation != nextOrientation)
                    {
                        if (!copying) affected.UnionWith(ChildOccupied(child).Cells.Select(cell => cell.At));
                        affected.UnionWith(ChildOccupied(transformed).Cells.Select(cell => cell.At));
                    }
                    return transformed;
                }).ToImmutableArray()
            };
            target = target with { Palette = palette.Values };
            if (operation is EditOperation.RotateSelection rotateSelection)
                foreach (var cell in Occupied(selected).Cells)
                    for (var turn = 1; turn <= rotateSelection.QuarterTurns; turn++)
                        highlight.Add(EditLayout.Rotate(cell.At, rotateSelection.Axis, rotateSelection.Pivot, turn));
        }
        var covered = Occupied(target).Cells.Select(cell => cell.At).ToHashSet();
        if (operation is not EditOperation.RotateSelection) highlight.UnionWith(covered);
        var cellsById = target.Cells.ToDictionary(cell => cell.Id);
        var childrenById = target.Children.ToDictionary(child => child.Id);
        var next = current with
        {
            Palette = target.Palette,
            Cells = copying ? current.Cells.AddRange(target.Cells) : current.Cells.Select(cell => cellsById.GetValueOrDefault(cell.Id, cell)).ToImmutableArray(),
            Children = copying ? current.Children.AddRange(target.Children) : current.Children.Select(child => childrenById.GetValueOrDefault(child.Id, child)).ToImmutableArray()
        };
        if (operation is EditOperation.MoveSelection or EditOperation.CopySelection or EditOperation.RotateSelection)
        {
            var previousAnchors = current.Children.ToDictionary(child => child.Id, child => EditLayout.Placement(child.Item));
            var changedAnchors = target.Children.Where(child => copying ||
                EditLayout.Placement(child.Item) != previousAnchors[child.Id])
                .Select(child => child.Id).ToHashSet();
            foreach (var group in next.Children.GroupBy(child => EditLayout.Placement(child.Item).At))
                if (group.Count() > 1 && group.Any(child => changedAnchors.Contains(child.Id)))
                    conflicts.Add(new(group.Key, group.First().Id, Problem("occupancy_conflict", "Nested anchors overlap.")));
        }
        return FinishPreview(new(Stamp, EditLayout.Update(root, path, _ => next), covered.Count, affected.Count,
            covered.Count(at => !affected.Contains(at)), conflicts.ToImmutable(), Ordered(highlight)), path);

        GridPosition Transform(GridPosition at) => operation switch
        {
            EditOperation.MoveSelection move => EditLayout.Offset(at, move.Offset),
            EditOperation.CopySelection copy => EditLayout.Offset(at, copy.Offset),
            EditOperation.RotateSelection rotate => EditLayout.Rotate(at, rotate.Axis, rotate.Pivot, rotate.QuarterTurns),
            _ => throw new InvalidOperationException()
        };
    }

    private EditObject CopyObject(EditObject value) => value with
    {
        Cells = value.Cells.Select(cell => cell with { Id = NextElement() }).ToImmutableArray(),
        Children = value.Children.Select(child => child with
        { Id = NextElement(), Copy = child.Copy is { } copy ? CopyObject(copy) : null }).ToImmutableArray()
    };

    private VoxelPaletteEntry? AppearanceEdit(VoxelPaletteEntry value, CellEdit action) => action switch
    {
        CellEdit.Paint paint => value with { Paint = paint.Color },
        CellEdit.Shape shape when content.Appearance(value.Voxel) is ContentLookup<VoxelAppearanceProjection>.Found
            { Value.Presentation: VoxelPresentation.Cube cube } && cube.Shapes.Contains(shape.Value) => value with { Shape = shape.Value },
        _ => null
    };

    private EditObject ChangeAppearance(EditObject value, CellEdit action, Func<GridPosition, GridPosition> map,
        HashSet<GridPosition> affected)
    {
        var palette = new EditPalette(value.Palette);
        var cells = value.Cells.Select(cell =>
        {
            var original = value.Palette[cell.PaletteIndex];
            var replacement = AppearanceEdit(original, action);
            if (replacement is null || replacement == original) return cell;
            affected.Add(map(cell.At));
            return cell with { PaletteIndex = palette.Index(replacement) };
        }).ToImmutableArray();
        var children = value.Children.Select(child =>
        {
            if (child.Copy is { } copy)
                return child with { Copy = ChangeAppearance(copy, action, at => map(EditLayout.Map(at, child.Item)), affected) };
            if (action is not CellEdit.Paint paint || child.Item is not NestedDesignItem.FunctionalCompositeReference reference ||
                reference.Paint == paint.Color) return child;
            affected.UnionWith(ChildOccupied(child).Cells.Select(cell => map(cell.At)));
            return child with { Item = reference with { Paint = paint.Color } };
        }).ToImmutableArray();
        return value with { Palette = palette.Values, Cells = cells, Children = children };
    }
}
