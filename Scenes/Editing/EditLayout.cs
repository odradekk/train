using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Editing;

internal sealed record EditCell(EditElementId Id, GridPosition At, int PaletteIndex);
internal sealed record EditChild(EditElementId Id, NestedDesignItem Item, EditObject? Copy);
internal sealed record EditObject(DisplayText Display, DesignSpecification Specification,
    ImmutableArray<VoxelPaletteEntry> Palette, ImmutableArray<EditCell> Cells,
    ImmutableArray<EditChild> Children)
{
    internal DesignDraft Export() => new(Display, Specification, new(Palette,
        Cells.Select(cell => new LayoutCell(cell.At, cell.PaletteIndex)).ToImmutableArray(),
        Children.Select(child => child.Item switch
        {
            NestedDesignItem.AssemblyCopy assembly => assembly with { Design = child.Copy!.Export() },
            NestedDesignItem.SceneModelCopy model => model with { Design = child.Copy!.Export() },
            _ => child.Item
        }).ToImmutableArray()));
}

internal sealed class EditPalette
{
    private readonly ImmutableArray<VoxelPaletteEntry>.Builder entries;
    private readonly Dictionary<VoxelPaletteEntry, int> indices = [];

    internal EditPalette(ImmutableArray<VoxelPaletteEntry> values)
    {
        entries = values.ToBuilder();
        for (var i = 0; i < values.Length; i++) indices.TryAdd(values[i], i);
    }

    internal int Index(VoxelPaletteEntry value)
    {
        if (indices.TryGetValue(value, out var index)) return index;
        index = entries.Count;
        entries.Add(value);
        indices.Add(value, index);
        return index;
    }

    internal ImmutableArray<VoxelPaletteEntry> Values => entries.ToImmutable();
}

internal static class EditLayout
{
    private static readonly VoxelOrientation[] orientations = Enum.GetValues<VoxelOrientation>();
    internal static EditObject Import(DesignDraft draft, Func<EditElementId> next)
    {
        Validate(draft);
        return new(draft.Display, draft.Specification, draft.Layout.Palette,
            draft.Layout.Cells.Select(cell => new EditCell(next(), cell.At, cell.PaletteIndex)).ToImmutableArray(),
            draft.Layout.NestedItems.Select(item => new EditChild(next(), item,
                Copy(item) is { } copy ? Import(copy, next) : null)).ToImmutableArray());
    }

    internal static DesignDraft? Copy(NestedDesignItem item) => item switch
    {
        NestedDesignItem.AssemblyCopy assembly => assembly.Design,
        NestedDesignItem.SceneModelCopy model => model.Design,
        _ => null
    };

    internal static bool ValidCopies(DesignDraft draft) => draft.Layout.NestedItems.All(item => item switch
    {
        NestedDesignItem.AssemblyCopy assembly => assembly.Design.Specification is DesignSpecification.Assembly && ValidCopies(assembly.Design),
        NestedDesignItem.SceneModelCopy model => model.Design.Specification is DesignSpecification.SceneModel && ValidCopies(model.Design),
        NestedDesignItem.FunctionalCompositeReference => true,
        _ => false
    });

    internal static bool CanEnter(EditObject value) => value.Specification is DesignSpecification.Assembly or DesignSpecification.SceneModel;

    internal static (GridPosition At, VoxelOrientation Orientation) Placement(NestedDesignItem item) => item switch
    {
        NestedDesignItem.AssemblyCopy assembly => (assembly.At, assembly.Orientation),
        NestedDesignItem.SceneModelCopy model => (model.At, model.Orientation),
        NestedDesignItem.FunctionalCompositeReference composite => (composite.At, composite.Orientation),
        _ => throw new ArgumentException("Invalid nested item.", nameof(item))
    };

    internal static NestedDesignItem Place(NestedDesignItem item, GridPosition at, VoxelOrientation orientation) => item switch
    {
        NestedDesignItem.AssemblyCopy copy => copy with { At = at, Orientation = orientation },
        NestedDesignItem.SceneModelCopy copy => copy with { At = at, Orientation = orientation },
        NestedDesignItem.FunctionalCompositeReference reference => reference with { At = at, Orientation = orientation },
        _ => throw new ArgumentException("Invalid nested item.", nameof(item))
    };

    internal static GridPosition Offset(GridPosition at, GridPosition offset) =>
        new(checked(at.X + offset.X), checked(at.Y + offset.Y), checked(at.Z + offset.Z));

