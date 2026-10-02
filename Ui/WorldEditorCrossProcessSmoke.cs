using System.Text;
using System.Text.Json.Nodes;
using Godot;
using Train.Domain.WorldEditor;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train;

/// <summary>One process leg of the formal world editor comparison; the PowerShell entry starts each leg separately.</summary>
internal sealed class WorldEditorCrossProcessSmoke
{
    public const string LegEnvironmentVariable = "TRAIN_WORLD_EDITOR_CROSS_LEG";
    public const string EvidenceEnvironmentVariable = "TRAIN_WORLD_EDITOR_CROSS_EVIDENCE";
    public const int SaveAfter = 8;

    private readonly WorldEditorScene _scene;
    private readonly WorldEditorSequence _sequence;
    private readonly string _leg;
    private readonly string _evidence;
    private int _index;
    private int _waitFrames;
    private bool _started;
    private bool _pending;
    private bool _finished;
    private string? _input;
    private ObservationCameraPose _targetPoseBeforeEdit;

    public WorldEditorCrossProcessSmoke(WorldEditorScene scene)
    {
        _scene = scene;
        _sequence = WorldEditorSequences.Find(WorldEditorSequences.CrossProcessComparisonId)
            ?? throw new InvalidOperationException("cross-process sequence is not registered");
        _leg = System.Environment.GetEnvironmentVariable(LegEnvironmentVariable) ?? "";
        _evidence = System.Environment.GetEnvironmentVariable(EvidenceEnvironmentVariable) ?? "";
        if (_leg is not ("continuous" or "save" or "resume" or "observe" or "failure" or "adaptation") ||
            string.IsNullOrWhiteSpace(_evidence))
            throw new InvalidOperationException("cross-process leg and evidence directory are required");
        _index = _leg == "resume" ? SaveAfter : 0;
    }

    private VoxelDevelopmentSession Session => _scene.Session!;
    private WorldEditorObserver Observer => _scene.Observation!.Observer;

    public void Advance()
    {
        if (_finished)
            return;
        try
        {
            if (_leg == "failure")
            {
                VerifyFailures();
                Finish();
                return;
            }
            if (_leg == "adaptation")
            {
                VerifyAdaptation();
                Finish();
                return;
            }

            if (!_started)
            {
                _started = true;
                Require(Session.SimulationPaused, "entry must be paused");
                Require(Session.WorldSource?.ConfigurationId == _sequence.ConfigurationId, "wrong formal configuration");
                var initial = _leg == "resume" ? Read(SaveAfter) : AuthorityCodec.Checkpoint(Session, 0, "establish");
                Record(initial.Index, initial.Input);
                return;
            }

            if (_pending)
            {
                if (_waitFrames-- > 0)
                    return;
                _scene.Refresh();
                VerifyGodot();
                if (_leg == "observe")
                    DisturbObservation(_index);
                Record(_index, _input!);
                _pending = false;
                if (_leg == "save" && _index == SaveAfter)
                {
                    SaveAndFinish();
                    return;
                }
                if (_index == _sequence.Steps.Count)
                    Finish();
                return;
            }

            var step = _sequence.Steps[_index];
            if (_leg == "observe" && _index == 8)
                _targetPoseBeforeEdit = Observer.Pose;
            _input = step.Operation.InputIdentity(Session);
            var actual = step.Operation.Apply(Session, step.ConfirmEndHistory);
            Require(actual == step.ExpectedStatus,
                $"step {_index + 1} {step.Operation.Describe()} status {actual}, expected {step.ExpectedStatus}");
            foreach (var check in step.Checkpoint)
                Require(WorldEditorCheck.Observe(Session, check.Observation) == check.Expected,
                    $"step {_index + 1} {check.Observation} differs from {check.Expected}");
            _index++;
            _pending = true;
            _waitFrames = _leg == "observe" ? _index % 3 + 3 : 2;
            GD.Print($"CROSS_STEP leg={_leg} index={_index} boundary={Session.EditBoundary} status={actual} input={_input}");
            if (_index == 9)
            {
                var atThree = Session.QueryEditLog().Where(item => item.ExecutedAt?.Step == 3).ToArray();
                Require(atThree.Any(item => item.State.ToString() == "Rejected") &&
                        atThree.Last().State.ToString() == "Executed",
                    "invalid queued edit was not rejected before independent input continued");
                GD.Print("CROSS_PENDING_REJECTED_CONTINUED step=3");
            }
        }
        catch (Exception exception)
        {
            _finished = true;
            GD.PrintErr($"CROSS_FAILED leg={_leg} index={_index} boundary={Session.EditBoundary} {exception}");
            _scene.GetTree().Quit(1);
        }
    }

