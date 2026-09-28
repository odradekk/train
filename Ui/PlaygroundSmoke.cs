using System.Globalization;
using Godot;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Playground;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train;

/// <summary>
/// Headless automatic entrance: drives the real Playground scene through its own controls and Godot physics.
/// </summary>
internal sealed class PlaygroundSmoke
{
    private const int SettleFrames = 4;
    private const int HoldFrames = 10;
    private const int RunFrames = 20;
    private const ulong EditDelay = 3;
    private const ulong ContinuationSteps = 5;
    private static readonly WorldVoxelCoord ConsoleCell = new(61, 3, 20);

    private readonly PlaygroundScene _scene;
    private readonly bool _resume;
    private int _phase;
    private int _frames;
    private ulong _mark;
    private SimulationPosition _saveBoundary;
    private bool _finished;

    public PlaygroundSmoke(PlaygroundScene scene, string? saveId)
    {
        _scene = scene;
        _resume = Guid.TryParse(saveId, out _);
    }

    private VoxelDevelopmentSession Session => _scene.Session!;
    private bool HasTrain => Session.QueryTrainAssemblies().Count > 0;
    private int QueuedEdits => HasTrain ? 5 : 2;

    public void Advance()
    {
        if (_finished)
            return;
        try
        {
            if (_resume)
                AdvanceResume();
            else
                AdvanceEstablish();
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
    }

    private void AdvanceEstablish()
    {
        _frames++;
        switch (_phase)
        {
            case 0:
                var source = Session.WorldSource ?? throw new InvalidOperationException("established session has no source");
                Require(Session.SimulationPaused, "established session is not paused");
                Require(Session.SimulationStep == 0, $"established step is {Session.SimulationStep}");
                GD.Print(
                    $"PLAYGROUND_SMOKE_ESTABLISHED config={source.ConfigurationId} version={source.ConfigurationVersion} kind={source.Kind} " +
                    $"parameters={source.EffectiveParameters.Count} dependencies={source.ContentDependencies.Count} paused=true step=0 " +
                    $"vehicles={Session.QueryComposites().Count} segments={Session.Rails.Segments.Count} sleepers={Session.QueryRails().SleeperCount}");
                foreach (var dependency in source.ContentDependencies)
                    GD.Print($"PLAYGROUND_SMOKE_DEPENDENCY {dependency.Kind} {dependency.Id} {dependency.SemanticRevision}");
                Next();
                break;
            case 1 when _frames >= SettleFrames:
                CheckDisplayAndCollision("PLAYGROUND_SMOKE");
                _mark = Session.SimulationStep;
                Next();
                break;
            case 2:
                if (_frames == 1)
                    ToggleConsole();
                Session.QueryTrainAssemblies();
                Session.QueryRails();
                Session.QueryCollision(StraightRailFixture.LeftRailSample);
                if (_frames < HoldFrames)
                    break;
                var consoleOpened = Main.ConsoleOpen;
                ToggleConsole();
                Require(consoleOpened, "console did not open");
                Require(Session.SimulationPaused && Session.SimulationStep == _mark, $"paused frames moved step {_mark}->{Session.SimulationStep}");
                GD.Print($"PLAYGROUND_SMOKE_PAUSE_HELD frames={HoldFrames} step={Session.SimulationStep} console_opened=true");
                Next();
                break;
            case 3 when !Main.ConsoleOpen:
                Require(Session.SimulationPaused && Session.SimulationStep == _mark, "closing the console changed the clock");
                Next();
                break;
            case 4:
                RunIndependentSequences();
                Next();
                break;
            case 5:
                RunManualControls();
                Next();
                break;
            case 6:
                RunPausedEdits();
                Next();
                break;
            case 7 when _frames >= SettleFrames:
                CheckDisplayAndCollision("PLAYGROUND_SMOKE_AFTER_STEPS");
                CheckEditedCells("PLAYGROUND_SMOKE_EDITED");
                var queue = QueueText();
                var saved = _scene.SaveWorld();
                Require(saved.Status == WorldSaveStatus.Saved, $"save status {saved.Status}");
                Require(QueueText() == queue, "saving changed the edit queue");
                GD.Print($"PLAYGROUND_SMOKE_SAVED id={saved.SaveId} step={Session.SimulationStep} {MotionText()}");
                GD.Print($"PLAYGROUND_SMOKE_SAVED_QUEUE {queue}");
                _saveBoundary = Session.EditBoundary;
                _scene.SetStepCount(ContinuationSteps);
                _scene.Press("step_n");
                PrintContinuation();
                Next();
                break;
            case 8:
                _mark = Session.SimulationStep;
                _scene.Press("run");
                Next();
                break;
            case 9 when _frames >= RunFrames:
                Require(Session.SimulationStep > _mark, $"run did not advance from {_mark}");
                var advanced = Session.SimulationStep - _mark;
                _scene.Press("pause");
                _mark = Session.SimulationStep;
                GD.Print($"PLAYGROUND_SMOKE_RUN advanced={advanced} frames={RunFrames}");
                Next();
                break;
            case 10 when _frames >= HoldFrames:
                Require(Session.SimulationPaused && Session.SimulationStep == _mark, $"pause after run moved step {_mark}->{Session.SimulationStep}");
                GD.Print($"PLAYGROUND_SMOKE_PAUSE_AFTER_RUN frames={HoldFrames} step={_mark}");
                Next();
                break;
            case 11:
                if (HasTrain)
                    RunRailCommands();
                else
                    RunPanelAssembly();
                Finish();
                break;
        }
    }

    private Main Main => _scene.GetNode<Main>("/root/Main");

    private static void ToggleConsole()
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Quoteleft, PhysicalKeycode = Key.Quoteleft, Pressed = true });
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Quoteleft, PhysicalKeycode = Key.Quoteleft, Pressed = false });
    }

    private void AdvanceResume()
    {
        _frames++;
        switch (_phase)
        {
            case 0:
                var source = Session.WorldSource ?? throw new InvalidOperationException("restored session lost its source");
                Require(Session.SimulationPaused, "restored Playground session is not paused");
                Require(source.ConfigurationId == Main.PlaygroundConfigurationId, $"restored source {source.ConfigurationId} is not the requested configuration");
                GD.Print(
                    $"PLAYGROUND_SMOKE_RESTORED config={source.ConfigurationId} version={source.ConfigurationVersion} kind={source.Kind} " +
                    $"parameters={source.EffectiveParameters.Count} dependencies={source.ContentDependencies.Count} paused=true " +
                    $"step={Session.SimulationStep} {MotionText()}");
                GD.Print($"PLAYGROUND_SMOKE_RESTORED_QUEUE {QueueText()}");
                _mark = Session.SimulationStep;
                _saveBoundary = Session.EditBoundary;
                Next();
                break;
            case 1 when _frames >= SettleFrames:
                Require(Session.SimulationStep == _mark, "restored session advanced while paused");
                CheckDisplayAndCollision("PLAYGROUND_SMOKE_RESTORED");
                CheckEditedCells("PLAYGROUND_SMOKE_RESTORED_EDITED");
                _scene.Press("step");
                Require(Session.SimulationStep == _mark + 1, $"single step after restore reached {Session.SimulationStep}");
                GD.Print($"PLAYGROUND_SMOKE_RESTORED_STEP step={Session.SimulationStep} {MotionText()}");
                _scene.SetStepCount(ContinuationSteps - 1);
                _scene.Press("step_n");
                PrintContinuation();
                Finish();
                break;
        }
    }

    private void RunIndependentSequences()
    {
        var reloader = Main.Reloader ?? throw new InvalidOperationException("content unavailable");
        var configurationId = Session.WorldSource!.ConfigurationId;
        var sequences = PlaygroundSequences.All.Where(item => item.ConfigurationId == configurationId).ToArray();
        foreach (var sequence in sequences)
        {
            var report = PlaygroundValidation.Run(sequence, reloader.Catalog, reloader.Composites);
            foreach (var line in report.Lines())
                GD.Print("PLAYGROUND_SEQUENCE " + line);
            Require(report.Passed, $"sequence {sequence.Id} failed");
            GD.Print($"PLAYGROUND_SMOKE_SEQUENCE_PASSED {sequence.Id} steps={report.Steps.Count}");
        }

        Require(Session.SimulationStep == 0, "independent sequences touched the displayed session");
        GD.Print($"PLAYGROUND_SMOKE_SEQUENCES count={sequences.Length} displayed_step={Session.SimulationStep}");
    }

    private void RunManualControls()
    {
        if (HasTrain)
        {
            _scene.Press("start");
            Require(Session.QueryTrainAssemblies().Single().Running, "start button did not start the train");
        }

        _scene.Press("step");
        Require(Session.SimulationStep == 1, $"single step reached {Session.SimulationStep}");
        var remaining = HasTrain ? 299UL : 119UL;
        _scene.SetStepCount(remaining);
        _scene.Press("step_n");
        Require(Session.SimulationStep == 1 + remaining, $"n-step reached {Session.SimulationStep}");
        Require(Session.SimulationPaused, "stepping resumed the clock");
        if (HasTrain)
        {
            var front = Session.QueryTrainAssemblies().Single().Vehicles[0].FrontContact.X;
            Require(Math.Abs(front - 96.0) < 1e-8, $"manual front contact {front}");
            RequireManualMatchesSequence();
        }

        GD.Print($"PLAYGROUND_SMOKE_MANUAL step={Session.SimulationStep} paused=true {MotionText()}");
    }

    private void RequireManualMatchesSequence()
    {
        var sequence = PlaygroundSequences.Find(PlaygroundSequences.StraightRailRunAndHaltId)!;
        var pressed = new[] { PlaygroundOperation.Start(), PlaygroundOperation.Advance(1), PlaygroundOperation.Advance(299) };
        Require(sequence.Steps.Take(pressed.Length).Select(step => step.Operation).SequenceEqual(pressed), "button operations differ from the registered sequence");
        var checkpoint = sequence.Steps[pressed.Length - 1].Checkpoint;
        foreach (var check in checkpoint)
        {
            var observed = PlaygroundCheck.Observe(Session, check.Observation);
            Require(observed == check.Expected, $"manual {check.Observation}={observed}, sequence expects {check.Expected}");
        }

        GD.Print($"PLAYGROUND_SMOKE_MANUAL_MATCHES_SEQUENCE {sequence.Id} steps={pressed.Length} checks={checkpoint.Count}");
    }

    /// <summary>
    /// Paused at one boundary: panel and real console edits take consecutive in-step sequences, then one panel and
    /// one console edit are queued <see cref="EditDelay"/> steps ahead, including a structural refit. With a train, a
    /// panel stop, a console repair of an intact rail cell and a console start naming the shell (an allocated id that
    /// is no assembly) join the same boundary, so the saved queue mixes kinds and carries two due rejections.
    /// </summary>
    private void RunPausedEdits()
    {
        var step = Session.SimulationStep;
        Require(Session.EditBoundary == new SimulationPosition(step, 0), $"edit boundary {Session.EditBoundary} before paused edits");
        var motion = MotionText();
        var probe = PlaygroundSequences.EditProbe;
        var add = HollowShellRefit.FirstAdd;
        var identity = VoxelOrientation.Identity;
        _scene.SetEditInputs(probe, add, 25, 0);
        PressEdit("edit_add");
        PressEdit("edit_damage");
        RunConsole($"voxel add {ConsoleCell.X} {ConsoleCell.Y} {ConsoleCell.Z} {PlaygroundSequences.EditVoxelId.Value} {identity}");
        var pose = PlaygroundSequences.EditShellPose;
        _scene.SetEditInputs(new WorldVoxelCoord((int)pose.OriginX, (int)pose.OriginY, (int)pose.OriginZ), add, 25, 0);
        PressEdit("edit_manufacture");
        var shell = Session.QueryComposites().Max(item => item.Id.Value);
        RunConsole($"composite add {shell} {add.X} {add.Y} {add.Z} {HollowShellRefit.MarkerId.Value} {identity} at {step + EditDelay}");
        _scene.SetEditInputs(probe, add, 25, EditDelay);
        PressEdit("edit_remove");
        if (HasTrain)
        {
            var rail = StraightRailFixture.FirstSegmentDamageCell;
            PressEdit("stop");
            RunConsole($"rail repair {rail.X} {rail.Y} {rail.Z} at {step + EditDelay}");
            RunConsole($"rail start assembly {shell} at {step + EditDelay}");
        }

        var executed = Session.QueryEditLog().Where(record => record.AcceptedAt.Step == step).ToArray();
        Require(
            executed.Select(record => record.Status).SequenceEqual(new[] { "Added", "Damaged", "Added", "Valid" }),
            $"paused edit statuses {string.Join(',', executed.Select(record => record.Status))}");
        for (var index = 0; index < executed.Length; index++)
            Require(executed[index].ExecutedAt == new SimulationPosition(step, (ulong)index), $"edit {executed[index].Id} ran at {executed[index].ExecutedAt}");
        var pending = Session.QueryPendingEdits();
        Require(pending.Count == QueuedEdits && pending.All(record => record.TargetStep == step + EditDelay), $"pending edits {QueueText()}");
        Require(Session.EditBoundary == new SimulationPosition(step, 4 + (ulong)QueuedEdits), $"edit boundary {Session.EditBoundary}");
        Require(Session.SimulationPaused && Session.SimulationStep == step && MotionText() == motion, "paused edits moved the clock or the train");
        GD.Print(
            $"PLAYGROUND_SMOKE_EDITS step={step} boundary={Session.EditBoundary} " +
            $"executed={string.Join(',', executed.Select(PlaygroundCheck.DescribeRecord))} {motion}");
        GD.Print($"PLAYGROUND_SMOKE_EDITS_PENDING {QueueText()}");
    }

    /// <summary>
    /// Rail removal, repair and an explicit start queued for one boundary through the panel, a console stop behind
    /// them, then an immediate removal and repair checked against Godot physics on the edited rail cell.
    /// </summary>
    private void RunRailCommands()
    {
        var step = Session.SimulationStep;
        var rail = StraightRailFixture.FirstSegmentDamageCell;
        var boundary = step + EditDelay;
        _scene.SetEditInputs(rail, HollowShellRefit.FirstAdd, 100, EditDelay);
        PressEdit("edit_damage");
        PressEdit("edit_repair");
        PressEdit("start");
        RunConsole($"rail stop at {boundary + EditDelay}");
        Require(Session.QueryPendingEdits().Count == 4 && Session.SimulationStep == step, $"rail commands queue {QueueText()}");
        _scene.SetStepCount(2 * EditDelay + 1);
        _scene.Press("step_n");

        var executed = Session.QueryEditLog().Where(record => record.AcceptedAt.Step == step).ToArray();
        Require(
            executed.Select(record => record.Status).SequenceEqual(new[] { "Removed", "Repaired", "Started", "Stopped" }),
            $"rail command statuses {string.Join(',', executed.Select(record => record.Status))}");
        Require(
            executed.Select(record => record.ExecutedAt).SequenceEqual(new SimulationPosition?[]
            {
                new(boundary, 0), new(boundary, 1), new(boundary, 2), new(boundary + EditDelay, 0)
            }),
            $"rail command positions {string.Join(',', executed.Select(PlaygroundCheck.DescribeRecord))}");
        var halted = Session.QueryTrainAssemblies().Single();
        Require(!halted.Running && halted.Speed == 0 && halted.HaltReason == TrainHaltReason.None, $"rail commands left {MotionText()}");
        GD.Print($"PLAYGROUND_SMOKE_RAIL_QUEUE step={Session.SimulationStep} executed={string.Join(',', executed.Select(PlaygroundCheck.DescribeRecord))} {MotionText()}");

        _scene.SetEditInputs(rail, HollowShellRefit.FirstAdd, 100, 0);
        PressEdit("edit_damage");
        _scene.Refresh();
        var world = _scene.GetWorld3D();
        var gapHit = _scene.Colliders.QueryPublishedCellInterior(world, rail);
        var gapRendered = _scene.Tracks.RailSolidCount;
        PressEdit("edit_repair");
        _scene.Refresh();
        var repairedHit = _scene.Colliders.QueryPublishedCellInterior(world, rail);
        Require(!gapHit && repairedHit, $"Godot physics on the edited rail cell gap={gapHit} repaired={repairedHit}");
        Require(Session.QueryEditLog()[^1].Status == "Repaired", "immediate repair was not recorded");
        GD.Print(
            $"PLAYGROUND_SMOKE_RAIL_COLLISION godot_gap_hit={gapHit.ToString().ToLowerInvariant()} godot_repaired_hit={repairedHit.ToString().ToLowerInvariant()} " +
            $"rail_solids_gap={gapRendered} rail_solids_repaired={_scene.Tracks.RailSolidCount} last={PlaygroundCheck.DescribeRecord(Session.QueryEditLog()[^1])}");
    }

    /// <summary>
    /// Without a train: the panel's construct is rejected by the existing line and its assemble forms the fixture
    /// train through the same edit entry, then the display and Godot collision follow the new vehicles.
    /// </summary>
    private void RunPanelAssembly()
    {
        _scene.SetEditInputs(PlaygroundSequences.EditProbe, HollowShellRefit.FirstAdd, 25, 0);
        PressEdit("edit_construct");
        PressEdit("edit_assemble");
        var records = Session.QueryEditLog().TakeLast(2).ToArray();
        Require(
            records.Select(record => record.Status).SequenceEqual(new[] { "IndependentLineConflict", "Formed" }),
            $"panel assembly statuses {string.Join(',', records.Select(record => record.Status))}");
        Require(Session.QueryTrainAssemblies().Count == 1 && Session.SimulationPaused, $"panel assembly left {MotionText()}");
        CheckDisplayAndCollision("PLAYGROUND_SMOKE_ASSEMBLED");
        GD.Print($"PLAYGROUND_SMOKE_PANEL_ASSEMBLY executed={string.Join(',', records.Select(PlaygroundCheck.DescribeRecord))} {MotionText()}");
    }

    private void PressEdit(string button)
    {
        var operation = _scene.EditOperation(button);
        _scene.Press(button);
        GD.Print($"PLAYGROUND_SMOKE_PANEL {button} {operation.Describe()}");
    }

    private void RunConsole(string line)
    {
        var console = Main.GetChildren().OfType<GameConsole>().Single();
        console.SubmitLine(line);
        GD.Print($"PLAYGROUND_SMOKE_CONSOLE {line}");
    }

    private void CheckEditedCells(string marker)
    {
        _scene.Refresh();
        var world = _scene.GetWorld3D();
        var probeHit = _scene.Colliders.QueryPublishedCellInterior(world, PlaygroundSequences.EditProbe);
        var consoleHit = _scene.Colliders.QueryPublishedCellInterior(world, ConsoleCell);
        var probe = Session.Query(PlaygroundSequences.EditProbe).Voxel;
        Require(probeHit && consoleHit, $"Godot physics missed edited cells probe={probeHit} console={consoleHit}");
        Require(Session.QueryCollision(PlaygroundSequences.EditProbe).Status == VoxelCollisionQueryStatus.Hit, "domain collision missed the edited probe");
        Require(probe is not null && probe.CurrentDurability == Session.Query(PlaygroundSequences.EditProbe).MaxDurability - 25, "edited probe durability");
        GD.Print(
            $"{marker}_COLLISION godot_probe_hit={probeHit.ToString().ToLowerInvariant()} godot_console_cell_hit={consoleHit.ToString().ToLowerInvariant()} " +
            $"probe_durability={probe!.CurrentDurability} vehicles={Session.QueryComposites().Count}");
    }

    private string QueueText()
    {
        var pending = Session.QueryPendingEdits().Select(record =>
            $"{record.Id}:{WorldEditCodec.WireKind(record.Edit.Kind)}@{record.AcceptedAt}->{record.TargetStep}");
        return $"boundary={Session.EditBoundary} next={Session.NextEditId} pending={string.Join(',', pending)}";
    }

    /// <summary>Deterministic continuation after the save: identical in the saving and the restoring process.</summary>
    private void PrintContinuation()
    {
        var executed = Session.QueryEditLog()
            .Where(record => record.ExecutedAt is { } at && at.CompareTo(_saveBoundary) >= 0)
            .ToArray();
        Require(executed.Length == QueuedEdits && Session.QueryPendingEdits().Count == 0, $"continuation executed {executed.Length} pending {Session.QueryPendingEdits().Count}");
        GD.Print(
            $"PLAYGROUND_SMOKE_CONTINUATION step={Session.SimulationStep} " +
            $"executed={string.Join(',', executed.Select(PlaygroundCheck.DescribeRecord))} " +
            $"payloads={string.Join(',', executed.Select(record => record.Edit.Digest[..16]))} " +
            $"fingerprint={WorldSaveAuthority.Fingerprint(Session)} {MotionText()}");
    }

    private void CheckDisplayAndCollision(string marker)
    {
        _scene.Refresh();
        var world = _scene.GetWorld3D();
        var railHit = _scene.Colliders.QueryPublishedCellInterior(world, StraightRailFixture.LeftRailSample);
        var emptyHit = _scene.Colliders.QueryPublishedCellInterior(world, PlaygroundScene.EmptyProbe);
        var expectedVehicles = Session.QueryComposites().Count;
        Require(_scene.Tracks.RailSolidCount > 0 && _scene.Tracks.SleeperSolidCount > 0, "track renderer is empty");
        Require(_scene.Composites.VehicleCount == expectedVehicles, $"vehicle visuals {_scene.Composites.VehicleCount} != {expectedVehicles}");
        Require(expectedVehicles == 0 || _scene.Composites.MeshInstanceCount > 0, "vehicle visuals have no meshes");
        Require(railHit, "Godot physics missed the published rail cell");
        Require(!emptyHit, "Godot physics hit an empty probe cell");
        Require(Session.QueryCollision(StraightRailFixture.LeftRailSample).Status == VoxelCollisionQueryStatus.Hit, "domain collision missed rail");
        GD.Print(
            $"{marker}_DISPLAY world_faces={_scene.Meshes.FaceCount} rail_solids={_scene.Tracks.RailSolidCount} " +
            $"sleeper_solids={_scene.Tracks.SleeperSolidCount} vehicle_visuals={_scene.Composites.VehicleCount} vehicle_meshes={_scene.Composites.MeshInstanceCount}");
        GD.Print(
            $"{marker}_COLLISION godot_rail_hit={railHit.ToString().ToLowerInvariant()} godot_empty_hit={emptyHit.ToString().ToLowerInvariant()} " +
            $"shapes={_scene.Colliders.ShapeCount}");
    }

    private string MotionText()
    {
        var assembly = Session.QueryTrainAssemblies().FirstOrDefault();
        return assembly is null
            ? "assemblies=0"
            : $"front={assembly.Vehicles[0].FrontContact.X.ToString("0.###", CultureInfo.InvariantCulture)} " +
              $"speed={assembly.Speed.ToString("0.###", CultureInfo.InvariantCulture)} running={assembly.Running.ToString().ToLowerInvariant()} halt={assembly.HaltReason}";
    }

    private void Next()
    {
        _phase++;
        _frames = 0;
    }

    private void Finish()
    {
        _finished = true;
        GD.Print("PLAYGROUND_SMOKE_DONE");
        _scene.GetTree().Quit();
    }

    private void Fail(string reason)
    {
        _finished = true;
        GD.PrintErr($"PLAYGROUND_SMOKE_FAILED {reason}");
        _scene.GetTree().Quit(1);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
            throw new InvalidOperationException(reason);
    }
}

