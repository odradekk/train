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

internal static class EditLayout
{
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

    internal static GridPosition Map(GridPosition cell, NestedDesignItem item)
    {
        var (at, orientation) = Placement(item);
        var (right, up, forward) = SceneGeometry.Axes(orientation);
        return new(checked(at.X + (int)right.X * cell.X + (int)up.X * cell.Y + (int)forward.X * cell.Z),
            checked(at.Y + (int)right.Y * cell.X + (int)up.Y * cell.Y + (int)forward.Y * cell.Z),
            checked(at.Z + (int)right.Z * cell.X + (int)up.Z * cell.Y + (int)forward.Z * cell.Z));
    }

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
        var rotation = draft.Specification switch
        {
            DesignSpecification.Assembly assembly => assembly.Rotation,
            DesignSpecification.FunctionalComposite functional => functional.Rotation,
            _ => null
        };
        if (rotation is not null && (!Enum.IsDefined(rotation.Axis) || !double.IsFinite(rotation.MinimumDegrees) ||
            !double.IsFinite(rotation.MaximumDegrees) || !double.IsFinite(rotation.SpeedDegreesPerSecond)))
            throw new ArgumentException("Rotation values must be finite and the axis valid.", nameof(draft));
        if (draft.Specification is DesignSpecification.Vehicle { Size: { } size } && !Enum.IsDefined(size) ||
            draft.Specification is DesignSpecification.Building b && (b.PlotSize is { } plot && !Enum.IsDefined(plot) ||
                b.Kind is { } kind && !Enum.IsDefined(kind) || b.FacilityTags.Any(tag => !Enum.IsDefined(tag))))
            throw new ArgumentException("Invalid specification enum.", nameof(draft));
        if (draft.Specification is DesignSpecification.Creature { Behavior: { } creature } &&
            (!double.IsFinite(creature.SpeedMetersPerSecond) || !double.IsFinite(creature.RetreatThreshold) || !double.IsFinite(creature.RaidDurationSeconds)))
            throw new ArgumentException("Creature values must be finite.", nameof(draft));
        if (draft.Specification is DesignSpecification.Creature { Weapon: { } creatureWeapon } && !double.IsFinite(creatureWeapon.IntervalSeconds))
            throw new ArgumentException("Weapon interval must be finite.", nameof(draft));
        if (draft.Specification is DesignSpecification.FunctionalComposite functionalComposite &&
            functionalComposite.Behaviors.Any(behavior => behavior switch
            {
                Train.Core.Contracts.PartCapability.Propulsion propulsion => !double.IsFinite(propulsion.TractionKilonewtons) || !double.IsFinite(propulsion.RatedSpeedMetersPerSecond),
                Train.Core.Contracts.PartCapability.Production production => !double.IsFinite(production.WorkPerSecond),
                Train.Core.Contracts.PartCapability.Extraction extraction => !double.IsFinite(extraction.WorkPerSecond),
                Train.Core.Contracts.PartCapability.Construction construction => !double.IsFinite(construction.WorkPerSecond) || !Enum.IsDefined(construction.MaximumBuildingPlotSize) ||
                    construction.MaximumVehicleSize is { } maximum && !Enum.IsDefined(maximum),
                Train.Core.Contracts.PartCapability.Power power => !double.IsFinite(power.EnergyPerSecond),
                Train.Core.Contracts.PartCapability.Weapon weapon => !double.IsFinite(weapon.IntervalSeconds),
                null => true,
                _ => false
            })) throw new ArgumentException("Behavior values must be finite and enums valid.", nameof(draft));
        foreach (var item in draft.Layout.NestedItems)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!Enum.IsDefined(Placement(item).Orientation)) throw new ArgumentException("Invalid nested orientation.", nameof(draft));
            if (Copy(item) is { } copy) Validate(copy);
        }
    }
}
