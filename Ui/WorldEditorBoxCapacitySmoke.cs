using Godot;
using Train.Domain.Commands;
using Train.Domain.Voxels;
using Train.Domain.WorldEditor;

namespace Train;

/// <summary>Real Godot coverage for the box-selection cap and stable rectangle filtering.</summary>
internal sealed class WorldEditorBoxCapacitySmoke(WorldEditorScene scene)
{
    private const int FixtureMargin = 128;
    private readonly List<WorldVoxelCoord> _added = new();
    private int _phase;
    private ulong _physicsReadyAt;
    private ulong _physicsDeadline;

    private VoxelDevelopmentSession Session => scene.Session!;
    private WorldEditorObservationView View => scene.Observation!;

    public bool Advance()
    {
        switch (_phase)
        {
            case 0:
                AddVisibleFixture();
                scene.Refresh();
                _physicsReadyAt = Engine.GetPhysicsFrames() + 2;
                _physicsDeadline = Engine.GetPhysicsFrames() + 180;
                _phase++;
                return false;
            case 1:
                if (Engine.GetPhysicsFrames() > _physicsDeadline)
                    throw new InvalidOperationException("box-capacity fixture collider was not published within 180 physics frames");
                if (Engine.GetPhysicsFrames() < _physicsReadyAt ||
                    (_added.Count > 0 && !scene.Colliders.QueryPublishedCellInterior(scene.GetWorld3D(), _added[0])))
                    return false;
                CheckRealSelection();
                return true;
            default:
                return true;
        }
    }

    private void AddVisibleFixture()
    {
        var sample = Session.QueryDisplayPoses();
        var rectangle = View.WorldRect;
        var initial = WorldEditorObservationView.ProjectBoxCandidates(
            Session, sample, scene.Camera, rectangle, out _).Length;
        var required = WorldEditorObservationView.BoxSelectionCandidateLimit + FixtureMargin - initial;
        if (required <= 0)
            return;

        var targets = new List<VoxelCellTarget>();
        var bounds = Session.World.Bounds;
        for (var x = 0; x < bounds.SizeX && _added.Count < required; x++)
        for (var y = 0; y < bounds.SizeY && _added.Count < required; y++)
        for (var z = 0; z < bounds.SizeZ && _added.Count < required; z++)
        {
            var cell = new WorldVoxelCoord(x, y, z);
            if (Session.Query(cell).IsOccupied)
                continue;
            var point = Center(cell);
            if (scene.Camera.IsPositionBehind(point) || !rectangle.HasPoint(scene.Camera.UnprojectPosition(point)))
                continue;
            var target = new VoxelCellTarget(cell, VoxelCellOperation.Add,
                new VoxelCellContent(WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity));
            if (!Session.ValidateCells(new(new[] { target })).Succeeded)
                continue;
            targets.Add(target);
            _added.Add(cell);
        }

        Require(_added.Count == required,
            $"world bounds supplied only {_added.Count} of {required} visible box-capacity fixture cells");
        Require(Session.SubmitEdit(new VoxelCellSetEdit(targets)).Record?.State == WorldEditState.Executed,
            "box-capacity fixture edit did not publish its colliders");
    }

    private void CheckRealSelection()
    {
        var sample = Session.QueryDisplayPoses();
        var rectangle = new Rect2(View.WorldRect.Position + new Vector2(2, 2), View.WorldRect.Size - new Vector2(4, 4));
        var first = WorldEditorObservationView.ProjectBoxCandidates(
            Session, sample, scene.Camera, rectangle, out var samples);
        var screen = scene.GetViewport().GetScreenTransform();
        GodotSmokeInput.Drag(screen * rectangle.Position, screen * rectangle.End);
        var original = View.LastBoxSelection ?? throw new InvalidOperationException("first capacity input produced no box selection");
        var originalSelection = View.Observer.SelectedObjects.ToHashSet();
        // A reload can reverse dictionary insertion order without changing any cell identity.
        Session.World.RestoreVoxels(Session.World.CaptureVoxels().Reverse().ToArray());
        var second = WorldEditorObservationView.ProjectBoxCandidates(
            Session, sample, scene.Camera, rectangle, out var repeatedSamples);
        Require(samples == repeatedSamples, "reordered world changed its sample count");
        Require(first.Length > WorldEditorObservationView.BoxSelectionCandidateLimit,
            $"box-capacity fixture produced only {first.Length} in-rectangle candidates");
        GodotSmokeInput.Drag(screen * rectangle.Position, screen * rectangle.End);
        var result = View.LastBoxSelection ?? throw new InvalidOperationException("capacity input produced no box selection");
        Require(result.Truncated && result.Candidates == WorldEditorObservationView.BoxSelectionCandidateLimit &&
                result.Samples == samples,
            $"real box selection did not stop at the cap: samples={result.Samples} candidates={result.Candidates} " +
            $"truncated={result.Truncated}");
        Require(original.Truncated == result.Truncated && original.Candidates == result.Candidates &&
                original.Rays == result.Rays && original.Objects.ToHashSet().SetEquals(result.Objects) &&
                View.Observer.SelectedObjects.SetEquals(originalSelection),
            "reversing world storage order changed the real capped selection");
        Require(result.Rays <= result.Candidates && result.Objects.All(item =>
                first.Take(result.Candidates).Any(candidate => candidate.Object == item)),
            "box selection queried or selected objects beyond the candidate cap");
        var visibleCell = result.Objects.First(item => item.Kind == ObservedObjectKind.WorldCell);
        // Put this known visible cell after the old global dictionary budget. The local rectangle must still find
        // it: unrelated samples before it must neither consume the ray budget nor report truncation.
        Session.World.RestoreVoxels(Session.World.CaptureVoxels().OrderBy(pair => pair.Key == visibleCell.Cell).ToArray());
        Require(Session.World.OccupiedVoxels().Skip(WorldEditorObservationView.BoxSelectionCandidateLimit)
                .Any(pair => pair.Key == visibleCell.Cell), "small-box target is not beyond the old global sample cap");
        var point = second.First(candidate => candidate.Object == visibleCell).Screen;
        var narrow = new Rect2(point - new Vector2(4, 4), new Vector2(8, 8));
        GodotSmokeInput.Drag(screen * narrow.Position, screen * narrow.End);
        Require(View.LastBoxSelection is { Truncated: false, Candidates: > 0 } small && small.Samples == samples &&
                View.Observer.SelectedObjects.Contains(visibleCell),
            "small real box in a world above the cap lost its visible cell or was incorrectly truncated");
        var retained = View.Observer.SelectedObjects.ToHashSet();
        var middle = rectangle.GetCenter();
        GodotSmokeInput.Drag(screen * middle, screen * (middle + new Vector2(12, 0)));
        Require(View.Observer.SelectedObjects.SetEquals(retained) && View.LastBoxSelection is { Candidates: 0, Rays: 0 },
            "zero-area input drag cleared selection instead of preserving it");
        GD.Print($"WORKSPACE_MULTI_CAP samples={result.Samples} in_rectangle={first.Length} " +
                 $"candidates={result.Candidates} rays={result.Rays} selected={result.Objects.Count} " +
                 $"truncated={result.Truncated.ToString().ToLowerInvariant()} deterministic=true filter_before_cap=true " +
                 $"elapsed_ms={result.ElapsedMilliseconds:0.###}");
    }

    private static Vector3 Center(WorldVoxelCoord cell) =>
        new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
