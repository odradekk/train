using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Editing;

internal static class VoxelRegionLimits
{
    internal const int MaximumBrushWidth = 16;
    internal const int MaximumCoveredCells = 4096;
    internal const int ConfirmationThreshold = 512;
}

internal sealed class DesignEditSession : IDesignEditSession
{
    private sealed record ElementLock(SceneProblem Problem, ImmutableHashSet<LockedDesignOperation> Operations);
    private sealed record PreviewValue(EditStamp Basis, EditObject Result, int CoveredCells, int AffectedCells,
        int SkippedCells, bool OccupancyComplete, bool RequiresConfirmation, ImmutableArray<EditConflict> Conflicts,
        ImmutableArray<GridPosition> HighlightCells,
        ImmutableArray<(EditElementId Id, LockedDesignOperation Operation)> LockChecks) : IEditPreview
    {
        public bool CanApply => Conflicts.IsEmpty;
    }

    private readonly EditContent content;
    private readonly Stack<EditObject> undo = new();
    private readonly Stack<EditObject> redo = new();
    private readonly Dictionary<EditElementId, ElementLock> locks = [];
    private readonly Dictionary<ulong, DesignDraft> captures = [];
    private readonly Dictionary<EditElementId, SceneObjectId> objectIds = [];
    private readonly Dictionary<(SceneObjectId Parent, GridPosition Cell), SceneObjectId> modelIds = [];
    private readonly SceneSessionId frameSession = new(Guid.NewGuid());
    private EditObject root;
    private DesignDraft saved;
    private DesignEvaluation? evaluation;
    private ImmutableArray<EditElementId> selection = [];
    private EditElementId? entered;
    private PreviewValue? preview;
    private ulong nextElement, nextObject, revision, frameRevision, space = 1;
    private bool disposed;
    public EditSessionId Id { get; } = new(Guid.NewGuid());
    public ContentGeneration Content { get; }
    private EditStamp Stamp => new(Id, new(revision), Content);
    private ImmutableArray<EditElement> Elements => EditLayout.Elements(root).ToImmutableArray();
    private ImmutableArray<int> EnteredPath => entered is { } id ? Elements.First(element => element.Id == id).ObjectPath : [];

    internal DesignEditSession(IContentView view, DesignEvaluation initial)
    {
        var projection = EditContent.ValidateEvaluation(initial);
        Content = view.Generation;
        content = new(view);
        saved = EditContent.Definition(initial);
        root = EditLayout.Import(saved, NextElement);
        evaluation = initial;
        RegisterMissing(root);
        RegisterLocks(projection);
    }

    private EditElementId NextElement() => new(++nextElement);
    private void CheckOpen() => ObjectDisposedException.ThrowIf(disposed, this);
    public EditSnapshot Capture()
    {
        CheckOpen();
        var draft = root.Export();
        captures[revision] = draft;
        return new(Stamp, draft);
    }
    public EditState Read()
    {
        CheckOpen();
        var snapshot = Capture();
        return new(snapshot, !EditLayout.Same(snapshot.Draft, saved), undo.Count > 0, redo.Count > 0,
            entered, selection, Elements.Select(element => element with { Locked = locks.ContainsKey(element.Id) }).ToImmutableArray(),
            evaluation, evaluation is null ? [] : EditContent.Diagnostics(evaluation));
    }

