using Train.Core.Content.Voxels;
using Godot;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

/// <summary>Real Godot pointer and modifier-key coverage for whole-level multi-selection and batch editing.</summary>
internal sealed class WorldEditorMultiSelectionSmoke(WorldEditorScene scene)
{
    private static readonly WorldVoxelCoord FirstBatchCell = new(74, 12, 5);
    private static readonly WorldVoxelCoord SecondBatchCell = new(78, 12, 5);
    private static readonly WorldVoxelCoord VisibleOccluder = new(76, 8, 4);
    private static readonly WorldVoxelCoord HiddenOccluder = new(76, 5, 9);
    private static readonly VoxelDefinitionId Ledge = new("core", "ledge");
    private int _phase;
    private ulong _physicsFrame;
    private ulong _physicsDeadline;
    private ObservationCameraPose _camera;
    private IReadOnlySet<ObservedObject> _expectedRange = new HashSet<ObservedObject>();
    private int _commitBeforeEdits;
    private int _commitBeforeUndo;
    private WorldEditorBoxCapacitySmoke? _capacitySmoke;

    private VoxelDevelopmentSession Session => scene.Session!;
    private WorldEditorObservationView View => scene.Observation!;
    private WorldEditorObserver Observer => View.Observer;

    public bool Advance()
    {
        switch (_phase)
        {
            case 0:
                Require(Session.SimulationPaused && Session.Vehicles.Count >= 2 && Session.Rails.Segments.Count > 0,
                    "multi-select leg did not establish the paused train configuration");
                foreach (var cell in FixtureCells())
                    Require(Session.SubmitEdit(new VoxelAddEdit(cell, WorldEditorSequences.EditVoxelId,
                            VoxelOrientation.Identity)).Status == "Added", $"could not add multi-select fixture {cell}");
                _phase++;
                return false;
            case 1:
                // MarkDirty is consumed before this Advance call. Start the physics wait only now, after Refresh has
                // rebuilt the collider nodes, and wait for their actual published shapes as well as a frame count.
                if (_physicsFrame == 0)
                {
                    _physicsFrame = Engine.GetPhysicsFrames() + 2;
                    _physicsDeadline = Engine.GetPhysicsFrames() + 120;
                    return false;
                }
                if (Engine.GetPhysicsFrames() > _physicsDeadline)
                    throw new InvalidOperationException("multi-select fixture colliders were not published within 120 physics frames");
                if (Engine.GetPhysicsFrames() < _physicsFrame ||
                    FixtureCells().Any(cell => !scene.Colliders.QueryPublishedCellInterior(scene.GetWorld3D(), cell)))
                    return false;
                _camera = Observer.Pose;
                var screen = scene.GetViewport().GetScreenTransform();
                Drag(screen * (View.WorldRect.Position + new Vector2(2, 2)),
                    screen * (View.WorldRect.End - new Vector2(2, 2)));
                _phase++;
                return false;
            case 2:
                var maximum = View.LastBoxSelection ?? throw new InvalidOperationException("full-world input drag produced no box result");
                Require(maximum.Objects.Any(item => item.Kind == ObservedObjectKind.Vehicle) &&
                        maximum.Objects.Any(item => item.Kind == ObservedObjectKind.RailSegment) &&
                        maximum.Objects.Any(item => item.Kind == ObservedObjectKind.WorldCell) &&
                        Observer.Pose == _camera,
                    "full-world box did not select visible vehicles, rails and world cells without moving the camera");
                GD.Print("WORKSPACE_MULTI_BOX types=vehicle,rail,cell camera_moved=false");
                GD.Print($"WORKSPACE_MULTI_COST candidates={maximum.Candidates} rays={maximum.Rays} " +
                         $"truncated={maximum.Truncated.ToString().ToLowerInvariant()} elapsed_ms={maximum.ElapsedMilliseconds:0.###}");
                AlignOcclusionRay();
                _physicsFrame = Engine.GetPhysicsFrames() + 2;
                _phase++;
                return false;
            case 3:
                if (Engine.GetPhysicsFrames() < _physicsFrame)
                    return false;
                var projection = RawWorldScreen(VisibleOccluder);
                Require(projection.DistanceTo(RawWorldScreen(HiddenOccluder)) < 0.1f,
                    "occlusion fixture cells are not on the same camera ray");
                _camera = Observer.Pose;
                Drag(ToInputScreen(projection - new Vector2(7, 7)),
                    ToInputScreen(projection + new Vector2(7, 7)));
                _phase++;
                return false;
            case 4:
                var occlusion = View.LastBoxSelection ?? throw new InvalidOperationException("occlusion input drag produced no result");
                var visible = Target(VisibleOccluder);
                var hidden = Target(HiddenOccluder);
                Require(occlusion.Objects.Contains(visible) && !occlusion.Objects.Contains(hidden) &&
                        Observer.SelectedObjects.Contains(visible) && !Observer.SelectedObjects.Contains(hidden) &&
                        Observer.Pose == _camera,
                    "box visibility did not keep the first ray hit and reject the hidden aligned cell");
                GD.Print("WORKSPACE_MULTI_OCCLUSION visible=true hidden=false camera_moved=false");
                Click(View.ActionButton("clear"));
                _phase++;
                return false;
            case 5:
                CheckListModifiers();
                _phase++;
                return false;
            case 6:
                CheckPartBox();
                _phase++;
                return false;
            case 7:
                SelectBatchTargets();
                _phase++;
                return false;
            case 8:
                StartBatch();
                _phase++;
                return false;
            case 9:
                // Let one complete process/layout frame present the newly visible batch parameters.
                _phase++;
                return false;
            case 10:
                OpenReplacementDefinition();
                _phase++;
                return false;
            case 11:
                ChooseReplacementDefinition();
                _phase++;
                return false;
            case 12:
                ClickBatchCommit();
                _phase++;
                return false;
            case 13:
                CheckBatchCommit();
                Observer.SelectMany(new[] { Target(FirstBatchCell), Target(SecondBatchCell) });
                View.Present(0, Session.QueryDisplayPoses());
                Click(View.ActionButton("batch-remove"));
                _phase++;
                return false;
            case 14:
                ShowBoundInputNotice();
                EnsureVisible(scene.CellTool!.DiscardButton);
                _phase++;
                return false;
            case 15:
                EnsureVisible(scene.CellTool!.DiscardButton);
                Click(scene.CellTool!.DiscardButton);
                Require(!scene.CellTool.State.HasDraft && !scene.CellTool.InputNoticeVisible,
                    $"discard left batch feedback visible: draft={scene.CellTool.State.HasDraft} " +
                    $"notice={scene.CellTool.InputNoticeVisible} rect={scene.CellTool.DiscardButton.GetGlobalRect()} " +
                    $"viewport={scene.GetViewport().GetVisibleRect()}");
                _capacitySmoke = new WorldEditorBoxCapacitySmoke(scene);
                _phase++;
                return false;
            case 16:
                if (!_capacitySmoke!.Advance())
                    return false;
                GD.Print("WORKSPACE_DONE leg=multi-select");
                scene.GetTree().Quit();
                return true;
            default:
                return true;
        }
    }