/// <summary>Windowed capture for agent-side observation; not a substitute for manual visual acceptance.</summary>
internal sealed class PlaygroundCapture
{
    private const int SettleFrames = 30;

    private readonly PlaygroundScene _scene;
    private readonly string _path;
    private readonly ulong _steps;
    private int _frames;
    private bool _finished;

    public PlaygroundCapture(PlaygroundScene scene, string path, string? steps)
    {
        _scene = scene;
        _path = path;
        _steps = ulong.TryParse(steps, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    public void Advance()
    {
        if (_finished)
            return;
        _frames++;
        if (_frames == 1 && _steps > 0)
        {
            if (_scene.Session!.QueryTrainAssemblies().Count > 0)
                _scene.Press("start");
            _scene.SetStepCount(_steps);
            _scene.Press("step_n");
        }

        if (_frames < SettleFrames)
            return;
        _finished = true;
        var image = _scene.GetViewport().GetTexture().GetImage();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var error = image.SavePng(_path);
        GD.Print($"PLAYGROUND_CAPTURE path={_path} error={error} step={_scene.Session!.SimulationStep} paused={_scene.Session.SimulationPaused.ToString().ToLowerInvariant()}");
        _scene.GetTree().Quit(error == Error.Ok ? 0 : 1);
    }
}
