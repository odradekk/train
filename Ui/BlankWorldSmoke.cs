using System.Text;
using System.Text.Json.Nodes;
using Godot;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

/// <summary>Real-Godot, new-process coverage for blank worlds and cross-source loading.</summary>
internal sealed class BlankWorldSmoke
{
    public const string LegEnvironmentVariable = "TRAIN_BLANK_WORLD_LEG";
    public const string SaveIdEnvironmentVariable = "TRAIN_BLANK_WORLD_SAVE_ID";

    private static readonly WorldVoxelCoord EditedCell = new(17, 1, 1);
    private static readonly WorldVoxelCoord ContinuedCell = new(18, 1, 1);

    private readonly WorldEditorScene _scene;
    private readonly string _leg;
    private readonly Guid? _savedBlank;
    private int _phase;
    private ulong _physicsReadyAt;
    private ulong _runUntilStep;
    private bool _finished;
    private Guid _legacySave;

    public BlankWorldSmoke(WorldEditorScene scene)
    {
        _scene = scene;
        _leg = System.Environment.GetEnvironmentVariable(LegEnvironmentVariable) ?? "";
        _savedBlank = Guid.TryParse(System.Environment.GetEnvironmentVariable(SaveIdEnvironmentVariable), out var saveId)
            ? saveId
            : null;
        if (_leg is not ("save" or "resume") || (_leg == "resume" && _savedBlank is null))
            throw new InvalidOperationException("blank-world leg and resume save id are required");
    }

    public static bool Requested() =>
        System.Environment.GetEnvironmentVariable(LegEnvironmentVariable) is "save" or "resume";

    private VoxelDevelopmentSession Session => _scene.Session!;
    private WorldEditorObserver Observer => _scene.Observation!.Observer;
    private Main Main => _scene.GetNode<Main>("/root/Main");

    public void Advance()
    {
        if (_finished)
            return;
        try
        {
            if (_leg == "save")
                AdvanceSave();
            else
                AdvanceResume();
        }
        catch (Exception exception)
        {
            Fail(exception.ToString());
        }
    }

    private void AdvanceSave()
    {
        switch (_phase)
        {
            case 0:
                Require(Session.WorldSource?.Kind == WorldSource.FixedConfigurationKind, "save leg did not start from a fixed source");
                Require(Session.Rails.Segments.Count > 0, "fixed train has no segment to select");
                Require(Observer.Select(ObservedObject.Segment(Session.Rails.Segments[0].Id)) == WorldEditorObservationStatus.Selected,
                    "fixed segment selection failed");
                SubmitAndFinish("voxel new 33 17 19 42 discard");
                AssertBlankBoundary();
                Require(Observer.Selection is null && Observer.Target is null && Observer.Mode == WorldEditorCameraMode.Free,
                    "new blank world kept an observation reference");
                GD.Print("BLANK_WORLD_CREATED bounds=33,17,19 seed=42 source=blank paused=true");
                _phase++;
                break;
            case 1:
                SubmitAndFinish("voxel new 0 17 19 42 discard");
                SubmitAndFinish("voxel new 321 17 19 42 discard");
                SubmitAndFinish("voxel new 33 0 19 42 discard");
                SubmitAndFinish("voxel new 33 17 33 42 discard");
                AssertBlankBoundary();
                GD.Print("BLANK_WORLD_INVALID_NEW_KEPT count=4");
                Submit("voxel add 17 1 1 core:ferrous-ore px_py");
                Submit("voxel damage 17 1 1 25");
                Submit("voxel remove 17 1 1 at 3");
                Submit("rail step");
                Require(Session.SimulationStep == 1, "single step did not reach step 1");
                Require(Session.QueryPendingEdits().Count == 1, "pending edit was not queued");
                Require(Session.Query(EditedCell).Voxel?.CurrentDurability == 75, "edited voxel durability is not 75");
                _scene.Refresh();
                _physicsReadyAt = Engine.GetPhysicsFrames() + 2;
                _phase++;
                break;
            case 2:
                if (Engine.GetPhysicsFrames() < _physicsReadyAt)
                    return;
                _scene.Refresh();
                Require(_scene.Meshes.LastWorldVoxelCount == 1 && _scene.Colliders.LastWorldVoxelCount == 1,
                    "blank edit was not rebuilt in Godot presenters");
                Require(_scene.Colliders.QueryPublishedCellInterior(_scene.GetWorld3D(), EditedCell),
                    "blank edit has no published collision");
                var saved = _scene.SaveWorld();
                Require(saved.Status == WorldSaveStatus.Saved && saved.SaveId is not null, "blank save failed");
                GD.Print($"BLANK_WORLD_SAVED id={saved.SaveId:D} step=1 durability=75 pending=1");
                Finish();
                break;
        }
    }