    private void CheckListModifiers()
    {
        _camera = Observer.Pose;
        var list = View.ListControl;
        var first = IndexOf(ObservedObject.Vehicle(Session.Vehicles[0].Id));
        var second = IndexOf(ObservedObject.Vehicle(Session.Vehicles[1].Id));
        // A Ctrl toggle preserves ItemList.current. Pin the native Shift anchor before box selection replaces
        // the domain set; clearing list highlights leaves that anchor in place.
        ClickList(list, second);
        SelectBatchTargets();
        var cells = new[] { Target(FirstBatchCell), Target(SecondBatchCell) };
        Require(Observer.SelectedObjects.SetEquals(cells), "list modifier fixture did not start from box-selected world cells");
        ClickList(list, second, ctrl: true);
        Require(Observer.SelectedObjects.SetEquals(cells.Append(View.Entries[second].Object)),
            "Ctrl list input did not preserve box-selected world cells");
        var rangeEnd = Math.Min(Math.Max(first, second) + 2, View.Entries.Count - 1);
        ClickList(list, rangeEnd, shift: true);
        var anchor = second;
        _expectedRange = Enumerable.Range(Math.Min(anchor, rangeEnd), Math.Abs(rangeEnd - anchor) + 1)
            .Select(index => View.Entries[index].Object).ToHashSet();
        Require(Observer.SelectedObjects.SetEquals(_expectedRange.Concat(cells)),
            "Shift list input did not preserve box-selected world cells while selecting the anchored range");
        var removed = Math.Min(anchor, rangeEnd);
        ClickList(list, removed, ctrl: true);
        Require(!Observer.SelectedObjects.Contains(View.Entries[removed].Object) &&
                cells.All(Observer.SelectedObjects.Contains) &&
                Observer.SelectedObjects.Count == _expectedRange.Count + cells.Length - 1 && Observer.Pose == _camera,
            "Ctrl list input did not remove one list item while preserving world cells, or moved the camera");
        ClickList(list, first);
        Require(Observer.SelectedObjects.SetEquals(new[] { View.Entries[first].Object }),
            "ordinary list input did not replace the complete mixed selection");
        GD.Print("WORKSPACE_MULTI_LIST ctrl_add=true shift_range=true ctrl_remove=true cells_preserved=true camera_moved=false");
    }