    private void Record(int index, string input)
    {
        var checkpoint = AuthorityCodec.Checkpoint(Session, index, input);
        var context = AuthorityCodec.Context(Session, null);
        Directory.CreateDirectory(_evidence);
        if (_leg == "continuous")
        {
            File.WriteAllText(PathFor(index), AuthorityCodec.EncodeEvidence(context, checkpoint), Encoding.UTF8);
        }
        else
        {
            var expected = Read(index);
            var evidence = File.ReadAllText(PathFor(index), Encoding.UTF8);
            Require(AuthorityCodec.TryReadEvidence(evidence, out var expectedContext, out _),
                $"checkpoint {index} evidence cannot be decoded");
            var difference = AuthorityComparison.Compare(
                new AuthorityObservation(expectedContext, new[] { expected }),
                new AuthorityObservation(context, new[] { checkpoint }));
            foreach (var line in difference.Lines())
                GD.Print($"CROSS_COMPARE leg={_leg} " + line);
            Require(difference.Equal, $"checkpoint {index} differs: {difference.Detail}");
        }

        GD.Print($"CROSS_CHECKPOINT leg={_leg} index={index} input={input} boundary={checkpoint.Boundary} digest={checkpoint.Digest} " +
                 $"next_entity_id={Session.NextEntityId} pending={Session.QueryPendingEdits().Count} paused={Session.SimulationPaused.ToString().ToLowerInvariant()}");
        if (index == 0 || index == SaveAfter)
            GD.Print($"CROSS_CONTEXT leg={_leg} build={context.BuildId} content={context.ContentDigest} source={context.SourceKind}/{context.ConfigurationId} " +
                     $"os={context.OperatingSystem} arch={context.Architecture} runtime={context.Framework}");
    }

    private AuthorityCheckpoint Read(int index)
    {
        var json = File.ReadAllText(PathFor(index), Encoding.UTF8);
        Require(AuthorityCodec.TryReadEvidence(json, out _, out var checkpoint),
            $"checkpoint {index} evidence cannot be decoded");
        return checkpoint;
    }

    private string PathFor(int index) => Path.Combine(_evidence, $"{index:D2}-evidence.json");

    private void VerifyGodot()
    {
        Require(_scene.Meshes.LastWorldVoxelCount == Session.World.VoxelCount && _scene.Tracks.RailSolidCount > 0,
            $"Godot world display differs: world={Session.World.VoxelCount} mesh_world={_scene.Meshes.LastWorldVoxelCount} rails={_scene.Tracks.RailSolidCount}");
        Require(_scene.Colliders.LastWorldVoxelCount == Session.World.VoxelCount && _scene.Colliders.ShapeCount > 0,
            $"Godot collision differs: world={Session.World.VoxelCount} collider_world={_scene.Colliders.LastWorldVoxelCount} shapes={_scene.Colliders.ShapeCount}");
        Require(_scene.Colliders.QueryPublishedCellInterior(_scene.GetWorld3D(), StraightRailFixture.LeftRailSample),
            "Godot published collision missed the rail sample");
        var probeExists = Session.Query(WorldEditorSequences.EditProbe).Voxel is not null;
        var publishedProbe = _scene.Colliders.QueryPublishedCellInterior(_scene.GetWorld3D(), WorldEditorSequences.EditProbe);
        Require(probeExists == (_index is >= 4 and <= 9) && publishedProbe == probeExists,
            $"edited probe collision lifecycle differs: index={_index} domain={probeExists} published={publishedProbe}");
        Require((_scene.Meshes.FaceCount > 0) == probeExists,
            $"edited probe display lifecycle differs: index={_index} faces={_scene.Meshes.FaceCount}");
        foreach (var vehicle in Session.Vehicles)
            Require(_scene.Composites.VisualFor(vehicle.Id) is not null, $"Godot visual missing vehicle {vehicle.Id}");
        GD.Print($"CROSS_GODOT leg={_leg} index={_index} voxels={Session.World.VoxelCount} " +
                 $"faces={_scene.Meshes.FaceCount} rails={_scene.Tracks.RailSolidCount} shapes={_scene.Colliders.ShapeCount} vehicles={Session.Vehicles.Count} rail_hit=true probe_hit={publishedProbe.ToString().ToLowerInvariant()}");
    }