    private void AdvanceResume()
    {
        switch (_phase)
        {
            case 0:
                Require(Session.WorldSource?.Kind == WorldSource.FixedConfigurationKind, "resume leg did not first establish fixed source");
                Require(Session.Rails.Segments.Count > 0, "fixed source has no segment");
                Require(Observer.Select(ObservedObject.Segment(Session.Rails.Segments[0].Id)) == WorldEditorObservationStatus.Selected,
                    "fixed segment selection failed");
                SubmitAndFinish($"voxel load {_savedBlank:D} discard");
                AssertRestoredBlank();
                Require(Observer.Selection is null && Observer.Target is null && Observer.Mode == WorldEditorCameraMode.Free,
                    "cross-source load kept the fixed-world observation reference");
                GD.Print($"BLANK_WORLD_RESTORED id={_savedBlank:D} step=1 durability=75 pending=1 from=fixed");
                _scene.Refresh();
                _physicsReadyAt = Engine.GetPhysicsFrames() + 2;
                _phase++;
                break;
            case 1:
                if (Engine.GetPhysicsFrames() < _physicsReadyAt)
                    return;
                _scene.Refresh();
                Require(_scene.Meshes.LastWorldVoxelCount == 1 && _scene.Colliders.LastWorldVoxelCount == 1,
                    "restored blank world did not rebuild presenter bounds");
                Require(_scene.Colliders.QueryPublishedCellInterior(_scene.GetWorld3D(), EditedCell),
                    "restored blank voxel has no published collision");
                Submit("voxel add 18 1 1 core:ferrous-ore px_py");
                Submit("rail step 2");
                Require(Session.SimulationStep == 3, "restored blank world did not reach step 3");
                Require(Session.Query(EditedCell).Voxel is null && Session.QueryPendingEdits().Count == 0,
                    "restored pending edit did not execute exactly once");
                Require(Session.Query(ContinuedCell).Voxel is not null, "post-restore edit was lost");
                GD.Print("BLANK_WORLD_CONTINUED step=3 pending=0 edited_removed=true continued=true");
                _phase++;
                break;
            case 2:
                SubmitAndFinish("voxel use straight-rail-empty discard");
                Require(Session.WorldSource?.Kind == WorldSource.FixedConfigurationKind, "blank to fixed switch failed");
                SubmitAndFinish("voxel new 33 17 19 42 discard");
                AssertBlankBoundary();
                GD.Print("BLANK_WORLD_TRANSITION blank-fixed-blank=true");

                var reloader = Main.Reloader ?? throw new InvalidOperationException("content reloader is unavailable");
                var predecessor = new VoxelDevelopmentSession(
                    reloader.Catalog, VoxelWorldIdentity.Slice2,
                    new FileWorldSaveStore(Main.IsolatedUserDataDirectory), reloader.Composites);
                try
                {
                    var legacy = predecessor.Save("Blank smoke slice 2 predecessor");
                    Require(legacy.Status == WorldSaveStatus.Saved && legacy.SaveId is not null,
                        "slice 2 predecessor save failed");
                    _legacySave = legacy.SaveId.GetValueOrDefault();
                }
                finally
                {
                    predecessor.Detach();
                }
                SubmitAndFinish($"voxel load {_legacySave:D} discard");
                Require(Session.WorldSource is null && Session.Identity == VoxelWorldIdentity.Slice2,
                    "slice 2 predecessor load acquired a source or changed identity");
                SubmitAndFinish("voxel new 33 17 19 42 discard");
                SubmitAndFinish($"voxel load {_savedBlank:D} discard");
                AssertRestoredBlank();
                GD.Print($"BLANK_WORLD_TRANSITION blank-legacy-blank=true legacy={_legacySave:D}");
                _phase++;
                break;
            case 3:
                Submit("voxel add 18 1 1 core:ferrous-ore px_py");
                Require(Observer.Select(ObservedObject.WorldCell(ContinuedCell)) == WorldEditorObservationStatus.Selected,
                    "bad-load preservation selection failed");
                Submit("rail resume");
                Require(!Session.SimulationPaused, "blank world did not resume");
                _runUntilStep = Session.SimulationStep + 2;
                _phase++;
                break;
            case 4:
                if (Session.SimulationStep < _runUntilStep)
                    return;
                var before = WorldSaveAuthority.Fingerprint(Session);
                var step = Session.SimulationStep;
                var selected = Observer.Selection;
                var paused = Session.SimulationPaused;
                var store = new FileWorldSaveStore(Main.IsolatedUserDataDirectory);
                var path = store.PathFor(_savedBlank.GetValueOrDefault());
                var original = File.ReadAllText(path, Encoding.UTF8);
                try
                {
                    var root = JsonNode.Parse(original) ?? throw new InvalidOperationException("blank save JSON could not be parsed");
                    root["generator_version"] = "invalid-blank-smoke-generator";
                    File.WriteAllText(path, root.ToJsonString(), Encoding.UTF8);
                    SubmitAndFinish($"voxel load {_savedBlank:D} discard");
                    Require(WorldSaveAuthority.Fingerprint(Session) == before && Session.SimulationStep == step,
                        "bad blank save replaced or advanced the current world");
                    Require(Session.SimulationPaused == paused && Observer.Selection == selected,
                        "bad blank save changed running or observation state");
                }
                finally
                {
                    File.WriteAllText(path, original, Encoding.UTF8);
                }

                Submit("rail pause");
                Require(Session.SimulationPaused, "final pause failed");
                GD.Print($"BLANK_WORLD_BAD_LOAD_KEPT step={Session.SimulationStep} running=true observation=true");
                Finish();
                break;
        }
    }