    internal static GridPosition Rotate(GridPosition at, GridAxis axis, GridPosition pivot, int turns)
    {
        var x = (long)at.X - pivot.X;
        var y = (long)at.Y - pivot.Y;
        var z = (long)at.Z - pivot.Z;
        for (var i = 0; i < turns; i++)
            (x, y, z) = axis switch
            {
                GridAxis.X => (x, -z, y),
                GridAxis.Y => (z, y, -x),
                _ => (-y, x, z)
            };
        return new(checked((int)(pivot.X + x)), checked((int)(pivot.Y + y)), checked((int)(pivot.Z + z)));
    }

    internal static VoxelOrientation Rotate(VoxelOrientation orientation, GridAxis axis, int turns)
    {
        var (right, up, _) = SceneGeometry.Axes(orientation);
        var r = Rotate(new((int)right.X, (int)right.Y, (int)right.Z), axis, default, turns);
        var u = Rotate(new((int)up.X, (int)up.Y, (int)up.Z), axis, default, turns);
        return orientations.First(value =>
        {
            var axes = SceneGeometry.Axes(value);
            return axes.Right.X == r.X && axes.Right.Y == r.Y && axes.Right.Z == r.Z &&
                axes.Up.X == u.X && axes.Up.Y == u.Y && axes.Up.Z == u.Z;
        });
    }

    internal static GridPosition Map(GridPosition cell, NestedDesignItem item)
    {
        var (at, orientation) = Placement(item);
        var (right, up, forward) = SceneGeometry.Axes(orientation);
        return new(checked((int)(at.X + (long)right.X * cell.X + (long)up.X * cell.Y + (long)forward.X * cell.Z)),
            checked((int)(at.Y + (long)right.Y * cell.X + (long)up.Y * cell.Y + (long)forward.Y * cell.Z)),
            checked((int)(at.Z + (long)right.Z * cell.X + (long)up.Z * cell.Y + (long)forward.Z * cell.Z)));
    }

    internal static GridPosition ToLocal(EditObject root, ImmutableArray<int> path, GridPosition cell)
    {
        foreach (var index in path)
        {
            var child = root.Children[index];
            var (at, orientation) = Placement(child.Item);
            var (right, up, forward) = SceneGeometry.Axes(orientation);
            var x = (long)cell.X - at.X; var y = (long)cell.Y - at.Y; var z = (long)cell.Z - at.Z;
            cell = new(checked((int)(x * (int)right.X + y * (int)right.Y + z * (int)right.Z)),
                checked((int)(x * (int)up.X + y * (int)up.Y + z * (int)up.Z)),
                checked((int)(x * (int)forward.X + y * (int)forward.Y + z * (int)forward.Z)));
            root = child.Copy!;
        }
        return cell;
    }

    internal static GridPosition ToRoot(EditObject root, ImmutableArray<int> path, GridPosition cell)
    {
        return MapAt(root, 0);

        GridPosition MapAt(EditObject node, int depth)
        {
            if (depth == path.Length) return cell;
            var child = node.Children[path[depth]];
            return Map(MapAt(child.Copy!, depth + 1), child.Item);
        }
    }

    internal static DesignDraft Normalize(DesignDraft draft) => draft with
    {
        Display = new(draft.Display.Name.Trim(), draft.Display.Description.Trim()),
        Layout = draft.Layout with { NestedItems = draft.Layout.NestedItems.Select(item => item switch
        {
            NestedDesignItem.AssemblyCopy copy => copy with { Design = Normalize(copy.Design) },
            NestedDesignItem.SceneModelCopy copy => copy with { Design = Normalize(copy.Design) },
            _ => item
        }).ToImmutableArray() }
    };

    internal static EditObject At(EditObject root, ImmutableArray<int> path)
    {
        foreach (var index in path) root = root.Children[index].Copy!;
        return root;
    }

    internal static EditObject Update(EditObject root, ImmutableArray<int> path, Func<EditObject, EditObject> change) =>
        path.IsEmpty ? change(root) : root with { Children = root.Children.SetItem(path[0],
            root.Children[path[0]] with { Copy = Update(root.Children[path[0]].Copy!, path.RemoveAt(0), change) }) };

