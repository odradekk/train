using System.Globalization;
using Godot;
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

    private readonly PlaygroundScene _scene;
    private readonly bool _resume;
    private int _phase;
    private int _frames;
    private ulong _mark;
    private bool _finished;

    public PlaygroundSmoke(PlaygroundScene scene, string? saveId)
    {
        _scene = scene;
        _resume = Guid.TryParse(saveId, out _);
    }

    private VoxelDevelopmentSession Session => _scene.Session!;
    private bool HasTrain => Session.QueryTrainAssemblies().Count > 0;

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
            case 6 when _frames >= SettleFrames:
                CheckDisplayAndCollision("PLAYGROUND_SMOKE_AFTER_STEPS");
                var saved = _scene.SaveWorld();
                Require(saved.Status == WorldSaveStatus.Saved, $"save status {saved.Status}");
                GD.Print($"PLAYGROUND_SMOKE_SAVED id={saved.SaveId} step={Session.SimulationStep} {MotionText()}");
                _mark = Session.SimulationStep;
                _scene.Press("run");
                Next();
                break;
            case 7 when _frames >= RunFrames:
                Require(Session.SimulationStep > _mark, $"run did not advance from {_mark}");
                var advanced = Session.SimulationStep - _mark;
                _scene.Press("pause");
                _mark = Session.SimulationStep;
                GD.Print($"PLAYGROUND_SMOKE_RUN advanced={advanced} frames={RunFrames}");
                Next();
                break;
            case 8 when _frames >= HoldFrames:
                Require(Session.SimulationPaused && Session.SimulationStep == _mark, $"pause after run moved step {_mark}->{Session.SimulationStep}");
                GD.Print($"PLAYGROUND_SMOKE_PAUSE_AFTER_RUN frames={HoldFrames} step={_mark}");
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
                _mark = Session.SimulationStep;
                Next();
                break;
            case 1 when _frames >= SettleFrames:
                Require(Session.SimulationStep == _mark, "restored session advanced while paused");
                CheckDisplayAndCollision("PLAYGROUND_SMOKE_RESTORED");
                _scene.Press("step");
                Require(Session.SimulationStep == _mark + 1, $"single step after restore reached {Session.SimulationStep}");
                GD.Print($"PLAYGROUND_SMOKE_RESTORED_STEP step={Session.SimulationStep} {MotionText()}");
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
