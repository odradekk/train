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
    private ObservationCameraPose _camera;
    private IReadOnlySet<ObservedObject> _expectedRange = new HashSet<ObservedObject>();

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
                foreach (var cell in new[] { FirstBatchCell, SecondBatchCell, VisibleOccluder, HiddenOccluder })
                    Require(Session.SubmitEdit(new VoxelAddEdit(cell, WorldEditorSequences.EditVoxelId,
                            VoxelOrientation.Identity)).Status == "Added", $"could not add multi-select fixture {cell}");
                _physicsFrame = Engine.GetPhysicsFrames() + 2;
                _phase++;
                return false;
            case 1:
                if (Engine.GetPhysicsFrames() < _physicsFrame)
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
                var projection = WorldScreen(VisibleOccluder);
                Require(projection.DistanceTo(WorldScreen(HiddenOccluder)) < 0.1f,
                    "occlusion fixture cells are not on the same camera ray");
                _camera = Observer.Pose;
                Drag(projection - new Vector2(7, 7), projection + new Vector2(7, 7));
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
                StartBatch();
                _phase++;
                return false;
            case 8:
                CommitBatch();
                GD.Print("WORKSPACE_DONE leg=multi-select");
                scene.GetTree().Quit();
                return true;
            default:
                return true;
        }
    }

    private void CheckListModifiers()
    {
        Click(View.ActionButton("clear"));
        _camera = Observer.Pose;
        var list = View.ListControl;
        var first = IndexOf(ObservedObject.Vehicle(Session.Vehicles[0].Id));
        var second = IndexOf(ObservedObject.Vehicle(Session.Vehicles[1].Id));
        ClickList(list, first);
        ClickList(list, second, ctrl: true);
        Require(Observer.SelectedObjects.SetEquals(new[] { View.Entries[first].Object, View.Entries[second].Object }),
            "Ctrl list input did not add the second vehicle");

        var rangeEnd = Math.Min(Math.Max(first, second) + 2, View.Entries.Count - 1);
        ClickList(list, rangeEnd, shift: true);
        var anchor = second;
        _expectedRange = Enumerable.Range(Math.Min(anchor, rangeEnd), Math.Abs(rangeEnd - anchor) + 1)
            .Select(index => View.Entries[index].Object).ToHashSet();
        Require(Observer.SelectedObjects.SetEquals(_expectedRange), "Shift list input did not select the anchored contiguous range");
        var removed = Math.Min(anchor, rangeEnd);
        ClickList(list, removed, ctrl: true);
        Require(!Observer.SelectedObjects.Contains(View.Entries[removed].Object) &&
                Observer.SelectedObjects.Count == _expectedRange.Count - 1 && Observer.Pose == _camera,
            "Ctrl list input did not remove one item or moved the camera");
        ClickList(list, first);
        GD.Print("WORKSPACE_MULTI_LIST ctrl_add=true shift_range=true ctrl_remove=true camera_moved=false");
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

    private void StartBatch()
    {
        Click(View.ActionButton("clear"));
        var first = WorldScreen(FirstBatchCell);
        var second = WorldScreen(SecondBatchCell);
        Drag(new Vector2(Math.Min(first.X, second.X) - 4, Math.Min(first.Y, second.Y) - 4),
            new Vector2(Math.Max(first.X, second.X) + 4, Math.Max(first.Y, second.Y) + 4));
        Require(Observer.SelectedObjects.SetEquals(new[] { Target(FirstBatchCell), Target(SecondBatchCell) }),
            "real list input did not establish the two batch targets");
        Require(!View.ActionButton("batch-remove").Disabled && !View.ActionButton("batch-replace").Disabled,
            "world-cell selection did not expose both common batch operations");
        var tool = scene.CellTool!.State;
        Require(tool.Definitions.Contains(Ledge), "batch replacement definition is unavailable");
        tool.SetSettings(Ledge, VoxelOrientation.Identity, VoxelCellOperation.Replace);
        Click(View.ActionButton("batch-replace"));
        Require(tool.HasBatchDraft && tool.BatchTargets.ToHashSet().SetEquals(new[] { FirstBatchCell, SecondBatchCell }),
            "batch action did not bind exactly the selected cells");
        ClickList(View.ListControl, IndexOf(ObservedObject.Vehicle(Session.Vehicles[0].Id)));
        Require(tool.HasBatchDraft && tool.BatchTargets.ToHashSet().SetEquals(new[] { FirstBatchCell, SecondBatchCell }) &&
                Observer.SelectedObjects.SetEquals(new[] { ObservedObject.Vehicle(Session.Vehicles[0].Id) }),
            "selection change redirected or discarded the bound batch draft");
    }

    private void CommitBatch()
    {
        var beforeEdits = Session.QueryEditLog().Count;
        var beforeUndo = Session.UndoCount;
        Click(scene.CellTool!.CommitButton);
        Require(!scene.CellTool.State.HasBatchDraft && !scene.CellTool.State.HasDraft &&
                Session.Query(FirstBatchCell).Voxel?.DefinitionId == Ledge &&
                Session.Query(SecondBatchCell).Voxel?.DefinitionId == Ledge &&
                Session.QueryEditLog().Count == beforeEdits + 1 && Session.UndoCount == beforeUndo + 1,
            "real commit input did not apply one formal multi-target edit and one undo record");
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
    {
        var target = WorldEditorInspector.WorldTarget(Session, cell);
        return target.Kind == ObservedObjectKind.WorldCell && Session.Query(cell).Voxel is { } voxel
            ? ObservedObject.WorldCell(cell, voxel.RuntimeId, voxel.DefinitionId)
            : target;
    }

    private Vector2 WorldScreen(WorldVoxelCoord cell) => scene.GetViewport().GetScreenTransform() *
        scene.Camera.UnprojectPosition(new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f));

    private static void Drag(Vector2 start, Vector2 end)
    {
        Input.ParseInputEvent(new InputEventMouseButton
            { ButtonIndex = MouseButton.Left, Pressed = true, Position = start, GlobalPosition = start });
        Input.ParseInputEvent(new InputEventMouseMotion
            { Position = end, GlobalPosition = end, Relative = end - start, ButtonMask = MouseButtonMask.Left });
        Input.ParseInputEvent(new InputEventMouseButton
            { ButtonIndex = MouseButton.Left, Pressed = false, Position = end, GlobalPosition = end });
        Input.FlushBufferedEvents();
    }

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
    {
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = point, GlobalPosition = point,
            CtrlPressed = ctrl, ShiftPressed = shift
        });
        Input.ParseInputEvent(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = point, GlobalPosition = point,
            CtrlPressed = ctrl, ShiftPressed = shift
        });
        Input.FlushBufferedEvents();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
