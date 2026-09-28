using System.Globalization;
using Godot;
using Train.Domain.Composites;
using Train.Domain.Playground;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train;

/// <summary>
/// Headless observation entrance: drives the real Playground camera, panel, keys, clicks and console, and checks
/// that observation reads the displayed bodies while never pausing, advancing or editing the session.
/// </summary>
internal sealed class PlaygroundObservationSmoke
{
    public const string ModeEnvironmentVariable = "TRAIN_PLAYGROUND_SMOKE_MODE";
    public const string ModeValue = "observation";

    private const int SettleFrames = 4;
    private const int HoldFrames = 10;
    private const int FlyFrames = 12;
    private const int FollowFrames = 40;
    private const int TransitionLimitFrames = 600;
    private const float Tolerance = 1e-3f;
    private const int RailClickX = 90;

    private readonly PlaygroundScene _scene;
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
    private bool _finished;

    public PlaygroundObservationSmoke(PlaygroundScene scene, string? saveId)
    {
        _scene = scene;
        _resume = Guid.TryParse(saveId, out _);
    }

    public static bool Requested() =>
        string.Equals(System.Environment.GetEnvironmentVariable(ModeEnvironmentVariable), ModeValue, StringComparison.Ordinal);

    private VoxelDevelopmentSession Session => _scene.Session!;
    private PlaygroundObservationView View => _scene.Observation!;
    private PlaygroundObserver Observer => View.Observer;
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
        Session.WorldSource?.ConfigurationId == PlaygroundConfigurations.StraightRailTrainId;

    private void AdvanceTrain()
    {
        switch (_phase)
        {
            case 0 when _frames >= SettleFrames:
                CheckFreeStart();
                Next();
                break;
            case 1:
                if (FlyForward())
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
                if (Transition("focus", Key.F, PlaygroundCameraMode.Focus, Loco))
                    Next();
                break;
            case 6:
                RequireUnchanged("observation-only phases");
                _scene.Press("start");
                _scene.Press("run");
                Require(!Session.SimulationPaused && Session.QueryTrainAssemblies().Single().Running, "train did not start");
                Next();
                break;
            case 7:
                if (Transition("follow", Key.None, PlaygroundCameraMode.Follow, Loco))
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
                if (_frames == 1)
                    _scene.Press("run");
                SelectFromList(ObservedObject.Vehicle(Carriage!.Value));
                if (Transition("switch", Key.V, PlaygroundCameraMode.Follow, Carriage.Value))
                    Next();
                break;
            case 11:
                SelectFromList(ObservedObject.Vehicle(Loco));
                if (Transition("switch-back", Key.None, PlaygroundCameraMode.Follow, Loco))
                    Next();
                break;
            case 12:
                SplitWhileMoving();
                Next();
                break;
            case 13:
                if (_frames == 1)
                    CheckSplitContinuity();
                if (FollowWhileMoving(Loco, "OBSERVE_FOLLOW_AFTER_SPLIT"))
                    Next();
                break;
            case 14:
                ChangeActiveLocomotive();
                Next();
                break;
            case 15:
                SelectFromList(ObservedObject.Vehicle(Carriage!.Value));
                if (Transition("follow-carriage", Key.None, PlaygroundCameraMode.Follow, Carriage.Value))
                    Next();
                break;
            case 16:
                DestroyFollowed();
                Next();
                break;
            case 17 when _frames >= SettleFrames:
                CheckDestroyedInPlace();
                Next();
                break;
            case 18:
                if (ClickFragment())
                    Next();
                break;
            case 19:
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
                if (FlyForward())
                    Next();
                break;
            case 2:
                if (ClickRail())
                    Next();
                break;
            case 3:
                SelectFromList(View.Entries[0].Object);
                if (Transition("focus", Key.F, PlaygroundCameraMode.Focus, null))
                    Next();
                break;
            case 4:
                View.Press("follow");
                Require(Observer.Mode == PlaygroundCameraMode.Focus, "follow on a segment changed the mode");
                View.Press("free");
                Require(Observer.Mode == PlaygroundCameraMode.Free && Observer.Target is null, "free did not release the target");
                GD.Print($"OBSERVE_SEGMENT_FOLLOW_REFUSED mode={Observer.Mode} selection={Observer.Selection?.Describe()}");
                Next();
                break;
            case 5 when _frames >= SettleFrames:
                RequireUnchanged("empty observation");
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
                Require(Observer.Mode == PlaygroundCameraMode.Free && Observer.Selection is null && Observer.Target is null,
                    $"restored observation is not the default: {Observer.Describe()}");
                RequireCameraAt(PlaygroundScene.OverviewPose, "restored camera");
                GD.Print(
                    $"OBSERVE_RESTORED step={Session.SimulationStep} paused=true {Observer.Describe()} " +
                    $"fingerprint={WorldSaveAuthority.Fingerprint(Session)}");
                Finish();
                break;
        }
    }

