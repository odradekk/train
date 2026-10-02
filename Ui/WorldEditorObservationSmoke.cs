using System.Globalization;
using Godot;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.WorldEditor;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train;

/// <summary>
/// Headless observation entrance: drives the real world editor camera, panel, keys, clicks and console, and checks
/// that observation reads the displayed bodies while never pausing, advancing or editing the session.
/// </summary>
internal sealed class WorldEditorObservationSmoke
{
    public const string ModeEnvironmentVariable = "TRAIN_WORLD_EDITOR_SMOKE_MODE";
    public const string ModeValue = "observation";

    private const int SettleFrames = 4;
    private const int HoldFrames = 10;
    private const int FlyFrames = 12;
    private const int FollowFrames = 40;
    private const int TransitionLimitFrames = 600;
    private const float Tolerance = 1e-3f;
    private const int RailClickX = 84;
    private const float OrbitDragPixels = 60;

    private readonly WorldEditorScene _scene;
    private readonly bool _resume;
    private readonly List<Vector3> _looks = new();
    private readonly List<Vector3> _positions = new();
    private int _phase;
    private int _frames;
    private ulong _mark;
    private string _fingerprint = "";
    private ObservationCameraPose _pose;
    private Vector3 _bodyOffset;
    private Vector3 _lastCarriageCenter;
    private Vector2 _clickTarget;
    private int _interpolatedFrames;
    private bool _rebuilt;
    private Guid _worldEditorSave;
    private Guid _hollowSave;
    private string _replacedDefinition = "";
    private bool _finished;
    private bool _routingFinished;
    private ObservedObject? _routingSelection;
    private ObservedObject? _partSelection;

    public WorldEditorObservationSmoke(WorldEditorScene scene, string? saveId)
    {
        _scene = scene;
        _resume = Guid.TryParse(saveId, out _);
    }

    public static bool Requested() =>
        string.Equals(System.Environment.GetEnvironmentVariable(ModeEnvironmentVariable), ModeValue, StringComparison.Ordinal);

    private VoxelDevelopmentSession Session => _scene.Session!;
    private WorldEditorObservationView View => _scene.Observation!;
    private WorldEditorObserver Observer => View.Observer;
    private Main Main => _scene.GetNode<Main>("/root/Main");
    private bool HasTrain => Session.QueryTrainAssemblies().Count > 0;
    private EntityId Loco => Session.Vehicles.First(vehicle => vehicle.DefinitionId == StaticTrainFixture.LocomotiveId).Id;
    private EntityId? Carriage => Session.Vehicles.FirstOrDefault(vehicle => vehicle.DefinitionId == StaticTrainFixture.CarriageId)?.Id;