    private void CheckPartBox()
    {
        Click(View.ActionButton("parts"));
        var part = Observer.Selection;
        Require(Observer.PartVehicleId is not null && part?.Kind == ObservedObjectKind.VehiclePart,
            "real panel input did not enter vehicle-part selection");
        var screen = scene.GetViewport().GetScreenTransform();
        Drag(screen * (View.WorldRect.Position + new Vector2(2, 2)),
            screen * (View.WorldRect.End - new Vector2(2, 2)));
        Require(View.LastBoxSelection is { Objects.Count: 0, Candidates: 0, Rays: 0 } &&
                Observer.Selection == part && Observer.PartVehicleId is not null,
            "part-level box input changed selection instead of being refused");
        Click(View.ActionButton("whole"));
    }

    private void SelectBatchTargets()
    {
        Click(View.ActionButton("clear"));
        var first = RawWorldScreen(FirstBatchCell);
        var second = RawWorldScreen(SecondBatchCell);
        Drag(ToInputScreen(new Vector2(Math.Min(first.X, second.X) - 4, Math.Min(first.Y, second.Y) - 4)),
            ToInputScreen(new Vector2(Math.Max(first.X, second.X) + 4, Math.Max(first.Y, second.Y) + 4)));
    }

    private void StartBatch()
    {
        var first = RawWorldScreen(FirstBatchCell);
        var second = RawWorldScreen(SecondBatchCell);
        var boxStart = ToInputScreen(new Vector2(Math.Min(first.X, second.X) - 4, Math.Min(first.Y, second.Y) - 4));
        var boxEnd = ToInputScreen(new Vector2(Math.Max(first.X, second.X) + 4, Math.Max(first.Y, second.Y) + 4));
        Require(Observer.SelectedObjects.SetEquals(new[] { Target(FirstBatchCell), Target(SecondBatchCell) }),
            $"real box drag did not establish the two batch targets: actual=[{string.Join(';', Observer.SelectedObjects.Select(item => item.Describe()))}] " +
            $"box_objects=[{string.Join(';', View.LastBoxSelection?.Objects.Select(item => item.Describe()) ?? Array.Empty<string>())}] box={boxStart}..{boxEnd}");
        Require(!View.ActionButton("batch-remove").Disabled && !View.ActionButton("batch-replace").Disabled,
            "world-cell selection did not expose both common batch operations");
        var tool = scene.CellTool!.State;
        Click(View.ActionButton("batch-replace"));
        Require(tool.HasBatchDraft && tool.BatchTargets.ToHashSet().SetEquals(new[] { FirstBatchCell, SecondBatchCell }),
            "batch action did not bind exactly the selected cells");
        Click(View.ActionButton("clear"));
        Require(Observer.SelectedObjects.Count == 0 && tool.HasBatchDraft &&
                tool.BatchTargets.ToHashSet().SetEquals(new[] { FirstBatchCell, SecondBatchCell }) &&
                !scene.CellTool!.DraftPrompt.Visible,
            "real clear input did not clear selection independently of the bound batch draft");
        ClickList(View.ListControl, IndexOf(ObservedObject.Vehicle(Session.Vehicles[0].Id)));
        Require(tool.HasBatchDraft && tool.BatchTargets.ToHashSet().SetEquals(new[] { FirstBatchCell, SecondBatchCell }) &&
                Observer.SelectedObjects.SetEquals(new[] { ObservedObject.Vehicle(Session.Vehicles[0].Id) }),
            "selection change redirected or discarded the bound batch draft");
    }

    private void OpenReplacementDefinition()
    {
        var tool = scene.CellTool!.State;
        Require(tool.Definitions.Contains(Ledge), "batch replacement definition is unavailable");
        Click(scene.CellTool.DefinitionInput);
    }

    private void ChooseReplacementDefinition()
    {
        var input = scene.CellTool!.DefinitionInput;
        Require(input.GetPopup().Visible, "definition popup did not open for batch replacement");
        var index = scene.CellTool.State.Definitions.ToList().IndexOf(Ledge);
        var focused = input.GetPopup().GetFocusedItem();
        var steps = focused < 0 ? index + 1 : (index - focused + input.ItemCount) % input.ItemCount;
        for (var item = 0; item < steps; item++)
        {
            SendKey(Key.Down, true);
            SendKey(Key.Down, false);
        }
        SendKey(Key.Enter, true);
        SendKey(Key.Enter, false);
    }

    private void ClickBatchCommit()
    {
        ShowBoundInputNotice();
        _commitBeforeEdits = Session.QueryEditLog().Count;
        _commitBeforeUndo = Session.UndoCount;
        var button = scene.CellTool!.CommitButton;
        Require(scene.CellTool.State.DefinitionId == Ledge,
            "real definition input did not select the batch replacement definition");
        Require(button.IsVisibleInTree() && !button.Disabled && button.GetGlobalRect().Size.X > 0 && button.GetGlobalRect().Size.Y > 0,
            $"batch commit control is not input-ready: visible={button.IsVisibleInTree()} disabled={button.Disabled} rect={button.GetGlobalRect()}");
        Click(scene.CellTool!.CommitButton);
    }

