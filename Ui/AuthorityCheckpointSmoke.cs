using Train.Core.Content.Voxels;
using System.Text;
using Godot;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

/// <summary>
/// Headless entrance for one formal authority sequence, a save, and the same continuation in a second process.
/// </summary>
internal sealed class AuthorityCheckpointSmoke
{
    public const string DirectoryEnvironmentVariable = "TRAIN_AUTHORITY_CHECKPOINT_DIR";
    public const string BuildEnvironmentVariable = "TRAIN_AUTHORITY_BUILD";
    private static readonly WorldVoxelCoord ContinuationCell = new(10, 10, 10);

    private readonly WorldEditorScene _scene;
    private readonly bool _resume;
    private bool _finished;

    public AuthorityCheckpointSmoke(WorldEditorScene scene, string? saveId)
    {
        _scene = scene;
        _resume = Guid.TryParse(saveId, out _);
    }

    private VoxelDevelopmentSession Session => _scene.Session!;

    public void Advance()
    {
        if (_finished)
            return;
        _finished = true;
        try
        {
            if (_resume)
                Resume();
            else
                Establish();
            _scene.GetTree().Quit();
        }
        catch (Exception exception)
        {
            GD.PrintErr($"AUTHORITY_CHECKPOINT_FAILED {exception.Message}");
            _scene.GetTree().Quit(1);
        }
    }

    private void Establish()
    {
        var sequence = WorldEditorSequences.Find(WorldEditorSequences.AuthorityManufactureFractureId)
            ?? throw new InvalidOperationException("authority sequence is not registered");
        var report = AuthorityValidation.Execute(sequence, Session, BuildId());
        foreach (var line in report.Lines())
            GD.Print("AUTHORITY_CHECKPOINT_SEQUENCE " + line);
        if (!report.Passed)
            throw new InvalidOperationException($"sequence {sequence.Id} failed");
        GD.Print($"AUTHORITY_CHECKPOINT_SEQUENCE_PASSED {sequence.Id} steps={report.Sequence.Steps.Count}");

        if (report.Observation is not { } observation)
            throw new InvalidOperationException("authority observation is missing");
        var savedCheckpoint = observation.Checkpoints[^1];
        if (savedCheckpoint.Index != sequence.Steps.Count)
            throw new InvalidOperationException($"saved checkpoint index {savedCheckpoint.Index}");
        var captured = AuthorityCodec.Document(AuthorityCodec.Capture(Session));
        if (!string.Equals(savedCheckpoint.Canonical, captured.Canonical, StringComparison.Ordinal))
            throw new InvalidOperationException("sequence checkpoint does not match the live capture");
        var result = _scene.SaveWorld();
        if (result.Status != WorldSaveStatus.Saved || result.SaveId is not { } saveId)
            throw new InvalidOperationException($"save status {result.Status}");
        var afterSave = AuthorityCodec.Document(AuthorityCodec.Capture(Session));
        if (!string.Equals(savedCheckpoint.Canonical, afterSave.Canonical, StringComparison.Ordinal))
            throw new InvalidOperationException("saving changed the authority document");
        Write("saved-canonical.json", savedCheckpoint.Canonical);
        Write("saved-evidence.json", AuthorityCodec.EncodeEvidence(observation.Context, savedCheckpoint));
        PrintState("AUTHORITY_CHECKPOINT_SAVED", saveId, savedCheckpoint.Digest, savedCheckpoint);
        var continuation = ContinuationOperation();
        var continuationIdentity = continuation.InputIdentity(Session);
        var status = continuation.Apply(Session);
        if (!string.Equals(status, "Added", StringComparison.Ordinal))
            throw new InvalidOperationException($"continuation status {status}");
        var continued = AuthorityCodec.Checkpoint(Session, savedCheckpoint.Index + 1, continuationIdentity);
        Write("continuation-canonical.json", continued.Canonical);
        Write("continuation-evidence.json", AuthorityCodec.EncodeEvidence(AuthorityCodec.Context(Session, BuildId()), continued));
        GD.Print($"AUTHORITY_CHECKPOINT_CONTINUATION digest={continued.Digest} cell={ContinuationCell.X},{ContinuationCell.Y},{ContinuationCell.Z}");
    }