    private void DisturbObservation(int index)
    {
        _ = Session.QueryTrainAssemblies();
        _ = Session.QueryPendingEdits();
        _ = _scene.Observation!.Entries.Count;
        if (index == 1)
        {
            var bridge = Session.Vehicles.OrderByDescending(item => item.Id.Value).First();
            Require(Observer.Select(ObservedObject.Vehicle(bridge.Id)) == WorldEditorObservationStatus.Selected, "select bridge");
            Require(Observer.FocusSelection() == WorldEditorObservationStatus.Focused, "focus bridge");
        }
        else if (index == 3 || index == 11)
        {
            var locomotive = Session.QueryTrainAssemblies()[0].Vehicles[0].VehicleId;
            Require(Observer.Select(ObservedObject.Vehicle(locomotive)) == WorldEditorObservationStatus.Selected, "select locomotive");
            Require(Observer.FollowSelection() == WorldEditorObservationStatus.Following, "follow locomotive");
        }
        else if (index == 4)
        {
            Require(Observer.Select(ObservedObject.WorldCell(WorldEditorSequences.EditProbe)) == WorldEditorObservationStatus.Selected,
                "select edit probe");
            Require(Observer.FocusSelection() == WorldEditorObservationStatus.Focused, "focus edit probe");
        }
        else if (index == 9)
        {
            Require(Observer.Selection is null && Observer.Target is null && Observer.Mode == WorldEditorCameraMode.Free,
                "removed target left a stale selection or camera target");
            Require(Observer.LastNotice?.Kind == WorldEditorNoticeKind.TargetLost &&
                    Observer.Pose.Position == _targetPoseBeforeEdit.Position,
                "target loss did not preserve the camera pose");
            GD.Print($"CROSS_TARGET_LOST {Observer.LastNotice?.Describe()}");
        }
        else if (index % 2 == 0 && (index < 4 || index > 9))
            Observer.Free();
        _scene.Observation.Present(0.016, Session.QueryDisplayPoses());
        GD.Print($"CROSS_OBSERVE index={index} mode={Observer.Mode} panel={_scene.Observation.InspectionText.Length} " +
                 $"frame_batch={index % 3 + 4}");
    }

    private void SaveAndFinish()
    {
        var before = AuthorityCodec.Checkpoint(Session, SaveAfter, Read(SaveAfter).Input);
        var result = _scene.SaveWorld();
        Require(result.Status == WorldSaveStatus.Saved && result.SaveId is not null, $"save failed: {result.Status}");
        var after = AuthorityCodec.Checkpoint(Session, SaveAfter, before.Input);
        Require(after.Canonical == before.Canonical, "save changed authoritative state");
        GD.Print($"CROSS_SAVED id={result.SaveId:D} index={SaveAfter} boundary={Session.EditBoundary} pending={Session.QueryPendingEdits().Count}");
        Finish();
    }