    private void ShowBoundInputNotice()
    {
        var view = scene.CellTool!;
        Require(view.State.HasBatchDraft, "bound input feedback requires a batch draft");
        // Exercise the Godot control binding for a queued coordinate change, even after the editor locks it.
        view.CoordinateInputs[0].Value += 1;
        Require(view.InputNoticeVisible, "ignored batch coordinate input did not show feedback");
    }

    private static void EnsureVisible(Control control)
    {
        for (var parent = control.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll)
                scroll.EnsureControlVisible(control);
    }

    private void CheckBatchCommit()
    {
        var tool = scene.CellTool!.State;
        var button = scene.CellTool.CommitButton;
        var editDelta = Session.QueryEditLog().Count - _commitBeforeEdits;
        var undoDelta = Session.UndoCount - _commitBeforeUndo;
        Require(!scene.CellTool.State.HasBatchDraft && !scene.CellTool.State.HasDraft && !scene.CellTool.InputNoticeVisible &&
                Session.Query(FirstBatchCell).Voxel?.DefinitionId == Ledge &&
                Session.Query(SecondBatchCell).Voxel?.DefinitionId == Ledge &&
                editDelta == 1 && undoDelta == 1,
            $"real commit input did not apply one formal multi-target edit and one undo record: batch={tool.HasBatchDraft} " +
            $"draft={tool.HasDraft} definition={tool.DefinitionId} orientation={tool.Orientation} edit_delta={editDelta} " +
            $"undo_delta={undoDelta} first={Session.Query(FirstBatchCell).Voxel?.DefinitionId.ToString() ?? "none"} " +
            $"second={Session.Query(SecondBatchCell).Voxel?.DefinitionId.ToString() ?? "none"} button_visible={button.IsVisibleInTree()} " +
            $"button_disabled={button.Disabled} button_rect={button.GetGlobalRect()}");
        GD.Print("WORKSPACE_MULTI_BATCH targets=2 selection_changed=true edits=1 undo=1 committed=true");
    }

    private void AlignOcclusionRay()
    {
        // Overview faces +Z. Move its origin onto the voxel-centre X plane, then pitch through both centres.
        Observer.Fly(-0.5, 0, 0);
        var pose = Observer.Pose;
        var current = pose.Look - pose.Position;
        var movedPosition = pose.Position + new ObservationVector(0.5, 0, 0);
        var target = new ObservationVector(VisibleOccluder.X + 0.5, VisibleOccluder.Y + 0.5,
            VisibleOccluder.Z + 0.5) - movedPosition;
        var currentPitch = Math.Asin(current.Y / current.Length);
        var targetPitch = Math.Asin(target.Y / target.Length);
        Observer.Look(0, targetPitch - currentPitch);
    }

    private int IndexOf(ObservedObject item)
    {
        var index = View.Entries.Select(entry => entry.Object).ToList().IndexOf(item);
        Require(index >= 0, $"object is absent from the inspector list: {item.Describe()}");
        return index;
    }

    private ObservedObject Target(WorldVoxelCoord cell)
        => WorldEditorInspector.WorldTarget(Session, cell);

    private Vector2 RawWorldScreen(WorldVoxelCoord cell) => scene.Camera.UnprojectPosition(CellCenter(cell));

    private Vector2 ToInputScreen(Vector2 viewportPoint) =>
        scene.GetViewport().GetScreenTransform() * viewportPoint;

    private static Vector3 CellCenter(WorldVoxelCoord cell) =>
        new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);

    private static WorldVoxelCoord[] FixtureCells() =>
        new[] { FirstBatchCell, SecondBatchCell, VisibleOccluder, HiddenOccluder };

    private static void Drag(Vector2 start, Vector2 end)
        => GodotSmokeInput.Drag(start, end);

    private static void Click(Control control)
    {
        var point = control.GetViewport().GetScreenTransform() * control.GetGlobalRect().GetCenter();
        ClickAt(point);
    }

    private static void ClickList(ItemList list, int index, bool ctrl = false, bool shift = false)
    {
        var local = list.GetItemRect(index).GetCenter();
        var point = list.GetViewport().GetScreenTransform() * (list.GetGlobalTransformWithCanvas() * local);
        ClickAt(point, ctrl, shift);
    }

    private static void ClickAt(Vector2 point, bool ctrl = false, bool shift = false)
        => GodotSmokeInput.Click(point, ctrl, shift);

    private static void SendKey(Key key, bool pressed) => Input.ParseInputEvent(new InputEventKey
        { Keycode = key, PhysicalKeycode = key, Pressed = pressed });

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
