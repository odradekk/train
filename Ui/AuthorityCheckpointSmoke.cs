using System.Text;
using Godot;
using Train.Domain.Commands;
using Train.Domain.Playground;
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

    private readonly PlaygroundScene _scene;
    private readonly bool _resume;
    private bool _finished;

    public AuthorityCheckpointSmoke(PlaygroundScene scene, string? saveId)
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
        var sequence = PlaygroundSequences.Find(PlaygroundSequences.AuthorityManufactureFractureId)
            ?? throw new InvalidOperationException("authority sequence is not registered");
        var report = AuthorityValidation.Execute(sequence, Session, BuildId());
        foreach (var line in report.Lines())
            GD.Print("AUTHORITY_CHECKPOINT_SEQUENCE " + line);
        if (!report.Passed)
            throw new InvalidOperationException($"sequence {sequence.Id} failed");
        GD.Print($"AUTHORITY_CHECKPOINT_SEQUENCE_PASSED {sequence.Id} steps={report.Sequence.Steps.Count}");

        var saved = AuthorityCodec.Document(AuthorityCodec.Capture(Session));
        var result = _scene.SaveWorld();
        if (result.Status != WorldSaveStatus.Saved || result.SaveId is not { } saveId)
            throw new InvalidOperationException($"save status {result.Status}");
        var afterSave = AuthorityCodec.Document(AuthorityCodec.Capture(Session));
        if (!string.Equals(saved.Canonical, afterSave.Canonical, StringComparison.Ordinal))
            throw new InvalidOperationException("saving changed the authority document");
        Write("saved-canonical.json", saved.Canonical);
        PrintState("AUTHORITY_CHECKPOINT_SAVED", saveId, saved.Digest);
        Continue("continuation-canonical.json", "AUTHORITY_CHECKPOINT_CONTINUATION");
    }

    private void Resume()
    {
        var restored = AuthorityCodec.Document(AuthorityCodec.Capture(Session));
        Write("restored-canonical.json", restored.Canonical);
        PrintState("AUTHORITY_CHECKPOINT_RESTORED", Session.SaveId, restored.Digest);
        CompareFile("saved-canonical.json", restored.Canonical, "saved");
        Continue("restored-continuation-canonical.json", "AUTHORITY_CHECKPOINT_CONTINUATION");
        var continuation = AuthorityCodec.Encode(AuthorityCodec.Capture(Session));
        CompareFile("continuation-canonical.json", continuation, "continuation");
    }

    private void CompareFile(string fileName, string actual, string label)
    {
        var directory = System.Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException($"{DirectoryEnvironmentVariable} is required");
        var expectedText = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, fileName)));
        var context = AuthorityCodec.Context(Session, BuildId());
        AuthorityCheckpoint Point(string text, string input) =>
            new(0, input, Session.EditBoundary, text, AuthorityCodec.Digest(text));
        var difference = AuthorityComparison.Compare(
            new AuthorityObservation(context, new[] { Point(expectedText, "process-1 " + label) }),
            new AuthorityObservation(context, new[] { Point(actual, "process-2 " + label) }));
        foreach (var line in difference.Lines())
            GD.Print($"AUTHORITY_CHECKPOINT_COMPARE {label} " + line);
        if (!difference.Equal)
            throw new InvalidOperationException($"{label} checkpoints differ: {difference.Detail}");
    }

    private void Continue(string fileName, string marker)
    {
        var status = PlaygroundOperation.Submit(
            new VoxelAddEdit(ContinuationCell, PlaygroundSequences.EditVoxelId, VoxelOrientation.Identity)).Apply(Session);
        if (!string.Equals(status, "Added", StringComparison.Ordinal))
            throw new InvalidOperationException($"continuation status {status}");
        var document = AuthorityCodec.Document(AuthorityCodec.Capture(Session));
        Write(fileName, document.Canonical);
        GD.Print($"{marker} digest={document.Digest} cell={ContinuationCell.X},{ContinuationCell.Y},{ContinuationCell.Z}");
    }

    private void PrintState(string marker, Guid? saveId, string digest)
    {
        var fault = Session.Fault is null ? "null" : Session.Fault.ExceptionType;
        GD.Print(
            $"{marker} id={saveId:D} digest={digest} next={Session.NextEntityId} vehicles={Session.QueryComposites().Count} fault={fault}");
        var observation = AuthorityCodec.Observe(Session, BuildId(), "saved");
        foreach (var line in observation.Lines())
            GD.Print("AUTHORITY_CHECKPOINT_LINE " + line);
    }

    private void Write(string fileName, string canonical)
    {
        var directory = System.Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException($"{DirectoryEnvironmentVariable} is required");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, fileName), Encoding.UTF8.GetBytes(canonical));
    }

    private static string? BuildId() => System.Environment.GetEnvironmentVariable(BuildEnvironmentVariable);
}