    private void AssertBlankBoundary()
    {
        Require(Session.WorldSource?.Kind == WorldSource.BlankKind, "world source is not blank");
        Require(Session.Identity.GeneratorVersion == BlankWorldSource.GeneratorVersion, "blank generator version differs");
        Require(Session.Identity.Bounds == new VoxelWorldBounds(33, 17, 19), "blank bounds differ");
        Require(Session.Identity.Seed == 42, "blank seed differs");
        Require(Session.World.VoxelCount == 0 && Session.Vehicles.Count == 0 && Session.Rails.Segments.Count == 0,
            "blank boundary is not empty");
        Require(Session.QueryTrainAssemblies().Count == 0, "blank boundary has an assembly");
        Require(Session.SimulationPaused && Session.HasUnsavedChanges && Session.SaveId is null,
            "blank boundary persistence state differs");
    }

    private void AssertRestoredBlank()
    {
        Require(Session.WorldSource?.Kind == WorldSource.BlankKind, "restored source is not blank");
        Require(Session.Identity.GeneratorVersion == BlankWorldSource.GeneratorVersion, "restored generator version differs");
        Require(Session.Identity.Bounds == new VoxelWorldBounds(33, 17, 19) && Session.Identity.Seed == 42,
            "restored blank identity differs");
        Require(Session.SimulationPaused && Session.SimulationStep == 1, "restored blank step or pause differs");
        Require(Session.SaveId == _savedBlank && !Session.HasUnsavedChanges, "restored blank save identity or clean state differs");
        Require(Session.Query(EditedCell).Voxel?.CurrentDurability == 75 && Session.QueryPendingEdits().Count == 1,
            "restored blank edits differ");
    }

    private void Submit(string line)
    {
        Main.GetChildren().OfType<GameConsole>().Single().SubmitLine(line);
        GD.Print($"BLANK_WORLD_CONSOLE {line}");
    }

    private void SubmitAndFinish(string line)
    {
        Submit(line);
        WorldChangeSmoke.Finish(Session);
    }

    private void Finish()
    {
        _finished = true;
        GD.Print($"BLANK_WORLD_DONE leg={_leg}");
        _scene.GetTree().Quit();
    }

    private void Fail(string message)
    {
        _finished = true;
        GD.PrintErr($"BLANK_WORLD_FAILED leg={_leg} phase={_phase} {message}");
        _scene.GetTree().Quit(1);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