    private void CheckFreeStart()
    {
        Require(Session.SimulationPaused && Session.SimulationStep == 0, "Playground did not start paused at step 0");
        Require(Observer.Mode == PlaygroundCameraMode.Free && Observer.Selection is null, $"start observation {Observer.Describe()}");
        RequireCameraAt(PlaygroundScene.OverviewPose, "start camera");
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
        var view = (PlaygroundObservationView.To(PlaygroundScene.OverviewPose.Look) - PlaygroundObservationView.To(PlaygroundScene.OverviewPose.Position)).Normalized();
        var horizontal = new Vector3(view.X, 0, view.Z).Normalized();
        Require(moved.Length() > 0.01f, "W did not move the free camera");
        Require(moved.Normalized().Dot(horizontal) > 0.999f, $"free flight direction {moved}");
        RequireUnchanged("free flight");
        GD.Print($"OBSERVE_FREE_FLY moved={moved.Length():0.###} dir_dot={moved.Normalized().Dot(horizontal):0.####} step={Session.SimulationStep}");
        return true;
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
        Require(View.InspectionText.Contains("身份", StringComparison.Ordinal) && View.InspectionText.Contains("运行资格", StringComparison.Ordinal),
            "inspection panel did not show the formal facts");
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
        var pick = ReceivedClick();
        Require(pick.Object == ObservedObject.Vehicle(vehicleId), $"click at {pick.Screen} picked {pick.Object?.Describe() ?? "nothing"}");
        Require(Observer.Selection == pick.Object, "click did not select the picked vehicle");
        Require(Observer.Pose == _pose, "world click moved the camera");
        GD.Print(
            $"OBSERVE_CLICK_VEHICLE picked={pick.Object!.Value.Describe()} part={pick.VehicleHit!.Cell.X},{pick.VehicleHit.Cell.Y},{pick.VehicleHit.Cell.Z} " +
            $"distance={pick.VehicleHit.Distance:0.###} camera_moved=false");
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
        Require(pick.Object?.Kind == ObservedObjectKind.WorldCell, $"rail click picked {pick.Object?.Describe()}");
        Require(domain == VoxelCollisionQueryStatus.Hit, $"Godot hit {cell} but domain collision is {domain}");
        Require(_scene.Colliders.QueryPublishedCellInterior(_scene.GetWorld3D(), cell), "published collider interior disagrees with the ray hit");
        Require(Observer.Pose == _pose, "rail click moved the camera");
        Require(View.InspectionText.Contains("rail segment", StringComparison.Ordinal), $"rail cell inspection: {View.InspectionText}");
        GD.Print(
            $"OBSERVE_CLICK_RAIL cell={cell.X},{cell.Y},{cell.Z} godot_hit=true domain={domain} " +
            $"voxel={Session.Query(cell).Voxel?.DefinitionId.Value} camera_moved=false");
        return true;
    }