    public void Advance()
    {
        if (_finished)
            return;
        try
        {
            _frames++;
            if (_resume)
                AdvanceResume();
            else if (HasTrainConfiguration())
                AdvanceTrain();
            else
                AdvanceEmpty();
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
    }

    private bool HasTrainConfiguration() =>
        Session.WorldSource?.ConfigurationId == WorldEditorConfigurations.StraightRailTrainId;

    private void AdvanceTrain()
    {
        switch (_phase)
        {
            case 0 when _frames >= SettleFrames:
                CheckFreeStart();
                Next();
                break;
            case 1:
                if (RouteThenFly())
                    Next();
                break;
            case 2:
                SelectFromList(ObservedObject.Vehicle(Loco));
                Next();
                break;
            case 3:
                if (ClickVehicle(Loco))
                    Next();
                break;
            case 4:
                if (ClickRail())
                    Next();
                break;
            case 5:
                SelectFromList(ObservedObject.Vehicle(Loco));
                if (Transition("focus", Key.F, WorldEditorCameraMode.Focus, Loco))
                    Next();
                break;
            case 6:
                RequireUnchanged("observation-only phases");
                _scene.Press("start");
                if (_scene.HistoryPrompt.Visible)
                    _scene.HistoryPrompt.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
                _scene.Press("run");
                Require(!Session.SimulationPaused && Session.QueryTrainAssemblies().Single().Running, "train did not start");
                Next();
                break;
            case 7:
                if (Transition("follow", Key.None, WorldEditorCameraMode.Follow, Loco))
                    Next();
                break;
            case 8:
                if (FollowWhileMoving(Loco, "OBSERVE_FOLLOW_RUNNING"))
                    Next();
                break;
            case 9:
                if (HoldPaused("OBSERVE_FOLLOW_PAUSED"))
                    Next();
                break;
            case 10:
                if (OrbitAndZoom(Loco))
                    Next();
                break;
            case 11:
                if (IgnoreTruncatedDrags(Loco))
                    Next();
                break;
            case 12:
                if (_frames == 1)
                    _scene.Press("run");
                SelectFromList(ObservedObject.Vehicle(Carriage!.Value));
                if (Transition("switch", Key.V, WorldEditorCameraMode.Follow, Carriage.Value))
                    Next();
                break;
            case 13:
                SelectFromList(ObservedObject.Vehicle(Loco));
                if (Transition("switch-back", Key.None, WorldEditorCameraMode.Follow, Loco))
                    Next();
                break;
            case 14:
                SplitWhileMoving();
                Next();
                break;
            case 15:
                if (_frames == 1)
                    CheckSplitContinuity();
                if (FollowWhileMoving(Loco, "OBSERVE_FOLLOW_AFTER_SPLIT"))
                    Next();
                break;
            case 16:
                ChangeActiveLocomotive();
                Next();
                break;
            case 17:
                SelectFromList(ObservedObject.Vehicle(Carriage!.Value));
                if (Transition("follow-carriage", Key.None, WorldEditorCameraMode.Follow, Carriage.Value))
                    Next();
                break;
            case 18:
                DestroyFollowed();
                Next();
                break;
            case 19 when _frames >= SettleFrames:
                CheckDestroyedInPlace();
                Next();
                break;
            case 20:
                if (ClickFragment())
                    Next();
                break;
            case 21:
                SaveAndFinish();
                break;
        }
    }

    private void AdvanceEmpty()
    {
        switch (_phase)
        {
            case 0 when _frames >= SettleFrames:
                CheckFreeStart();
                Require(Session.Vehicles.Count == 0, "empty configuration has vehicles");
                Require(View.Entries.All(entry => entry.Object.Kind == ObservedObjectKind.RailSegment), "empty list has non-segment entries");
                Next();
                break;
            case 1:
                if (RouteThenFly())
                    Next();
                break;
            case 2:
                if (ClickRail())
                    Next();
                break;
            case 3:
                SelectFromList(View.Entries[0].Object);
                if (Transition("focus", Key.F, WorldEditorCameraMode.Focus, null))
                    Next();
                break;
            case 4:
                if (_frames == 1)
                {
                    View.Press("follow");
                    Require(Observer.Mode == WorldEditorCameraMode.Focus, "follow on a segment changed the mode");
                    PressKey(Key.C, true, Key.Q);
                    PressKey(Key.C, false, Key.Q);
                    break;
                }
                if (_frames < 3)
                    break;
                Require(Observer.Mode == WorldEditorCameraMode.Free && Observer.Target is null, "free did not release the target");
                GD.Print($"OBSERVE_SEGMENT_FOLLOW_REFUSED mode={Observer.Mode} selection={Observer.Selection?.Describe()}");
                Next();
                break;
            case 5 when _frames >= SettleFrames:
                RequireUnchanged("empty observation");
                Next();
                break;
            case 6:
                if (RebuildFocusedCell(scheduled: false))
                    Next();
                break;
            case 7:
                if (RebuildFocusedCell(scheduled: true))
                    Next();
                break;
            case 8:
                if (ReplaceFollowedWorld(WorldReplacement.Reset))
                    Next();
                break;
            case 9:
                if (ReplaceFollowedWorld(WorldReplacement.Load))
                    Next();
                break;
            case 10:
                SaveAndFinish();
                break;
        }
    }

    private void AdvanceResume()
    {
        switch (_phase)
        {
            case 0 when _frames >= SettleFrames:
                Require(Session.SimulationPaused, "restored session is not paused");
                Require(Observer.Mode == WorldEditorCameraMode.Free && Observer.Selection is null && Observer.Target is null,
                    $"restored observation is not the default: {Observer.Describe()}");
                RequireCameraAt(WorldEditorScene.OverviewPose, "restored camera");
                GD.Print(
                    $"OBSERVE_RESTORED step={Session.SimulationStep} paused=true {Observer.Describe()} " +
                    $"fingerprint={WorldSaveAuthority.Fingerprint(Session)}");
                Finish();
                break;
        }
    }

    private void CheckFreeStart()
    {
        Require(Session.SimulationPaused && Session.SimulationStep == 0, "world editor did not start paused at step 0");
        Require(Observer.Mode == WorldEditorCameraMode.Free && Observer.Selection is null, $"start observation {Observer.Describe()}");
        RequireCameraAt(WorldEditorScene.OverviewPose, "start camera");
        RequireActionDisabled("parts");
        RequireActionDisabled("whole");
        if (HasTrain)
            Require(Session.SubmitEdit(new VoxelAddEdit(new WorldVoxelCoord(76, 0, 6), WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Status == "Added",
                "terrain fixture was not placed");
        _mark = Session.SimulationStep;
        _fingerprint = WorldSaveAuthority.Fingerprint(Session);
        GD.Print(
            $"OBSERVE_FREE vehicles={Session.Vehicles.Count} entries={View.Entries.Count} {Observer.Describe()} " +
            $"camera={_scene.Camera.GlobalPosition} step={Session.SimulationStep} paused=true");
    }

    /// <summary>Holds W through real input events; the free camera moves along its view while the clock stays put.</summary>
    private bool FlyForward()
    {
        if (_frames == 1)
        {
            _positions.Clear();
            _positions.Add(_scene.Camera.GlobalPosition);
            PressKey(Key.W, true);
            return false;
        }

        if (_frames < FlyFrames)
            return false;
        PressKey(Key.W, false);
        var moved = _scene.Camera.GlobalPosition - _positions[0];
        var view = (WorldEditorObservationView.To(WorldEditorScene.OverviewPose.Look) - WorldEditorObservationView.To(WorldEditorScene.OverviewPose.Position)).Normalized();
        var horizontal = new Vector3(view.X, 0, view.Z).Normalized();
        Require(moved.Length() > 0.01f, "W did not move the free camera");
        Require(moved.Normalized().Dot(horizontal) > 0.999f, $"free flight direction {moved}");
        RequireUnchanged("free flight");
        GD.Print($"OBSERVE_FREE_FLY moved={moved.Length():0.###} dir_dot={moved.Normalized().Dot(horizontal):0.####} step={Session.SimulationStep}");
        return true;
    }

    private bool RouteThenFly()
    {
        if (_routingFinished)
            return FlyForward();
        if (CheckInputRouting())
        {
            _routingFinished = true;
            _frames = 0;
        }
        return false;
    }

    /// <summary>Real GUI delivery, including a held movement key when a panel takes ownership.</summary>
    private bool CheckInputRouting()
    {
        var world = View.WorldRect.GetCenter();
        var panel = View.PanelRect.Position + new Vector2(8, 8);
        var field = _scene.SaveNameInput.GetGlobalRect().GetCenter();
        var screen = _scene.GetViewport().GetScreenTransform();
        void MouseObservation(Vector2 at)
        {
            var window = screen * at;
            RightButton(true, window);
            Motion(window, screen.BasisXform(new Vector2(20, 0)), MouseButtonMask.Right);
            RightButton(false, window);
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = window, GlobalPosition = window });
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = false, Position = window, GlobalPosition = window });
        }
        void Still(string context)
        {
            Require(Observer.Pose == _pose, context + " moved the camera");
            Require(Observer.Selection == _routingSelection, context + " changed selection");
            RequireUnchanged(context);
        }
        switch (_frames)
        {
            case 1:
                Click(world);
                break;
            case 3:
                Require(View.WorldHasInput, "world click did not acquire input");
                _pose = Observer.Pose;
                PressKey(Key.W, true);
                break;
            case 6:
                Require(Observer.Pose.Position != _pose.Position, "world input did not fly");
                Click(panel); // W is still held when the panel takes input.
                break;
            case 8:
                Require(!View.WorldHasInput, "panel click retained world input");
                _pose = Observer.Pose;
                _routingSelection = Observer.Selection;
                MouseObservation(panel);
                break;
            case 10:
                Still("panel input");
                PressKey(Key.W, false);
                Click(field);
                break;
            case 12:
                Require(_scene.GetViewport().GuiGetFocusOwner() == _scene.SaveNameInput, "text field did not acquire input");
                _pose = Observer.Pose;
                _routingSelection = Observer.Selection;
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.W, PhysicalKeycode = Key.W, Unicode = 'w', Pressed = true });
                MouseObservation(field);
                break;
            case 15:
                Still("text field input");
                Require(_scene.SaveNameInput.Text.Contains('w'), "text field did not receive the typed character");
                PressKey(Key.W, false);
                TapKey(Key.Escape); // Dismiss the LineEdit's ordinary right-click menu before toggling the console.
                break;
            case 17:
                TapKey(Key.Quoteleft);
                break;
            case 19:
                Require(Main.ConsoleOpen, "console did not open");
                _pose = Observer.Pose;
                _routingSelection = Observer.Selection;
                PressKey(Key.W, true);
                Click(world);
                MouseObservation(world);
                break;
            case 22:
                Still("console input");
                PressKey(Key.W, false);
                TapKey(Key.Quoteleft);
                break;
            case 24:
                Require(!Main.ConsoleOpen && !View.WorldHasInput, "closing console resumed world input");
                Click(world);
                break;
            case 26:
                Require(View.WorldHasInput, "fresh world click did not restore input");
                RequireUnchanged("input routing");
                GD.Print("OBSERVE_INPUT_ROUTING world=fly panel=isolated text=isolated console=isolated authority=unchanged");
                return true;
        }
        return false;
    }

    private void SelectFromList(ObservedObject item)
    {
        if (_frames != 1)
            return;
        var before = CameraPose();
        var index = View.Entries.Select(entry => entry.Object).ToList().IndexOf(item);
        Require(index >= 0, $"{item.Describe()} is not listed");
        View.SelectListIndex(index);
        Require(Observer.Selection == item, $"list selection is {Observer.Selection?.Describe()}");
        Require(CameraPose() == before, "list selection moved the camera");
        RequireInspectionBinding();
        GD.Print($"OBSERVE_LIST_SELECT {item.Describe()} camera_moved=false mode={Observer.Mode} inspection_lines={View.InspectionText.Split('\n').Length}");
    }

    /// <summary>Clicks the displayed body centre on screen; the domain ray against display poses must pick that vehicle.</summary>
    private bool ClickVehicle(EntityId vehicleId)
    {
        if (_frames == 1)
        {
            Observer.ClearSelection();
            _pose = Observer.Pose;
            Click(_scene.Camera.UnprojectPosition(BodyCenter(vehicleId)));
            return false;
        }

        if (_frames < 3)
            return false;
        if (_frames == 3)
        {
            var whole = ReceivedClick();
            Require(whole.Object == ObservedObject.Vehicle(vehicleId), "default click did not select whole vehicle");
            Require(Observer.Pose == _pose, "whole selection moved the camera");
            RequireActionDisabled("whole");
            View.Press("parts");
            Require(Observer.PartVehicleId == vehicleId && Observer.Selection?.Kind == ObservedObjectKind.VehiclePart, "explicit part entry failed");
            Click(_scene.Camera.UnprojectPosition(BodyCenter(vehicleId)));
            return false;
        }
        if (_frames < 5)
            return false;
        if (_frames == 5)
        {
            var partPick = ReceivedClick();
            var hitPart = Session.Vehicles.Single(vehicle => vehicle.Id == vehicleId).Parts.Single(part => part.Cell == partPick.VehicleHit!.Cell);
            Require(partPick.Object == ObservedObject.VehiclePart(vehicleId, hitPart.Id) && Observer.Selection == partPick.Object, "local click selected the wrong part");
            RequireInspectionBinding();
            RequireActionDisabled("parts");
            _partSelection = Observer.Selection;
            var otherVehicle = Session.Vehicles.First(vehicle => vehicle.Id != vehicleId);
            Click(_scene.Camera.UnprojectPosition(BodyCenter(otherVehicle.Id)));
            return false;
        }
        if (_frames < 7)
            return false;
        if (_frames == 7)
        {
            var otherPick = ReceivedClick();
            Require(otherPick.VehicleHit is { } other && other.VehicleId != vehicleId && other.Distance <= otherPick.WorldDistance,
                "out-of-level click did not hit another vehicle");
            Require(otherPick.Object is null && Observer.Selection == _partSelection, "other vehicle changed local selection");
            Click(_scene.Camera.UnprojectPosition(new Vector3(76.5f, 0.5f, 6.5f)));
            return false;
        }
        if (_frames < 9)
            return false;
        var pick = ReceivedClick();
        Require(pick.WorldCell is { } terrain && Session.Query(terrain).IsOccupied && !Session.Rails.TryGetOccupancy(terrain, out _) &&
            (pick.VehicleHit is null || pick.WorldDistance < pick.VehicleHit.Distance), "out-of-level click did not hit terrain");
        Require(pick.Object is null && Observer.Selection == _partSelection, "terrain changed local selection");
        Require(Observer.Pose == _pose, "world clicks moved the camera");
        View.Press("whole");
        Require(Observer.Selection == ObservedObject.Vehicle(vehicleId) && Observer.PartVehicleId is null, "return did not restore whole vehicle");
        RequireInspectionBinding();
        Require(Observer.Pose == _pose, "return moved the camera");
        RequireUnchanged("selection hierarchy");
        GD.Print("OBSERVE_SELECTION_HIERARCHY whole=vehicle part=stable-id return=whole camera_moved=false");
        GD.Print(
            $"OBSERVE_CLICK_VEHICLE picked={Observer.Selection!.Value.Describe()} part={_partSelection!.Value.PartId!.Value.Value} out_of_level=ignored camera_moved=false");
        return true;
    }

    /// <summary>Clicks a published rail cell; the Godot physics hit must be a cell the domain also reports as a hit.</summary>
    private bool ClickRail()
    {
        var rail = StraightRailFixture.LeftRailSample;
        var sample = new Vector3(RailClickX + 0.5f, rail.Y + 0.5f, rail.Z + 0.5f);
        if (_frames == 1)
        {
            _pose = Observer.Pose;
            Click(_scene.Camera.UnprojectPosition(sample));
            return false;
        }

        if (_frames < 3)
            return false;
        var pick = ReceivedClick();
        var cell = pick.WorldCell ?? throw new InvalidOperationException($"rail click hit no world collider (object {pick.Object?.Describe()})");
        var domain = Session.QueryCollision(cell).Status;
        Require(pick.Object?.Kind == ObservedObjectKind.RailSegment && Session.Rails.TryGetOccupancy(cell, out var occupancy) &&
            occupancy.Supports.Any(support => support.SegmentId == pick.Object.Value.Id), $"rail click picked {pick.Object?.Describe()}");
        Require(domain == VoxelCollisionQueryStatus.Hit, $"Godot hit {cell} but domain collision is {domain}");
        Require(_scene.Colliders.QueryPublishedCellInterior(_scene.GetWorld3D(), cell), "published collider interior disagrees with the ray hit");
        Require(Observer.Pose == _pose, "rail click moved the camera");
        RequireInspectionBinding();
        GD.Print(
            $"OBSERVE_CLICK_RAIL cell={cell.X},{cell.Y},{cell.Z} godot_hit=true domain={domain} " +
            $"voxel={Session.Query(cell).Voxel?.DefinitionId.Value} camera_moved=false");
        return true;
    }

    /// <summary>
    /// Starts focus / follow through a key or panel button and samples every frame until it settles: the camera must
    /// ease from where it was, without a first-frame jump, and end on the target.
    /// </summary>
    private bool Transition(string name, Key key, WorldEditorCameraMode mode, EntityId? vehicleId)
    {
        if (_frames == 1)
            return false;
        if (_frames == 2)
        {
            _pose = Observer.Pose;
            _looks.Clear();
            _looks.Add(WorldEditorObservationView.To(_pose.Look));
            if (key == Key.None)
                View.Press(mode == WorldEditorCameraMode.Focus ? "focus" : "follow");
            else
            {
                var physical = key == Key.F ? Key.E : Key.W;
                PressKey(key, true, physical);
                PressKey(key, false, physical);
            }

            return false;
        }

        _looks.Add(WorldEditorObservationView.To(Observer.Pose.Look));
        if (_frames > TransitionLimitFrames)
            throw new InvalidOperationException($"{name} transition did not settle");
        if (Observer.Mode != mode || Observer.Transitioning)
            return false;

        var start = _looks[0];
        var end = _looks[^1];
        var travel = (end - start).Length();
        var steps = _looks.Zip(_looks.Skip(1), (a, b) => (b - a).Length()).SkipWhile(step => step == 0).ToArray();
        var first = steps.Length == 0 ? 0 : steps[0];
        var largest = steps.Length == 0 ? 0 : steps.Max();
        var target = vehicleId is { } id ? BodyCenter(id) : WorldEditorObservationView.To(Observer.Center(Observer.Target!.Value, Session.QueryDisplayPoses()));
        Require((end - target).Length() < Tolerance, $"{name} settled on {end}, target {target}");
        Require(steps.Length >= 3, $"{name} completed in {steps.Length} frames; not a transition");
        Require(travel < 1e-3f || first <= 0.2f * travel + 0.5f, $"{name} first frame jumped {first} of {travel}");
        GD.Print(
            $"OBSERVE_TRANSITION {name} mode={mode} target={Observer.Target?.Describe()} frames={steps.Length} " +
            $"travel={travel:0.###} first_step={first:0.####} largest_step={largest:0.####} " +
            $"step={Session.SimulationStep} paused={Session.SimulationPaused.ToString().ToLowerInvariant()}");
        return true;
    }

    /// <summary>
    /// While the train runs, every frame the camera must look at the centre of the body node Godot is drawing and keep
    /// the same offset in that body's frame.
    /// </summary>
    private bool FollowWhileMoving(EntityId vehicleId, string marker)
    {
        var visual = _scene.Composites.VisualFor(vehicleId) ?? throw new InvalidOperationException($"no visual for {vehicleId}");
        var body = visual.GlobalTransform;
        var center = BodyCenter(vehicleId);
        var look = WorldEditorObservationView.To(Observer.Pose.Look);
        var camera = _scene.Camera.GlobalPosition;
        Require((look - center).Length() < Tolerance, $"{marker} look {look} is not the drawn body centre {center}");
        var offset = body.Basis.Transposed() * (camera - center);
        if (_frames == 1)
        {
            if (Session.SimulationPaused)
                _scene.Press("run");
            _bodyOffset = offset;
            _positions.Clear();
            _mark = Session.SimulationStep;
            _interpolatedFrames = 0;
        }

        Require((offset - _bodyOffset).Length() < 2e-3f, $"{marker} body-frame offset drifted {offset} vs {_bodyOffset}");
        _positions.Add(camera);
        var authority = Session.Vehicles.Single(vehicle => vehicle.Id == vehicleId).Transform;
        var local = WorldEditorObserver.LocalCenter(Session.Vehicles.Single(vehicle => vehicle.Id == vehicleId));
        var step = authority.TransformPoint(local.X, local.Y, local.Z);
        if ((new Vector3((float)step.X, (float)step.Y, (float)step.Z) - center).Length() > 1e-4f)
            _interpolatedFrames++;
        if (_frames < FollowFrames)
            return false;

        var travelled = (_positions[^1] - _positions[0]).Length();
        var largest = _positions.Zip(_positions.Skip(1), (a, b) => (b - a).Length()).Max();
        Require(Session.SimulationStep > _mark, $"{marker} clock did not advance while following");
        Require(travelled > 0.01f, $"{marker} camera did not move with the train");
        _scene.Press("pause");
        GD.Print(
            $"{marker} vehicle={vehicleId} frames={FollowFrames} steps={Session.SimulationStep - _mark} travelled={travelled:0.###} " +
            $"largest_frame_move={largest:0.####} interpolated_frames={_interpolatedFrames} body_offset={_bodyOffset} " +
            $"heading={FrameHeading(vehicleId)}");
        return true;
    }

    private bool HoldPaused(string marker)
    {
        if (_frames == 1)
        {
            Require(Session.SimulationPaused, "not paused before the paused hold");
            _mark = Session.SimulationStep;
            _positions.Clear();
            _positions.Add(_scene.Camera.GlobalPosition);
            return false;
        }

        _positions.Add(_scene.Camera.GlobalPosition);
        if (_frames < HoldFrames)
            return false;
        Require(Session.SimulationPaused && Session.SimulationStep == _mark, "observation moved the paused clock");
        Require(_positions.All(position => position == _positions[0]), "camera moved while paused and following a stopped body");
        GD.Print($"{marker} frames={HoldFrames} step={_mark} camera_constant=true {Observer.Describe()}");
        return true;
    }

    /// <summary>
    /// Right-drag and wheel through real mouse events while following a paused body: the camera orbits and closes in,
    /// still looking at the same body centre, and the clock does not move.
    /// </summary>
    private bool OrbitAndZoom(EntityId vehicleId)
    {
        var center = BodyCenter(vehicleId);
        if (_frames == 1)
        {
            _positions.Clear();
            _positions.Add(_scene.Camera.GlobalPosition);
            _mark = Session.SimulationStep;
            var size = _scene.GetViewport().GetVisibleRect().Size;
            var screen = _scene.GetViewport().GetScreenTransform();
            var at = screen * (size * 0.5f);
            var drag = screen.BasisXform(new Vector2(OrbitDragPixels, 0));
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = at, GlobalPosition = at });
            Input.ParseInputEvent(new InputEventMouseMotion { Position = at + drag, GlobalPosition = at + drag, Relative = drag, ButtonMask = MouseButtonMask.Right });
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = at + drag, GlobalPosition = at + drag });
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = at, GlobalPosition = at, Factor = 1 });
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = false, Position = at, GlobalPosition = at, Factor = 1 });
            return false;
        }

        if (_frames < 3)
            return false;
        var camera = _scene.Camera.GlobalPosition;
        var before = (_positions[0] - center).Length();
        var after = (camera - center).Length();
        var turned = new Vector2(_positions[0].X - center.X, _positions[0].Z - center.Z).AngleTo(new Vector2(camera.X - center.X, camera.Z - center.Z));
        Require((WorldEditorObservationView.To(Observer.Pose.Look) - center).Length() < Tolerance, "orbit moved the look point off the body");
        Require(Math.Abs(before - after - (float)WorldEditorObservationView.ZoomStep) < 2e-3f, $"wheel changed distance {before} -> {after}");
        Require(Math.Abs(turned) > 0.01f, "right-drag did not orbit the camera");
        Require(Session.SimulationPaused && Session.SimulationStep == _mark, "orbit moved the clock");
        GD.Print(
            $"OBSERVE_ORBIT_ZOOM vehicle={vehicleId} yaw_turn={turned:0.####} distance={before:0.###}->{after:0.###} " +
            $"step={Session.SimulationStep} paused=true");
        return true;
    }

    /// <summary>
    /// Right-drags whose release the view never sees, while following a paused body: a release made while the console
    /// is open, a release routed to the observation panel because a left press on it took GUI mouse focus, and a
    /// window focus-out notification mid-drag. Mouse and key events are real parsed input. None may leave a drag
    /// behind that turns the camera on a later motion; a fresh right-drag still orbits.
    /// </summary>
    private bool IgnoreTruncatedDrags(EntityId vehicleId)
    {
        var viewport = _scene.GetViewport();
        var screen = viewport.GetScreenTransform();
        var size = viewport.GetVisibleRect().Size;
        var world = screen * (size * 0.5f);
        var panel = screen * (View.PanelRect.Position + new Vector2(8, 8));
        var drag = screen.BasisXform(new Vector2(OrbitDragPixels, 0));
        switch (_frames)
        {
            case 1:
                Require(Observer.Mode == WorldEditorCameraMode.Follow && Session.SimulationPaused, $"truncated drags need a paused follow: {Observer.Describe()}");
                RightButton(true, world);
                break;
            case 3:
                TapKey(Key.Quoteleft);
                break;
            case 5:
                Require(Main.ConsoleOpen, "~ did not open the console");
                RightButton(false, world);
                break;
            case 7:
                TapKey(Key.Quoteleft);
                break;
            case 9:
                Require(!Main.ConsoleOpen, "~ did not close the console");
                Require(!Input.IsMouseButtonPressed(MouseButton.Right), "right button still down after its release");
                _pose = Observer.Pose;
                Motion(world, drag, 0);
                break;
            case 11:
                RequireStill("a motion after a release made in the console");
                RightButton(true, world);
                break;
            case 13:
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = panel, GlobalPosition = panel });
                break;
            case 15:
                RightButton(false, world);
                Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = panel, GlobalPosition = panel });
                break;
            case 17:
                Require(!Input.IsMouseButtonPressed(MouseButton.Right), "right button still down after the panel took its release");
                Motion(world, drag, 0);
                break;
            case 19:
                RequireStill("a motion after the panel took the release");
                RightButton(true, world);
                break;
            case 21:
                _scene.GetTree().Root.PropagateNotification((int)Node.NotificationWMWindowFocusOut);
                Motion(world, drag, MouseButtonMask.Right);
                break;
            case 23:
                RequireStill("a held-button motion after the window lost focus");
                RightButton(false, world);
                break;
            case 25:
                RightButton(true, world);
                Motion(world, drag, MouseButtonMask.Right);
                RightButton(false, world + drag);
                break;
            case 27:
                var center = BodyCenter(vehicleId);
                var before = WorldEditorObservationView.To(_pose.Position);
                var camera = _scene.Camera.GlobalPosition;
                var turned = new Vector2(before.X - center.X, before.Z - center.Z).AngleTo(new Vector2(camera.X - center.X, camera.Z - center.Z));
                Require(Math.Abs(turned) > 0.01f, "right-drag after the truncated drags did not orbit");
                Require(Session.SimulationPaused && Session.SimulationStep == _mark, "truncated drags moved the clock");
                GD.Print(
                    $"OBSERVE_TRUNCATED_DRAGS_IGNORED console_release=still panel_release=still focus_out=still " +
                    $"fresh_drag_yaw_turn={turned:0.####} step={Session.SimulationStep}");
                return true;
        }

        return false;
    }

    /// <summary>
    /// Focuses an ordinary world voxel, then removes and re-adds the same definition there through the console before
    /// the next frame, either immediately or as two edits due at one step. The old reference must be lost with the
    /// camera free in place, and the rebuilt voxel must not be selected.
    /// </summary>
    private bool RebuildFocusedCell(bool scheduled)
    {
        var probe = WorldEditorSequences.EditProbe;
        var cell = $"{probe.X} {probe.Y} {probe.Z}";
        var add = $"voxel add {cell} {WorldEditorSequences.EditVoxelId.Value} {VoxelOrientation.Identity}";
        if (_frames == 1)
        {
            if (Session.Query(probe).Voxel is null)
                RunConsole(add);
            RunConsole($"observe select cell {cell}");
            RunConsole("observe focus");
            Require(Observer.Mode == WorldEditorCameraMode.Focus && Observer.Target?.Cell == probe, $"cell focus: {Observer.Describe()}");
            _rebuilt = false;
            return false;
        }

        if (!_rebuilt)
        {
            Require(_frames < TransitionLimitFrames, "cell focus transition never finished");
            if (Observer.Transitioning)
                return false;
            _pose = Observer.Pose;
            _positions.Clear();
            _positions.Add(_scene.Camera.GlobalPosition);
            var notices = Observer.Notices.Count;
            if (scheduled)
            {
                var step = Session.SimulationStep + 1;
                RunConsole($"voxel remove {cell} at {step} confirm-end-history");
                RunConsole($"{add} at {step}");
                Require(Observer.Selection is not null && Observer.Notices.Count == notices, "queueing the rebuild dropped the selection");
                RunConsole("rail step 1");
            }
            else
            {
                RunConsole($"voxel remove {cell}");
                RunConsole(add);
            }

            _rebuilt = true;
            _frames = 1;
            return false;
        }

        if (_frames < 3)
            return false;
        Require(Session.Query(probe).Voxel?.DefinitionId == WorldEditorSequences.EditVoxelId, "rebuild did not leave the same definition");
        Require(Observer.Mode == WorldEditorCameraMode.Free && Observer.Target is null && Observer.Selection is null,
            $"rebuilt cell kept {Observer.Describe()}");
        Require(Observer.LastNotice is { Kind: WorldEditorNoticeKind.TargetLost, Mode: WorldEditorCameraMode.Focus } lost && lost.Object.Cell == probe,
            $"notice {Observer.LastNotice?.Describe()}");
        Require(Observer.Pose.Position == _pose.Position && (_scene.Camera.GlobalPosition - _positions[0]).Length() < Tolerance,
            "camera moved when the rebuilt cell was lost");
        RunConsole("observe");
        GD.Print(
            $"OBSERVE_REBUILD_LOST {(scheduled ? "scheduled" : "immediate")} {Observer.LastNotice!.Describe()} " +
            $"camera_moved=false {Observer.Describe()} step={Session.SimulationStep}");
        return true;
    }

    /// <summary>
    /// Follows the manufactured vehicle, then replaces the whole world through the console so a different vehicle takes the same id:
    /// <c>composite reset bridge</c> over a followed hollow shell, and after a load of a missing save that must keep
    /// observing, <c>voxel load</c> of the hollow save over a followed bridge. Either replacement drops the old
    /// references with the camera in place. The world editor save taken first is loaded back at the end.
    /// </summary>
    private bool ReplaceFollowedWorld(WorldReplacement replacement)
    {
        if (_frames == 1)
        {
            if (replacement == WorldReplacement.Reset)
            {
                _worldEditorSave = SaveCopy("Before replacement");
                RunConsoleAndFinishChange("composite reset hollow");
                _hollowSave = SaveCopy("Hollow");
            }

            var vehicle = Session.Vehicles.Single().Id;
            RunConsole($"observe select vehicle {vehicle.Value}");
            RunConsole("observe follow");
            Require(Observer.Mode == WorldEditorCameraMode.Follow && Observer.Target == ObservedObject.Vehicle(vehicle), $"follow: {Observer.Describe()}");
            _rebuilt = false;
            return false;
        }

        if (!_rebuilt)
        {
            Require(_frames < TransitionLimitFrames, "follow transition never finished");
            if (Observer.Transitioning)
                return false;
            _pose = Observer.Pose;
            _positions.Clear();
            _positions.Add(_scene.Camera.GlobalPosition);
            var followed = Observer.Target!.Value;
            _replacedDefinition = Session.QueryComposite(followed.Id)!.DefinitionId.Value;
            if (replacement == WorldReplacement.Reset)
            {
                RunConsoleAndFinishChange("composite reset bridge");
            }
            else
            {
                var notices = Observer.Notices.Count;
                var fingerprint = WorldSaveAuthority.Fingerprint(Session);
                RunConsoleAndFinishChange($"voxel load {Guid.NewGuid():D}");
                Require(WorldSaveAuthority.Fingerprint(Session) == fingerprint, "a failed load changed the session");
                Require(Observer.Mode == WorldEditorCameraMode.Follow && Observer.Target == followed && Observer.Selection == followed &&
                        Observer.Notices.Count == notices, $"a failed load changed observation: {Observer.Describe()}");
                GD.Print($"OBSERVE_FAILED_LOAD_KEEP {Observer.Describe()} notices={Observer.Notices.Count}");
                RunConsoleAndFinishChange($"voxel load {_hollowSave:D}");
            }

            var reused = Session.QueryComposite(followed.Id)?.DefinitionId.Value;
            Require(reused is not null && reused != _replacedDefinition, $"{followed.Describe()} was not reused by a different vehicle: {reused}");
            _replacedDefinition += "->" + reused;
            _rebuilt = true;
            _frames = 1;
            return false;
        }

        if (_frames < 3)
            return false;
        Require(Observer.Mode == WorldEditorCameraMode.Free && Observer.Target is null && Observer.Selection is null,
            $"replaced world kept {Observer.Describe()}");
        Require(Observer.LastNotice is { Kind: WorldEditorNoticeKind.TargetLost, Mode: WorldEditorCameraMode.Follow } lost && lost.Replacement == replacement,
            $"notice {Observer.LastNotice?.Describe()}");
        Require(Observer.Pose.Position == _pose.Position && (_scene.Camera.GlobalPosition - _positions[0]).Length() < Tolerance,
            "camera moved when the world was replaced");
        RunConsole("observe");
        GD.Print(
            $"OBSERVE_WORLD_REPLACED {replacement.ToString().ToLowerInvariant()} {Observer.LastNotice!.Describe()} " +
            $"camera_moved=false {Observer.Describe()} same_id_definition={_replacedDefinition} notice_zh=\"{View.NoticeText}\"");
        if (replacement == WorldReplacement.Load)
        {
            RunConsoleAndFinishChange($"voxel load {_worldEditorSave:D}");
            Require(Session.WorldSource?.ConfigurationId == WorldEditorConfigurations.StraightRailEmptyId, "the world editor save did not load back");
        }

        return true;
    }

    private Guid SaveCopy(string name)
    {
        var saved = Session.SaveAs(name);
        Require(saved.Succeeded, $"save copy {name} failed: {saved.Status}");
        return saved.SaveId ?? throw new InvalidOperationException($"save copy {name} did not save");
    }

    private void RequireStill(string what)
    {
        Require(Observer.Pose == _pose, $"{what} turned the camera: {_pose} -> {Observer.Pose}");
        GD.Print($"OBSERVE_TRUNCATED_DRAG_STILL after={what}");
    }

    private static void RightButton(bool pressed, Vector2 window) =>
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = pressed, Position = window, GlobalPosition = window });

    private static void Motion(Vector2 window, Vector2 relative, MouseButtonMask mask) =>
        Input.ParseInputEvent(new InputEventMouseMotion { Position = window + relative, GlobalPosition = window + relative, Relative = relative, ButtonMask = mask });

    private static void TapKey(Key key)
    {
        PressKey(key, true);
        PressKey(key, false);
    }

    private void SplitWhileMoving()
    {
        Require(!Session.SimulationPaused, "split should happen while the train runs");
        var loco = Loco;
        _positions.Clear();
        _positions.Add(_scene.Camera.GlobalPosition);
        RunConsole($"composite damage {loco} {TrainCells.RearCoupler} 100");
        var assemblies = Session.QueryTrainAssemblies();
        Require(assemblies.Count == 2, $"coupler damage left {assemblies.Count} assemblies");
        Observer.Reconcile();
        Require(Observer.Target == ObservedObject.Vehicle(loco) && Observer.Mode == WorldEditorCameraMode.Follow, $"split changed the target: {Observer.Describe()}");
        GD.Print($"OBSERVE_SPLIT_KEEP assemblies={assemblies.Count} step={Session.SimulationStep} {Observer.Describe()} notices={Observer.Notices.Count}");
    }

    private void CheckSplitContinuity()
    {
        var jump = (_scene.Camera.GlobalPosition - _positions[0]).Length();
        Require(jump < 0.5f, $"camera jumped {jump} across the split");
        GD.Print($"OBSERVE_SPLIT_CONTINUITY camera_frame_move={jump:0.####} target={Observer.Target?.Describe()}");
    }

    private void ChangeActiveLocomotive()
    {
        var loco = Loco;
        RunConsole($"composite damage {loco} {TrainCells.Control} 100");
        var owner = Session.QueryTrainAssemblies().Single(item => item.VehicleIds.Contains(loco));
        Require(owner.ActiveLocomotiveId is null, $"control damage kept active locomotive {owner.ActiveLocomotiveId}");
        Observer.Reconcile();
        Require(Observer.Target == ObservedObject.Vehicle(loco) && Observer.Selection == ObservedObject.Vehicle(loco),
            $"active-locomotive change moved the target: {Observer.Describe()}");
        GD.Print($"OBSERVE_ACTIVE_LOCOMOTIVE_CHANGE_KEEP active_locomotive=none {Observer.Describe()} notices={Observer.Notices.Count}");
    }

    private void DestroyFollowed()
    {
        var carriage = Carriage!.Value;
        Require(Observer.Target == ObservedObject.Vehicle(carriage), "not following the carriage before destruction");
        if (!Session.SimulationPaused)
            _scene.Press("pause");
        _pose = Observer.Pose;
        _positions.Clear();
        _positions.Add(_scene.Camera.GlobalPosition);
        _lastCarriageCenter = BodyCenter(carriage);
        _mark = Session.SimulationStep;
        RunConsole($"composite damage {carriage} {TrainCells.CarriageCore} 100");
        Require(Session.Vehicles.All(vehicle => vehicle.Id != carriage), "carriage core damage did not remove the vehicle");
        GD.Print($"OBSERVE_DESTROY vehicle={carriage} vehicles={Session.Vehicles.Count} step={Session.SimulationStep}");
    }

    private void CheckDestroyedInPlace()
    {
        var camera = _scene.Camera.GlobalPosition;
        Require(Observer.Mode == WorldEditorCameraMode.Free && Observer.Target is null && Observer.Selection is null,
            $"destroyed target left {Observer.Describe()}");
        Require(Observer.LastNotice is { Kind: WorldEditorNoticeKind.TargetLost }, "no target-lost notice");
        Require((camera - _positions[0]).Length() < Tolerance, $"camera jumped from {_positions[0]} to {camera}");
        Require(Observer.Pose.Position == _pose.Position, "observer position changed on target loss");
        var before = (WorldEditorObservationView.To(_pose.Look) - WorldEditorObservationView.To(_pose.Position)).Normalized();
        var facing = -_scene.Camera.GlobalBasis.Z;
        Require(facing.Dot(before) > 0.9999f, $"camera turned on target loss: {facing} vs {before}");
        Require(Session.SimulationStep == _mark, "target loss moved the clock");
        RunConsole("observe");
        GD.Print(
            $"OBSERVE_TARGET_LOST {Observer.LastNotice!.Describe()} camera_moved=false {Observer.Describe()} " +
            $"notice_zh=\"{View.NoticeText}\"");
    }

    /// <summary>After destruction the carriage's surviving parts are world fragments; a click must hit their colliders.</summary>
    private bool ClickFragment()
    {
        if (_frames == 1)
        {
            _pose = Observer.Pose;
            Click(_scene.Camera.UnprojectPosition(_lastCarriageCenter));
            return false;
        }

        if (_frames < 3)
            return false;
        var pick = ReceivedClick();
        var cell = pick.WorldCell ?? throw new InvalidOperationException("fragment click hit no world collider");
        var voxel = Session.Query(cell).Voxel;
        Require(voxel is not null, $"Godot hit {cell} but the domain cell is empty");
        Require(voxel!.RuntimeId is not null, $"click at the destroyed carriage hit {cell} {voxel.DefinitionId.Value}, not a detached fragment");
        Require(Session.QueryCollision(cell).Status == VoxelCollisionQueryStatus.Hit, $"domain collision at {cell}");
        Require(Observer.Pose == _pose, "fragment click moved the camera");
        GD.Print(
            $"OBSERVE_CLICK_FRAGMENT cell={cell.X},{cell.Y},{cell.Z} runtime={voxel!.RuntimeId?.Value.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
            $"selection={Observer.Selection?.Describe()} godot_hit=true domain=Hit");
        return true;
    }

    private void SaveAndFinish()
    {
        var before = WorldSaveAuthority.Fingerprint(Session);
        var saved = _scene.SaveWorld();
        Require(saved.Status == WorldSaveStatus.Saved, $"save status {saved.Status}");
        var path = new FileWorldSaveStore(Main.IsolatedUserDataDirectory).PathFor(saved.SaveId!.Value);
        var json = File.ReadAllText(path);
        foreach (var key in new[] { "observ", "camera", "select", "follow", "focus" })
            Require(!json.Contains(key, StringComparison.OrdinalIgnoreCase), $"save mentions {key}");
        Require(WorldSaveAuthority.Fingerprint(Session) == before, "saving changed authority");
        GD.Print(
            $"OBSERVE_SAVED id={saved.SaveId} step={Session.SimulationStep} {Observer.Describe()} " +
            $"fingerprint={before}");
        Finish();
    }

    private void RequireUnchanged(string what)
    {
        Require(Session.SimulationPaused && Session.SimulationStep == _mark, $"{what} moved the clock");
        Require(WorldSaveAuthority.Fingerprint(Session) == _fingerprint, $"{what} changed authority");
    }

    private void RequireCameraAt(ObservationCameraPose pose, string what)
    {
        var camera = _scene.Camera;
        var position = WorldEditorObservationView.To(pose.Position);
        var direction = (WorldEditorObservationView.To(pose.Look) - position).Normalized();
        Require((camera.GlobalPosition - position).Length() < Tolerance, $"{what} at {camera.GlobalPosition}");
        Require((-camera.GlobalBasis.Z).Dot(direction) > 0.9999f, $"{what} looks along {-camera.GlobalBasis.Z}");
    }

    private (Vector3 Position, Vector3 Look) CameraPose() =>
        (_scene.Camera.GlobalPosition, WorldEditorObservationView.To(Observer.Pose.Look));

    private Vector3 BodyCenter(EntityId vehicleId)
    {
        var visual = _scene.Composites.VisualFor(vehicleId) ?? throw new InvalidOperationException($"no visual for {vehicleId}");
        var local = WorldEditorObserver.LocalCenter(Session.Vehicles.Single(vehicle => vehicle.Id == vehicleId));
        return visual.GlobalTransform * WorldEditorObservationView.To(local);
    }

    private string FrameHeading(EntityId vehicleId) =>
        WorldEditorObserver.VehicleFrame(Session, Session.QueryDisplayPoses(), vehicleId)?.Heading.ToString() ?? "none";

    private void RunConsole(string line)
    {
        Main.GetChildren().OfType<GameConsole>().Single().SubmitLine(line);
        GD.Print($"OBSERVE_CONSOLE {line}");
    }

    private void RunConsoleAndFinishChange(string line)
    {
        RunConsole(line);
        WorldChangeSmoke.Finish(Session);
    }

    private static void PressKey(Key key, bool pressed, Key? physical = null) =>
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = physical ?? key, Pressed = pressed });

    private void RequireInspectionBinding()
    {
        var inspection = WorldEditorInspector.Inspect(Session, Observer.Selection!.Value)!;
        Require(View.InspectionText.EndsWith(string.Join('\n', inspection.LinesZh()), StringComparison.Ordinal),
            "inspector panel is not bound to the selected object's facts");
    }

    private void RequireActionDisabled(string action)
    {
        var refused = false;
        try { View.Press(action); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused, $"{action} was enabled outside its selection level");
    }

    /// <summary>The pick produced by the last synthesized click, which must have arrived at the aimed viewport point.</summary>
    private WorldEditorPick ReceivedClick()
    {
        var pick = View.LastPick ?? throw new InvalidOperationException("click did not reach the observation view");
        Require((pick.Screen - _clickTarget).Length() < 1f, $"click arrived at {pick.Screen}, aimed at {_clickTarget}");
        return pick;
    }

    private void Click(Vector2 screen)
    {
        _clickTarget = screen;
        var size = _scene.GetViewport().GetVisibleRect().Size;
        Require(screen.X >= 0 && screen.Y >= 0 && screen.X < size.X && screen.Y < size.Y, $"click target {screen} is off screen {size}");
        var window = _scene.GetViewport().GetScreenTransform() * screen;
        GD.Print($"OBSERVE_CLICK viewport_point={screen.X:0.#},{screen.Y:0.#} window_point={window.X:0.##},{window.Y:0.##} viewport={size.X:0}x{size.Y:0}");
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = window, GlobalPosition = window });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = window, GlobalPosition = window });
    }

    private void Next()
    {
        _phase++;
        _frames = 0;
    }

    private void Finish()
    {
        _finished = true;
        GD.Print("OBSERVE_SMOKE_DONE");
        _scene.GetTree().Quit();
    }

    private void Fail(string reason)
    {
        _finished = true;
        GD.PrintErr($"OBSERVE_SMOKE_FAILED phase={_phase} frame={_frames} {reason}");
        _scene.GetTree().Quit(1);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }

    private static class TrainCells
    {
        public const string RearCoupler = "7 1 1";
        public const string Control = "3 0 1";
        public const string CarriageCore = "1 0 1";
    }
}