    private void Resume()
    {
        var sequence = WorldEditorSequences.Find(WorldEditorSequences.AuthorityManufactureFractureId)
            ?? throw new InvalidOperationException("authority sequence is not registered");
        // The loaded save is after the last command, so resolving "latest" again names a different vehicle.
        // Replay the formal sequence for the identity captured at that command; the save supplies the state.
        var restored = AuthorityCodec.Checkpoint(Session, sequence.Steps.Count, ReplayLastInput(sequence));
        Write("restored-canonical.json", restored.Canonical);
        PrintState("AUTHORITY_CHECKPOINT_RESTORED", Session.SaveId, restored.Digest, restored);
        CompareEvidence("saved-evidence.json", restored, "saved");
        var continuation = ContinuationOperation();
        var continuationIdentity = continuation.InputIdentity(Session);
        var status = continuation.Apply(Session);
        if (!string.Equals(status, "Added", StringComparison.Ordinal))
            throw new InvalidOperationException($"continuation status {status}");
        var continued = AuthorityCodec.Checkpoint(Session, sequence.Steps.Count + 1, continuationIdentity);
        Write("restored-continuation-canonical.json", continued.Canonical);
        GD.Print($"AUTHORITY_CHECKPOINT_CONTINUATION digest={continued.Digest} cell={ContinuationCell.X},{ContinuationCell.Y},{ContinuationCell.Z}");
        CompareEvidence("continuation-evidence.json", continued, "continuation");
    }

    private void CompareEvidence(string fileName, AuthorityCheckpoint actual, string label)
    {
        var json = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(EvidenceDirectory(), fileName)));
        if (!AuthorityCodec.TryReadEvidence(json, out var expectedContext, out var expectedCheckpoint))
            throw new InvalidOperationException($"{fileName} is not authority evidence");
        GD.Print(
            $"AUTHORITY_CHECKPOINT_COMPARE {label} checkpoint {expectedCheckpoint.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"input {expectedCheckpoint.Input} boundary {expectedCheckpoint.Boundary}");
        var difference = AuthorityComparison.Compare(
            new AuthorityObservation(expectedContext, new[] { expectedCheckpoint }),
            new AuthorityObservation(AuthorityCodec.Context(Session, BuildId()), new[] { actual }));
        foreach (var line in difference.Lines())
            GD.Print($"AUTHORITY_CHECKPOINT_COMPARE {label} " + line);
        if (!difference.Equal)
            throw new InvalidOperationException($"{label} checkpoints differ: {difference.Detail}");
    }

    private string ReplayLastInput(WorldEditorSequence sequence)
    {
        var reloader = _scene.GetNode<Main>("/root/Main").Reloader
            ?? throw new InvalidOperationException("content is not loaded");
        var replay = AuthorityValidation.Run(sequence, reloader.Catalog, reloader.Composites);
        if (replay.Observation is not { } observation || !replay.Passed)
            throw new InvalidOperationException(string.Join(System.Environment.NewLine, replay.Lines()));
        var checkpoint = observation.Checkpoints[^1];
        if (checkpoint.Index != sequence.Steps.Count)
            throw new InvalidOperationException($"replayed checkpoint index {checkpoint.Index}");
        return checkpoint.Input;
    }

    private static WorldEditorOperation ContinuationOperation() =>
        WorldEditorOperation.Submit(new VoxelAddEdit(ContinuationCell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity));

    private void PrintState(string marker, Guid? saveId, string digest, AuthorityCheckpoint checkpoint)
    {
        var fault = Session.Fault is null ? "null" : Session.Fault.ExceptionType;
        var buildId = AuthorityEnvironment.CurrentBuildId;
        var buildFile = AuthorityEnvironment.CurrentBuildFile;
        if (string.IsNullOrWhiteSpace(buildId) || string.IsNullOrWhiteSpace(buildFile))
            throw new InvalidOperationException("authority build identity is unavailable");
        GD.Print($"AUTHORITY_CHECKPOINT_BUILD id={buildId} file={buildFile}");
        GD.Print(
            $"{marker} id={saveId:D} digest={digest} next={Session.NextEntityId} vehicles={Session.QueryComposites().Count} fault={fault}");
        var observation = new AuthorityObservation(AuthorityCodec.Context(Session, BuildId()), new[] { checkpoint });
        foreach (var line in observation.Lines())
            GD.Print("AUTHORITY_CHECKPOINT_LINE " + line);
    }

    private void Write(string fileName, string canonical)
    {
        System.IO.Directory.CreateDirectory(EvidenceDirectory());
        File.WriteAllBytes(Path.Combine(EvidenceDirectory(), fileName), Encoding.UTF8.GetBytes(canonical));
    }

    private static string EvidenceDirectory()
    {
        var directory = System.Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException($"{DirectoryEnvironmentVariable} is required");
        return directory;
    }

    private static string? BuildId() => System.Environment.GetEnvironmentVariable(BuildEnvironmentVariable);
}