    /// <summary>
    /// Starts focus / follow through a key or panel button and samples every frame until it settles: the camera must
    /// ease from where it was, without a first-frame jump, and end on the target.
    /// </summary>
    private bool Transition(string name, Key key, PlaygroundCameraMode mode, EntityId? vehicleId)
    {
        if (_frames == 1)
            return false;
        if (_frames == 2)
        {
            _pose = Observer.Pose;
            _looks.Clear();
            _looks.Add(PlaygroundObservationView.To(_pose.Look));
            if (key == Key.None)
                View.Press(mode == PlaygroundCameraMode.Focus ? "focus" : "follow");
            else
            {
                PressKey(key, true);
                PressKey(key, false);
            }

            return false;
        }

        _looks.Add(PlaygroundObservationView.To(Observer.Pose.Look));
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
        var target = vehicleId is { } id ? BodyCenter(id) : PlaygroundObservationView.To(Observer.Center(Observer.Target!.Value, Session.QueryDisplayPoses()));
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
        var look = PlaygroundObservationView.To(Observer.Pose.Look);
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
        var local = PlaygroundObserver.LocalCenter(Session.Vehicles.Single(vehicle => vehicle.Id == vehicleId));
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
        Require(Observer.Target == ObservedObject.Vehicle(loco) && Observer.Mode == PlaygroundCameraMode.Follow, $"split changed the target: {Observer.Describe()}");
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
        Require(Observer.Mode == PlaygroundCameraMode.Free && Observer.Target is null && Observer.Selection is null,
            $"destroyed target left {Observer.Describe()}");
        Require(Observer.LastNotice is { Kind: PlaygroundNoticeKind.TargetLost }, "no target-lost notice");
        Require(View.NoticeText.Contains("已彻底销毁", StringComparison.Ordinal), $"panel notice: {View.NoticeText}");
        Require((camera - _positions[0]).Length() < Tolerance, $"camera jumped from {_positions[0]} to {camera}");
        Require(Observer.Pose.Position == _pose.Position, "observer position changed on target loss");
        var before = (PlaygroundObservationView.To(_pose.Look) - PlaygroundObservationView.To(_pose.Position)).Normalized();
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
        var position = PlaygroundObservationView.To(pose.Position);
        var direction = (PlaygroundObservationView.To(pose.Look) - position).Normalized();
        Require((camera.GlobalPosition - position).Length() < Tolerance, $"{what} at {camera.GlobalPosition}");
        Require((-camera.GlobalBasis.Z).Dot(direction) > 0.9999f, $"{what} looks along {-camera.GlobalBasis.Z}");
    }

    private (Vector3 Position, Vector3 Look) CameraPose() =>
        (_scene.Camera.GlobalPosition, PlaygroundObservationView.To(Observer.Pose.Look));

    private Vector3 BodyCenter(EntityId vehicleId)
    {
        var visual = _scene.Composites.VisualFor(vehicleId) ?? throw new InvalidOperationException($"no visual for {vehicleId}");
        var local = PlaygroundObserver.LocalCenter(Session.Vehicles.Single(vehicle => vehicle.Id == vehicleId));
        return visual.GlobalTransform * PlaygroundObservationView.To(local);
    }

    private string FrameHeading(EntityId vehicleId) =>
        PlaygroundObserver.VehicleFrame(Session, Session.QueryDisplayPoses(), vehicleId)?.Heading.ToString() ?? "none";

    private void RunConsole(string line)
    {
        Main.GetChildren().OfType<GameConsole>().Single().SubmitLine(line);
        GD.Print($"OBSERVE_CONSOLE {line}");
    }

    private static void PressKey(Key key, bool pressed) =>
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = pressed });

    /// <summary>The pick produced by the last synthesized click, which must have arrived at the aimed viewport point.</summary>
    private PlaygroundPick ReceivedClick()
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