    public SceneResult<EditState> Select(EditSelection value)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(value);
        var path = EnteredPath;
        var current = EditLayout.At(root, path);
        ImmutableArray<EditElementId> ids;
        if (value is EditSelection.Elements elements)
        {
            if (elements.Ids.IsDefault) throw new ArgumentException("Selection must be initialized.", nameof(value));
            ids = elements.Ids.Distinct().ToImmutableArray();
            var available = current.Cells.Select(cell => cell.Id).Concat(current.Children.Select(child => child.Id)).ToHashSet();
            if (ids.Any(id => !available.Contains(id))) return Fail<EditState>("target_missing", "Selection element is not in the current object.");
        }
        else if (value is EditSelection.Box box)
        {
            ValidateBounds(box.Bounds);
            var picked = current.Cells.Where(cell => Contains(box.Bounds, cell.At)).Select(cell => cell.Id).ToList();
            foreach (var child in current.Children)
            {
                var occupied = ChildOccupied(child);
                if (occupied.Cells.Any(cell => Contains(box.Bounds, cell.At)) ||
                    !occupied.Complete && Contains(box.Bounds, EditLayout.Placement(child.Item).At)) picked.Add(child.Id);
            }
            ids = picked.ToImmutableArray();
        }
        else throw new ArgumentException("Invalid selection.", nameof(value));
        selection = ids;
        return Success(Read());
    }

    public SceneResult<EditState> Enter(EditElementId? element)
    {
        CheckOpen();
        if (element is { } id)
        {
            var found = Elements.FirstOrDefault(entry => entry.Id == id);
            if (found is null) return Fail<EditState>("target_missing", "The nested object no longer exists.");
            if (locks.TryGetValue(id, out var locked) && locked.Operations.Contains(LockedDesignOperation.Enter)) return Failure<EditState>([locked.Problem]);
            if (found.Kind is not (EditElementKind.Assembly or EditElementKind.SceneModel) || !EditLayout.CanEnter(EditLayout.At(root, found.ObjectPath)))
                return Fail<EditState>("invalid_operation", "Only copied objects can be entered.");
        }
        entered = element;
        selection = [];
        preview = null;
        space++;
        return Success(Read());
    }

    public SceneResult<DesignDraft> CaptureObject(EditElementId element)
    {
        CheckOpen();
        var entry = Elements.FirstOrDefault(item => item.Id == element);
        if (entry is null) return Fail<DesignDraft>("target_missing", "The object no longer exists.");
        if (LockFor(element, LockedDesignOperation.Copy) is { } problem) return Failure<DesignDraft>([problem]);
        if (entry.Kind is not (EditElementKind.Assembly or EditElementKind.SceneModel) || !EditLayout.CanEnter(EditLayout.At(root, entry.ObjectPath)))
            return Fail<DesignDraft>("invalid_operation", "Only copied objects can be exported.");
        return Success(EditLayout.At(root, entry.ObjectPath).Export());
    }

    public SceneResult<EditElementId> Locate(EditStamp stamp, DesignElementPath path)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(path);
        if (stamp != Stamp) return Fail<EditElementId>("stale_request", "The diagnostic belongs to another edit revision.");
        var indices = path switch { DesignElementPath.Cell cell => cell.NestedItemIndices,
            DesignElementPath.FunctionalComposite composite => composite.NestedItemIndices, _ => default };
        if (indices.IsDefault) throw new ArgumentException("Path must be initialized.", nameof(path));
        var element = Elements.FirstOrDefault(entry => EditLayout.Same(entry.Path, path));
        return element is null ? Fail<EditElementId>("target_missing", "The diagnostic element is absent.") : Success(element.Id);
    }

    public SceneResult<IEditPreview> Preview(EditOperation operation)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(operation);
        if (operation is EditOperation.MoveSelection or EditOperation.RotateSelection or EditOperation.CopySelection or
            EditOperation.PaintSelection or EditOperation.ShapeSelection || operation is EditOperation.Cells
            { Footprint: EditFootprint.Box } or EditOperation.Cells { Action: CellEdit.Paint or CellEdit.Shape })
            throw new NotImplementedException("Region and selection editing is implemented by Issue #334.");
        preview = null;
        var path = EnteredPath;
        var current = EditLayout.At(root, path);
        var next = current;
        var conflicts = ImmutableArray.CreateBuilder<EditConflict>();
        var lockChecks = ImmutableArray.CreateBuilder<(EditElementId, LockedDesignOperation)>();
        var highlight = new HashSet<GridPosition>();
        var affected = 0;
        int? covered = null;
        var skipped = 0;
        var occupied = Occupied(current);
        var elements = Elements;
        switch (operation)
        {
            case EditOperation.Cells cells:
            {
                ArgumentNullException.ThrowIfNull(cells.Footprint);
                ArgumentNullException.ThrowIfNull(cells.Action);
                var footprint = Footprint(cells.Footprint);
                if (footprint is null) return Fail<IEditPreview>("region_limit", "The brush exceeds the region limit.");
                highlight.UnionWith(footprint);
                var value = cells.Action switch { CellEdit.Add add => add.Value, CellEdit.Replace replace => replace.Value, _ => null };
                if (cells.Action is CellEdit.Add or CellEdit.Replace) ArgumentNullException.ThrowIfNull(value);
                if (value is not null)
                {
                    var invalid = ValidateValue(value);
                    if (!invalid.IsEmpty) return Failure<IEditPreview>(invalid);
                }
                var own = current.Cells.ToLookup(cell => cell.At);
                var all = Occupied(root).Cells.Select(cell => (At: EditLayout.ToLocal(root, path, cell.At), cell.Id)).ToLookup(cell => cell.At);
                var changed = current.Cells.ToDictionary(cell => cell.Id);
                var palette = current.Palette;
                foreach (var at in footprint)
                {
                    var targets = own[at].ToArray();
                    var blockers = all[at].ToArray();
                    var locked = blockers.Select(cell => (cell.Id, Problem: CheckLock(cell.Id,
                        cells.Action is CellEdit.Remove ? LockedDesignOperation.Remove : LockedDesignOperation.Replace)))
                        .FirstOrDefault(value => value.Problem is not null);
                    if (locked.Problem is not null) { AddConflict(at, locked.Id, locked.Problem); continue; }
                    var blocker = blockers.FirstOrDefault(cell => !targets.Any(target => target.Id == cell.Id));
                    if (blocker.Id != default)
                    { AddConflict(at, blocker.Id, Problem("occupancy_conflict", "The brush intersects another object's cells.")); continue; }
                    if (cells.Action is CellEdit.Add && targets.Length > 0) { skipped++; continue; }
                    if (targets.Length > 1)
                    { AddConflict(at, null, Problem("occupancy_conflict", "The brush intersects overlapping cells.")); continue; }
                    var target = targets.FirstOrDefault();
                    if (cells.Action is CellEdit.Remove)
                    {
                        if (target is null) { skipped++; continue; }
                        changed.Remove(target.Id);
                    }
                    else
                    {
                        if (cells.Action is CellEdit.Replace && target is null || target is not null && current.Palette[target.PaletteIndex] == value)
                        { skipped++; continue; }
                        var index = palette.IndexOf(value!);
                        if (index < 0) { index = palette.Length; palette = palette.Add(value!); }
                        var replacement = new EditCell(target?.Id ?? NextElement(), at, index);
                        changed[replacement.Id] = replacement;
                    }
                    affected++;
                }
                next = current with { Palette = palette, Cells = changed.Values.ToImmutableArray() };
                break;
            }
            case EditOperation.RemoveSelection:
            {
                var ids = selection.ToHashSet();
                foreach (var id in ids)
                    if (CheckLock(id, LockedDesignOperation.Remove) is { } problem) conflicts.Add(new(null, id, problem));
                next = Remove(current, ids);
                var remaining = Occupied(next).Cells.Select(cell => cell.Id).ToHashSet();
                var removed = occupied.Cells.Where(cell => !remaining.Contains(cell.Id)).ToArray();
                affected = removed.Length;
                highlight.UnionWith(removed.Select(cell => cell.At));
                break;
            }
            case EditOperation.Properties properties:
                ArgumentNullException.ThrowIfNull(properties.Display);
                ArgumentNullException.ThrowIfNull(properties.Specification);
                if (properties.Specification.GetType() != current.Specification.GetType())
                    return Fail<IEditPreview>("invalid_operation", "Properties cannot change the design category.");
                next = current with { Display = properties.Display, Specification = properties.Specification };
                EditLayout.Validate(next.Export());
                next = next with { Display = new(properties.Display.Name.Trim(), properties.Display.Description.Trim()) };
                break;
            case EditOperation.PlaceNested:
            case EditOperation.ReplaceNested:
            {
                var item = operation is EditOperation.PlaceNested add ? add.Value : ((EditOperation.ReplaceNested)operation).Value;
                ArgumentNullException.ThrowIfNull(item);
                var temporary = new DesignDraft(current.Display, current.Specification, new([], [], [item]));
                EditLayout.Validate(temporary);
                temporary = EditLayout.Normalize(temporary);
                item = temporary.Layout.NestedItems[0];
                if (!EditLayout.ValidCopies(temporary))
                    return Fail<IEditPreview>("invalid_operation", "Nested copy category does not match its kind.");
                var missing = content.Missing(temporary);
                if (!missing.IsEmpty) return Failure<IEditPreview>(missing);
                var index = operation is EditOperation.ReplaceNested replace
                    ? Enumerable.Range(0, current.Children.Length).FirstOrDefault(i => current.Children[i].Id == replace.Element, -1) : -1;
                if (operation is EditOperation.ReplaceNested replacement)
                {
                    if (index < 0) return Fail<IEditPreview>("target_missing", "The nested object is not in the current object.");
                    if (CheckLock(replacement.Element, LockedDesignOperation.Replace) is { } problem) conflicts.Add(new(null, replacement.Element, problem));
                    if (EditLayout.Same(current.Export().Layout.NestedItems[index], item)) break;
                }
                var child = new EditChild(NextElement(), item, EditLayout.Copy(item) is { } copy ? EditLayout.Import(copy, NextElement) : null);
                var target = ChildOccupied(child);
                var without = index < 0 ? current : current with { Children = current.Children.RemoveAt(index) };
                highlight.UnionWith(target.Cells.Select(cell => cell.At));
                var anchorBlocker = without.Children.FirstOrDefault(other => EditLayout.Placement(other.Item).At == EditLayout.Placement(item).At);
                if (anchorBlocker is not null)
                    AddConflict(EditLayout.Placement(item).At, anchorBlocker.Id, Problem("occupancy_conflict", "Nested anchors overlap."));
                next = current with { Children = index < 0 ? current.Children.Add(child) : current.Children.SetItem(index, child) };
                var changedCells = target.Cells.Select(cell => cell.At);
                if (index >= 0) changedCells = changedCells.Concat(ChildOccupied(current.Children[index]).Cells.Select(cell => cell.At));
                affected = changedCells.Distinct().Count();
                covered = affected;
                break;
            }
            default: throw new ArgumentException("Invalid edit operation.", nameof(operation));
        }
        var result = EditLayout.Update(root, path, _ => next);
        var previousCells = Occupied(root).Cells.ToHashSet();
        var resultOccupied = Occupied(result);
        foreach (var group in resultOccupied.Cells.GroupBy(cell => cell.At))
            if (group.Count() > 1 && group.Any(cell => !previousCells.Contains(cell)))
            {
                var blockers = group.Where(previousCells.Contains).Select(cell => cell.Id).Distinct().ToArray();
                AddConflict(EditLayout.ToLocal(root, path, group.Key), blockers.Length == 1 ? blockers[0] : null,
                    Problem("occupancy_conflict", "The edit overlaps another object's cells."));
            }
        preview = new(Stamp, result, covered ?? highlight.Count, affected, skipped,
            resultOccupied.Complete, operation is EditOperation.Cells && affected > VoxelRegionLimits.ConfirmationThreshold,
            conflicts.ToImmutable(), highlight.OrderBy(cell => cell.X).ThenBy(cell => cell.Y).ThenBy(cell => cell.Z).ToImmutableArray(), lockChecks.ToImmutable());
        return Success<IEditPreview>(preview);

        SceneProblem? CheckLock(EditElementId id, LockedDesignOperation action)
        {
            lockChecks.Add((id, action));
            return LockFor(id, action, elements);
        }

        void AddConflict(GridPosition at, EditElementId? blocker, SceneProblem problem)
        {
            if (!conflicts.Any(conflict => conflict.Cell == at)) conflicts.Add(new(at, blocker, problem));
        }
    }

    public SceneResult<EditState> Apply(IEditPreview value, bool confirmed)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(value, preview) || preview.Basis != Stamp)
            return Fail<EditState>("stale_preview", "The preview is no longer current.");
        if (!preview.Conflicts.IsEmpty) return Failure<EditState>(preview.Conflicts.Select(conflict => conflict.Problem).ToImmutableArray());
        foreach (var check in preview.LockChecks)
            if (LockFor(check.Id, check.Operation) is { } problem) return Failure<EditState>([problem]);
        if (preview.RequiresConfirmation && !confirmed) return Fail<EditState>("confirmation_required", "Confirm the brush operation first.");
        var next = preview.Result;
        preview = null;
        if (!EditLayout.Same(root.Export(), next.Export()))
        {
            undo.Push(root);
            redo.Clear();
            root = next;
            Changed();
        }
        return Success(Read());
    }

    public SceneResult<EditState> Undo() => Restore(undo, redo);
    public SceneResult<EditState> Redo() => Restore(redo, undo);
    private SceneResult<EditState> Restore(Stack<EditObject> source, Stack<EditObject> destination)
    {
        CheckOpen();
        if (source.Count == 0) return Fail<EditState>("invalid_operation", "There is no edit to restore.");
        destination.Push(root);
        root = source.Pop();
        Changed();
        return Success(Read());
    }
    private void Changed()
    {
        revision++;
        evaluation = null;
        preview = null;
        var ids = Elements.Select(element => element.Id).ToHashSet();
        selection = selection.Where(ids.Contains).ToImmutableArray();
        if (entered is { } id && !ids.Contains(id)) { entered = null; space++; }
    }

    public bool AcceptEvaluation(EditStamp requested, DesignEvaluation result)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(result);
        if (disposed || requested != Stamp || result is DesignEvaluation.Failed) return false;
        var projection = EditContent.ValidateEvaluation(result);
        var draft = EditContent.Definition(result);
        if (!EditLayout.Same(root.Export(), draft)) return false;
        evaluation = result;
        RegisterLocks(projection);
        return true;
    }

    public bool MarkSaved(EditSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (disposed || value.Stamp.Session != Id || value.Stamp.Content != Content ||
            !captures.TryGetValue(value.Stamp.Revision.Value, out var captured)) return false;
        EditLayout.Validate(value.Draft);
        if (!EditLayout.Same(captured, value.Draft)) return false;
        saved = captured;
        return true;
    }

    public SceneResult<ISceneFrame> Present(EditViewOptions options, ISceneFrame? displayedFrame)
    {
        CheckOpen();
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.WorkPlane);
        if (!Enum.IsDefined(options.WorkPlane.Axis) || (options.DebugLayers & ~(SceneDebugLayer.Chunks | SceneDebugLayer.RailwaySegments |
            SceneDebugLayer.Origin | SceneDebugLayer.PickRay | SceneDebugLayer.Centerline)) != 0) throw new ArgumentException("Invalid edit view.", nameof(options));
        if (displayedFrame is not null && displayedFrame.Update.Frame.Session != frameSession)
            throw new ArgumentException("Displayed frame belongs to another session.", nameof(displayedFrame));
        var path = EnteredPath;
        var geometry = EditGeometry.Build(EditLayout.At(root, path), content, Id, new(revision), options.HighestVisibleY, ObjectId, ModelId, path);
        var selectedPaths = selection.IsEmpty ? ImmutableArray.Create(path) : Elements.Where(element => selection.Contains(element.Id) && element.Kind != EditElementKind.Cell)
            .Select(element => element.ObjectPath).ToImmutableArray();
        var pose = new ScenePose(Vector3.Zero, Quaternion.Identity);
        var node = root;
        foreach (var index in path)
        {
            var child = node.Children[index];
            var placement = EditLayout.Placement(child.Item);
            pose = SceneGeometry.Compose(pose, SceneGeometry.Placement(placement.At, placement.Orientation));
            node = child.Copy!;
        }
        return Success<ISceneFrame>(new SceneFrame(new(frameSession, new(1), new(space), ++frameRevision), new(0, 0, 0), 0,
            SceneReadiness.Ready, geometry.Objects, geometry.Picking, [], geometry.Diagnostics,
            displayedFrame as SceneFrame, debugLayers: options.DebugLayers, editLines: EditGeometry.SweepLines(evaluation, selectedPaths, pose)));
    }
    private SceneObjectId ObjectId(EditElementId id)
    {
        if (!objectIds.TryGetValue(id, out var value)) objectIds.Add(id, value = new(++nextObject));
        return value;
    }
    private SceneObjectId ModelId(SceneObjectId parent, GridPosition cell)
    {
        if (!modelIds.TryGetValue((parent, cell), out var value)) modelIds.Add((parent, cell), value = new(++nextObject));
        return value;
    }

    public void Dispose() { disposed = true; preview = null; }

    private void RegisterLocks(DesignSceneProjection projection)
    {
        foreach (var value in projection.EditLocks)
        {
            var entry = Elements.FirstOrDefault(element => EditLayout.Same(element.Path, value.Element));
            if (entry is not null) locks[entry.Id] = new(new("edit_locked", SceneStage.Edit, null, value.Reference,
                value.Package, value.Element, "The original missing reference is locked."), value.BlockedOperations.ToImmutableHashSet());
        }
    }
    private void RegisterMissing(EditObject node)
    {
        foreach (var cell in node.Cells)
        {
            var voxel = node.Palette[cell.PaletteIndex].Voxel;
            var missing = EditContent.MissingProblems(content.Appearance(voxel), new ContentReference.Core(new Train.Core.Contracts.CoreDefinitionReference.Voxel(voxel)));
            if (!missing.IsEmpty) locks[cell.Id] = new(missing[0] with { Code = "edit_locked" }, Enum.GetValues<LockedDesignOperation>().ToImmutableHashSet());
        }
        foreach (var child in node.Children)
            if (child.Copy is not null) RegisterMissing(child.Copy);
            else if (child.Item is NestedDesignItem.FunctionalCompositeReference reference && content.Composite(reference.Definition) is { Missing: true } composite)
                locks[child.Id] = new((composite.Problems.FirstOrDefault() ?? EditContent.Problem(new ContentReference.Core(new Train.Core.Contracts.CoreDefinitionReference.Composite(reference.Definition))))
                    with { Code = "edit_locked" }, Enum.GetValues<LockedDesignOperation>().ToImmutableHashSet());
    }

    private SceneProblem? LockFor(EditElementId id, LockedDesignOperation operation) => LockFor(id, operation, Elements);

    private SceneProblem? LockFor(EditElementId id, LockedDesignOperation operation, ImmutableArray<EditElement> entries)
    {
        var descendants = new HashSet<EditElementId> { id };
        foreach (var entry in entries)
            if (entry.Parent is { } parent && descendants.Contains(parent)) descendants.Add(entry.Id);
        foreach (var entry in entries)
            if (descendants.Contains(entry.Id) && locks.TryGetValue(entry.Id, out var value) && value.Operations.Contains(operation))
                return value.Problem with { Element = entry.Path };
        return null;
    }
    private static EditObject Remove(EditObject value, HashSet<EditElementId> ids) => value with
    {
        Cells = value.Cells.Where(cell => !ids.Contains(cell.Id)).ToImmutableArray(),
        Children = value.Children.Where(child => !ids.Contains(child.Id)).Select(child => child.Copy is null ? child : child with { Copy = Remove(child.Copy, ids) }).ToImmutableArray()
    };

    private (ImmutableArray<(GridPosition At, EditElementId Id)> Cells, bool Complete) Occupied(EditObject node)
    {
        var cells = node.Cells.Select(cell => (cell.At, cell.Id)).ToImmutableArray().ToBuilder();
        var complete = true;
        foreach (var child in node.Children)
        {
            var occupied = ChildOccupied(child);
            cells.AddRange(occupied.Cells);
            complete &= occupied.Complete;
        }
        return (cells.ToImmutable(), complete);
    }
    private (ImmutableArray<(GridPosition At, EditElementId Id)> Cells, bool Complete) ChildOccupied(EditChild child)
    {
        if (child.Copy is not null)
        {
            var occupied = Occupied(child.Copy);
            return (occupied.Cells.Select(cell => (EditLayout.Map(cell.At, child.Item), cell.Id)).ToImmutableArray(), occupied.Complete);
        }
        var composite = content.Composite(((NestedDesignItem.FunctionalCompositeReference)child.Item).Definition);
        return (composite.Voxels.Select(cell => (EditLayout.Map(cell.LocalAt, child.Item), child.Id)).ToImmutableArray(), composite.Complete);
    }

    private ImmutableArray<SceneProblem> ValidateValue(VoxelPaletteEntry value)
    {
        if (!Enum.IsDefined(value.Orientation)) throw new ArgumentException("Invalid voxel orientation.", nameof(value));
        var lookup = content.Appearance(value.Voxel);
        var missing = EditContent.MissingProblems(lookup, new ContentReference.Core(new Train.Core.Contracts.CoreDefinitionReference.Voxel(value.Voxel)));
        if (!missing.IsEmpty) return missing;
        var appearance = ((ContentLookup<VoxelAppearanceProjection>.Found)lookup).Value;
        if (appearance.AllowedOrientations == AllowedVoxelOrientations.None && value.Orientation != VoxelOrientation.PxPy ||
            appearance.AllowedOrientations == AllowedVoxelOrientations.FourWay && value.Orientation is not
                (VoxelOrientation.PxPy or VoxelOrientation.NxPy or VoxelOrientation.PzPy or VoxelOrientation.NzPy) ||
            value.Shape is { } shape && (appearance.Presentation is not VoxelPresentation.Cube cube || !cube.Shapes.Contains(shape)))
            return [Problem("invalid_operation", "The voxel does not support this orientation or shape.")];
        return [];
    }

    private static HashSet<GridPosition>? Footprint(EditFootprint value)
    {
        if (value is EditFootprint.Cell cell) return [cell.At];
        if (value is not EditFootprint.Stroke stroke) throw new ArgumentException("Invalid footprint.", nameof(value));
        if (stroke.Samples.IsDefault) throw new ArgumentException("Brush samples must be initialized.", nameof(value));
        if (stroke.BrushWidth < 1 || stroke.BrushWidth > VoxelRegionLimits.MaximumBrushWidth) return null;
        var result = new HashSet<GridPosition>();
        foreach (var sample in stroke.Samples)
            for (var x = 0; x < stroke.BrushWidth; x++)
            for (var y = 0; y < stroke.BrushWidth; y++)
            for (var z = 0; z < stroke.BrushWidth; z++)
            {
                var px = (long)sample.X + x; var py = (long)sample.Y + y; var pz = (long)sample.Z + z;
                if (px > int.MaxValue || py > int.MaxValue || pz > int.MaxValue) return null;
                result.Add(new((int)px, (int)py, (int)pz));
                if (result.Count > VoxelRegionLimits.MaximumCoveredCells) return null;
            }
        return result;
    }
    private static bool Contains(GridBounds bounds, GridPosition at) => at.X >= bounds.Minimum.X && at.X <= bounds.Maximum.X &&
        at.Y >= bounds.Minimum.Y && at.Y <= bounds.Maximum.Y && at.Z >= bounds.Minimum.Z && at.Z <= bounds.Maximum.Z;
    private static void ValidateBounds(GridBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (bounds.Minimum.X > bounds.Maximum.X || bounds.Minimum.Y > bounds.Maximum.Y || bounds.Minimum.Z > bounds.Maximum.Z)
            throw new ArgumentException("Selection bounds are reversed.", nameof(bounds));
    }
    private static SceneProblem Problem(string code, string detail) => new(code, SceneStage.Edit, null, null, null, null, detail);
    private static SceneResult<T> Success<T>(T value) where T : notnull => new SceneResult<T>.Success(value);
    private static SceneResult<T> Failure<T>(ImmutableArray<SceneProblem> problems) where T : notnull => new SceneResult<T>.Failure(problems);
    private static SceneResult<T> Fail<T>(string code, string detail) where T : notnull => Failure<T>([Problem(code, detail)]);
}