    private void VerifyFailures()
    {
        Require(Session.SimulationPaused, "fixed configuration is not paused");
        Require(Session.StartTrain(Train.Domain.Rails.RailHeading.AlongAlignment).Succeeded, "start failure setup");
        Require(Session.AdvanceSimulation(1).Succeeded, "advance failure setup");
        var initial = AuthorityCodec.Document(AuthorityCodec.Capture(Session)).Canonical;
        var cancel = Session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.SwitchConfiguration, WorldChangeChoice.Cancel,
            WorldEditorConfigurations.StraightRailEmptyId));
        Require(cancel.Status == WorldChangeStatus.Cancelled && Capture() == initial, "cancel replaced the current session");
        GD.Print("CROSS_PROTECTION cancel=kept");

        var saved = _scene.SaveWorld();
        Require(saved.Status == WorldSaveStatus.Saved && saved.SaveId is not null, "failure setup save");
        Require(Session.AdvanceSimulation(1).Succeeded, "dirty after save");
        var current = Capture();
        var main = _scene.GetNode<Main>("/root/Main");
        var path = new FileWorldSaveStore(main.IsolatedUserDataDirectory).PathFor(saved.SaveId!.Value);
        var file = new FileInfo(path);
        file.IsReadOnly = true;
        try
        {
            var failedSave = Session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.SwitchConfiguration,
                WorldChangeChoice.SaveThenSwitch, WorldEditorConfigurations.StraightRailEmptyId));
            Require(failedSave.Status == WorldChangeStatus.SaveFailed && Capture() == current,
                $"save failure did not protect the session: {failedSave.Status}");
            GD.Print($"CROSS_PROTECTION save_failure={failedSave.SaveStatus} kept=true");
        }
        finally
        {
            file.IsReadOnly = false;
        }

        var absent = Session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Load,
            WorldChangeChoice.Discard, SaveId: Guid.NewGuid()));
        Require(absent.Status == WorldChangeStatus.CandidateFailed && Capture() == current,
            "missing candidate replaced the current session");
        GD.Print($"CROSS_PROTECTION candidate_failure={absent.CandidateStatus} kept=true");

        var original = File.ReadAllText(path, Encoding.UTF8);
        var root = JsonNode.Parse(original) ?? throw new InvalidOperationException("saved JSON could not be parsed");
        root["world_source"]!["configuration_version"] = 99;
        File.WriteAllText(path, root.ToJsonString(), Encoding.UTF8);
        var bad = Session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Load,
            WorldChangeChoice.Discard, SaveId: saved.SaveId));
        Require(bad.Status == WorldChangeStatus.CandidateFailed && bad.Diagnostic is not null && Capture() == current,
            $"bad save did not protect the session: {bad.Status}");
        GD.Print($"CROSS_RECOVERY_FAILED stage={bad.Diagnostic!.Stage} subject={bad.Diagnostic.Subject} " +
                 $"relationship={bad.Diagnostic.Relationship} status={bad.CandidateStatus} kept=true save={saved.SaveId:D}");
        GD.Print($"CROSS_PROTECTION bad_save={bad.CandidateStatus} kept=true");
    }

    private void VerifyAdaptation()
    {
        Require(Session.SimulationPaused, "adaptation entry is not paused");
        var saved = _scene.SaveWorld();
        Require(saved.Status == WorldSaveStatus.Saved && saved.SaveId is not null, "adaptation setup save failed");
        var main = _scene.GetNode<Main>("/root/Main");
        var path = new FileWorldSaveStore(main.IsolatedUserDataDirectory).PathFor(saved.SaveId!.Value);
        var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))
            ?? throw new InvalidOperationException("adaptation save is invalid JSON");
        var dependency = root["world_source"]!["content_dependencies"]![0]!;
        var previous = dependency["semantic_revision"]!.GetValue<string>();
        dependency["semantic_revision"] = previous[..^1] + (previous[^1] == '0' ? "1" : "0");
        File.WriteAllText(path, root.ToJsonString(), Encoding.UTF8);
        var result = Session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Load,
            WorldChangeChoice.Discard, SaveId: saved.SaveId));
        Require(result.Status == WorldChangeStatus.Completed && Session.SimulationPaused &&
                result.Adaptations is { Count: > 0 } &&
                result.Adaptations.Any(item => item.Relationship == "semantic_revision"),
            $"ordinary content adaptation did not report the changed dependency: {result.Status}");
        GD.Print($"CROSS_ADAPTATION status={result.Status} relationship=semantic_revision paused=true " +
                 $"save={saved.SaveId:D} count={result.Adaptations!.Count} domain_comparison=separate");
    }

    private string Capture() => AuthorityCodec.Document(AuthorityCodec.Capture(Session)).Canonical;

    private void Finish()
    {
        _finished = true;
        GD.Print($"CROSS_DONE leg={_leg} checkpoints={_index + 1}");
        _scene.GetTree().Quit();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
