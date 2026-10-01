using Godot;
using Train.Domain.WorldEditor;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train;

/// <summary>
/// Headless world editor entrance for unsaved switch protection, candidate failure, and a cross-process restore.
/// </summary>
internal sealed class WorldEditorSwitchSmoke
{
    public const string ModeValue = "switch";

    private readonly WorldEditorScene _scene;
    private readonly bool _resume;
    private int _phase;
    private bool _finished;
    private string _source = "";
    private ulong _cancelStep;
    private int _shapes;

    public WorldEditorSwitchSmoke(WorldEditorScene scene, string? saveId)
    {
        _scene = scene;
        _resume = Guid.TryParse(saveId, out _);
    }

    public static bool Requested() =>
        string.Equals(
            System.Environment.GetEnvironmentVariable(WorldEditorObservationSmoke.ModeEnvironmentVariable),
            ModeValue,
            StringComparison.Ordinal);

    private VoxelDevelopmentSession Session => _scene.Session!;
    private WorldEditorObserver Observer => _scene.Observation!.Observer;
    private Main Main => _scene.GetNode<Main>("/root/Main");

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
        switch (_phase)
        {
            case 0:
                _source = Session.WorldSource?.ConfigurationId ?? "";
                Require(Session.SimulationPaused, "establish should pause");
                Require(!Session.HasUnsavedChanges, "establish should be a clean boundary");
                Require(Observer.Select(ObservedObject.Segment(Session.Rails.Segments[0].Id)) == WorldEditorObservationStatus.Selected, "segment select");
                _scene.Press("step");
                Require(Session.HasUnsavedChanges, "a step is an unsaved change");
                _cancelStep = Session.SimulationStep;
                _scene.Press("reset_world");
                Require(_scene.PromptVisible, "dirty reset asks for a choice");
                _scene.PressPrompt("cancel");
                Require(!_scene.PromptVisible, "cancel closes the prompt");
                Require(Session.SimulationStep == _cancelStep, "cancel keeps the step");
                Require(Session.HasUnsavedChanges, "cancel keeps the unsaved mark");
                Require(Observer.Selection is not null, "cancel keeps the selection");
                GD.Print($"SWITCH_CANCEL step={Session.SimulationStep} source={_source}");
                _phase++;
                break;
            case 1:
                var pendingWorld = Session.World;
                var pendingFingerprint = WorldSaveAuthority.Fingerprint(Session);
                _scene.Press("switch_world");
                Require(_scene.PromptVisible && Session.IsChangingWorld, "dirty switch asks before the console replaces the world");
                Submit("voxel reset discard");
                Require(_scene.PromptVisible && Session.IsChangingWorld && ReferenceEquals(Session.World, pendingWorld) &&
                        WorldSaveAuthority.Fingerprint(Session) == pendingFingerprint &&
                        Session.WorldSource?.ConfigurationId == _source,
                    "console reset replaced or retargeted a pending switch");
                _scene.PressPrompt("cancel");
                Require(!_scene.PromptVisible && !Session.IsChangingWorld && ReferenceEquals(Session.World, pendingWorld),
                    "cancelling the pending switch did not preserve the world");
                SubmitAndFinish("voxel reset discard");
                Require(Session.SimulationPaused && Session.SimulationStep == 0, "discard reset pauses at the establish boundary");
                Require(!Session.HasUnsavedChanges, "discard reset is the new baseline");
                Require(Observer.Selection is null, "reset drops the old selection");
                GD.Print($"SWITCH_DISCARD source={Session.WorldSource?.ConfigurationId} paused={Session.SimulationPaused}");
                _phase++;
                break;
            case 2:
                var saved = _scene.SaveWorld();
                Require(saved.Status == WorldSaveStatus.Saved && saved.SaveId is not null, "clean save");
                _scene.Press("step");
                Require(Session.HasUnsavedChanges, "step after save is unsaved");
                var path = new FileWorldSaveStore(Main.IsolatedUserDataDirectory).PathFor(saved.SaveId!.Value);
                var file = new FileInfo(path);
                file.IsReadOnly = true;
                try
                {
                    _scene.Press("switch_world");
                    Require(_scene.PromptVisible, "dirty switch asks for a choice");
                    _scene.PressPrompt("save_then_switch");
                    WorldChangeSmoke.Finish(Session);
                    Require(Session.WorldSource?.ConfigurationId == _source, "save failure keeps the configuration");
                    Require(Session.HasUnsavedChanges, "save failure keeps the unsaved mark");
                    Require(Session.SimulationStep == 1, "save failure does not switch");
                }
                finally
                {
                    file.IsReadOnly = false;
                }

                GD.Print("SWITCH_SAVE_FAILED");
                _phase++;
                break;
            case 3:
                var before = WorldSaveAuthority.Fingerprint(Session);
                SubmitAndFinish("voxel use not-a-config discard");
                Require(WorldSaveAuthority.Fingerprint(Session) == before, "unknown configuration keeps the session");
                Require(Session.WorldSource?.ConfigurationId == _source, "unknown configuration keeps the source");
                SubmitAndFinish($"voxel load {Guid.NewGuid():D} discard");
                Require(WorldSaveAuthority.Fingerprint(Session) == before, "missing save keeps the session");
                GD.Print("SWITCH_CANDIDATE_FAILED");
                _phase++;
                break;
            case 4:
                _scene.Press("switch_world");
                Require(_scene.PromptVisible, "the failed save left the world dirty");
                _scene.PressPrompt("save_then_switch");
                WorldChangeSmoke.Finish(Session);
                var other = _source == WorldEditorConfigurations.StraightRailEmptyId
                    ? WorldEditorConfigurations.StraightRailTrainId
                    : WorldEditorConfigurations.StraightRailEmptyId;
                Require(Session.WorldSource?.ConfigurationId == other, "save then switch replaces the configuration");
                Require(Session.SimulationPaused, "switch pauses");
                Require(!Session.HasUnsavedChanges, "a completed switch is the new baseline");
                Require(Session.SaveId is null, "switch does not reuse the previous save id");
                Require(Observer.Selection is null, "switch drops the old selection");
                Require(Session.QueryCollision(StraightRailFixture.LeftRailSample).Status == VoxelCollisionQueryStatus.Hit, "switched world still collides");
                _shapes = _scene.Colliders.ShapeCount;
                GD.Print($"SWITCH_COMPLETED source={Session.WorldSource?.ConfigurationId} assemblies={Session.QueryTrainAssemblies().Count}");
                _phase++;
                break;
            case 5:
                Require(_scene.Colliders.ShapeCount > 0, "collision shapes were rebound");
                Require(_scene.Colliders.LastWorldVoxelCount == Session.World.VoxelCount, "collision follows the new world");
                Require(_shapes > 0, "the previous frame already had shapes");
                _scene.Press("step");
                var continued = _scene.SaveWorld();
                Require(continued.Status == WorldSaveStatus.Saved, "post-switch save");
                GD.Print($"SWITCH_SAVED id={continued.SaveId:D} step={Session.SimulationStep} source={Session.WorldSource?.ConfigurationId} paused={Session.SimulationPaused.ToString().ToLowerInvariant()} fingerprint={WorldSaveAuthority.Fingerprint(Session)}");
                GD.Print("SWITCH_SMOKE_DONE");
                _finished = true;
                _scene.GetTree().Quit();
                break;
        }
    }

    private void AdvanceResume()
    {
        switch (_phase)
        {
            case 0:
                Require(Session.SimulationPaused, "restored session stays paused");
                Require(Session.WorldSource is not null, "restored session keeps its source");
                Require(!Session.HasUnsavedChanges, "a restored session without value adaptation is clean");
                Require(Observer.Selection is null && Observer.Mode == WorldEditorCameraMode.Free, "observation starts free");
                GD.Print($"SWITCH_RESTORED step={Session.SimulationStep} source={Session.WorldSource?.ConfigurationId} paused=true fingerprint={WorldSaveAuthority.Fingerprint(Session)}");
                _phase++;
                break;
            case 1:
                var step = Session.SimulationStep;
                _scene.Press("step");
                Require(Session.SimulationStep == step + 1, "the restored world can continue");
                Require(Session.HasUnsavedChanges, "continuing after restore is unsaved");
                GD.Print($"SWITCH_CONTINUED step={Session.SimulationStep}");
                GD.Print("SWITCH_SMOKE_DONE");
                _finished = true;
                _scene.GetTree().Quit();
                break;
        }
    }

    private void Submit(string line)
    {
        Main.GetChildren().OfType<GameConsole>().Single().SubmitLine(line);
        GD.Print($"SWITCH_CONSOLE {line}");
    }

    private void SubmitAndFinish(string line)
    {
        Submit(line);
        WorldChangeSmoke.Finish(Session);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private void Fail(string message)
    {
        GD.PrintErr($"SWITCH_SMOKE_FAILED {message}");
        GD.PrintErr($"SWITCH_SMOKE_FAILED source={Session.WorldSource?.ConfigurationId} step={Session.SimulationStep} unsaved={Session.HasUnsavedChanges} prompt={_scene.PromptVisible}");
        _finished = true;
        _scene.GetTree().Quit(1);
    }
}