    internal static IEnumerable<EditElement> Elements(EditObject root, ImmutableArray<int> path = default,
        EditElementId? parent = null)
    {
        if (path.IsDefault) path = [];
        for (var i = 0; i < root.Cells.Length; i++)
        {
            var cell = root.Cells[i];
            yield return new(cell.Id, parent, EditElementKind.Cell, cell.At,
                new DesignElementPath.Cell(path, i), path, false);
        }
        for (var i = 0; i < root.Children.Length; i++)
        {
            var child = root.Children[i];
            var childPath = path.Add(i);
            var kind = child.Item switch
            {
                NestedDesignItem.AssemblyCopy => EditElementKind.Assembly,
                NestedDesignItem.SceneModelCopy => EditElementKind.SceneModel,
                _ => EditElementKind.FunctionalComposite
            };
            yield return new(child.Id, parent, kind, Placement(child.Item).At,
                kind == EditElementKind.FunctionalComposite ? new DesignElementPath.FunctionalComposite(path, i) : null,
                childPath, false);
            if (child.Copy is not null)
                foreach (var entry in Elements(child.Copy, childPath, child.Id)) yield return entry;
        }
    }

    internal static bool SamePath(ImmutableArray<int> a, ImmutableArray<int> b) => a.AsSpan().SequenceEqual(b.AsSpan());
    internal static bool Same(DesignElementPath? a, DesignElementPath? b) => (a, b) switch
    {
        (DesignElementPath.Cell x, DesignElementPath.Cell y) => x.CellIndex == y.CellIndex && SamePath(x.NestedItemIndices, y.NestedItemIndices),
        (DesignElementPath.FunctionalComposite x, DesignElementPath.FunctionalComposite y) => x.NestedItemIndex == y.NestedItemIndex && SamePath(x.NestedItemIndices, y.NestedItemIndices),
        (null, null) => true,
        _ => false
    };

    internal static bool Same(DesignDraft a, DesignDraft b) => a.Display == b.Display && Same(a.Specification, b.Specification) &&
        a.Layout.Palette.SequenceEqual(b.Layout.Palette) && a.Layout.Cells.SequenceEqual(b.Layout.Cells) &&
        a.Layout.NestedItems.Length == b.Layout.NestedItems.Length &&
        a.Layout.NestedItems.Zip(b.Layout.NestedItems).All(pair => Same(pair.First, pair.Second));

    internal static bool Same(DesignSpecification a, DesignSpecification b) => (a, b) switch
    {
        (DesignSpecification.Building x, DesignSpecification.Building y) => x.PlotSize == y.PlotSize &&
            x.Kind == y.Kind && x.FacilityTags.SequenceEqual(y.FacilityTags),
        (DesignSpecification.FunctionalComposite x, DesignSpecification.FunctionalComposite y) =>
            x.Rotation == y.Rotation && x.Behaviors.SequenceEqual(y.Behaviors),
        _ => a == b
    };

    internal static bool Same(NestedDesignItem a, NestedDesignItem b) => (a, b) switch
    {
        (NestedDesignItem.AssemblyCopy x, NestedDesignItem.AssemblyCopy y) => x.At == y.At &&
            x.Orientation == y.Orientation && Same(x.Design, y.Design),
        (NestedDesignItem.SceneModelCopy x, NestedDesignItem.SceneModelCopy y) => x.At == y.At &&
            x.Orientation == y.Orientation && Same(x.Design, y.Design),
        _ => a == b
    };

    internal static void Validate(DesignDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(draft.Display);
        ArgumentNullException.ThrowIfNull(draft.Specification);
        ArgumentNullException.ThrowIfNull(draft.Layout);
        if (draft.Display.Name is null || draft.Display.Description is null || draft.Layout.Palette.IsDefault ||
            draft.Layout.Cells.IsDefault || draft.Layout.NestedItems.IsDefault ||
            draft.Layout.Cells.Any(cell => cell is null || cell.PaletteIndex < 0 || cell.PaletteIndex >= draft.Layout.Palette.Length))
            throw new ArgumentException("Draft fields and collections must be initialized.", nameof(draft));
        foreach (var value in draft.Layout.Palette)
            if (value is null || !Enum.IsDefined(value.Orientation)) throw new ArgumentException("Invalid palette.", nameof(draft));
        if (draft.Specification is DesignSpecification.Building building && building.FacilityTags.IsDefault ||
            draft.Specification is DesignSpecification.FunctionalComposite composite && composite.Behaviors.IsDefault)
            throw new ArgumentException("Specification collections must be initialized.", nameof(draft));
        foreach (var item in draft.Layout.NestedItems)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!Enum.IsDefined(Placement(item).Orientation)) throw new ArgumentException("Invalid nested orientation.", nameof(draft));
            if (Copy(item) is { } copy) Validate(copy);
        }
    }
}
