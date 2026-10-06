using Train.Ui;
using Train.Core.Saves;
using Train.Core.Content.Voxels;
using Godot;
using Train.Domain.Commands;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Domain.WorldEditor;

namespace Train;

/// <summary>Exercises every editor leave entry point before the workspace save-as process creates its fixtures.</summary>
internal sealed class WorldEditorLeaveSmoke(WorldEditorScene scene)
{
    private const string OriginalName = "离开保护原存档";
    private const string CopyName = "离开保护重载副本";
    private static readonly VoxelWorldIdentity BlankIdentity = CreateBlankIdentity();
    private static readonly WorldVoxelCoord DirtyCell = new(2, 2, 2);
    private static readonly WorldVoxelCoord ReloadCell = new(3, 2, 2);
    private static readonly WorldVoxelCoord RefusedCell = new(4, 2, 2);

    private readonly Main _main = scene.GetNode<Main>("/root/Main");
    private readonly GameConsole _console = scene.GetNode<Main>("/root/Main").GetChildren().OfType<GameConsole>().Single();
    private readonly List<(string Name, Action Request)> _cancelRoutes = [];
    private int _phase;
    private int _route;
    private Snapshot? _before;
    private Guid _originalId;
    private Guid _copyId;
    private byte[]? _originalBytes;
    private string? _originalPath;
    private bool _sawReloadCopy;
    private bool _finished;
    private bool _failed;

    public bool Advance()
    {
        if (_finished)
            return true;

        Guard(() => AdvanceCore(scene.Session ?? throw new InvalidOperationException("leave smoke has no editor session")));
        return _finished && !_failed;
    }

    private void AdvanceCore(VoxelDevelopmentSession session)
    {
        switch (_phase)
        {
            case 0:
                Require(session.SaveId is null && session.HasUnsavedChanges && session.SimulationPaused &&
                        session.Identity == BlankIdentity && session.World.VoxelCount == 0,
                    "leave smoke did not start in the expected unsaved blank world");
                ConfigureCancelRoutes();
                var reloadWorld = session.World;
                scene.Press("reload_saved");
                Require(!scene.PromptVisible && !session.IsChangingWorld && ReferenceEquals(session.World, reloadWorld) &&
                        session.SaveId is null && session.HasUnsavedChanges,
                    "reload signal replaced or started changing a never-saved world");
                _phase = 1;
                return;

            case 1:
                if (_route == _cancelRoutes.Count)
                {
                    SaveReloadFixture(session);
                    _phase = 4;
                    return;
                }

                PrepareRoute(session);
                _cancelRoutes[_route].Request();
                Require(scene.PromptVisible && session.IsChangingWorld,
                    $"{_cancelRoutes[_route].Name} did not enter the shared leave confirmation");
                AssertChangingRejectsInput(session);
                _phase = 2;
                return;

            case 2:
                scene.PressPrompt("cancel");
                Require(!session.IsChangingWorld && !scene.PromptVisible,
                    $"{_cancelRoutes[_route].Name} did not finish cancellation");
                AssertPreserved(session, _before!, $"{_cancelRoutes[_route].Name} cancellation");
                if (!_before!.Paused)
                    Require(session.PauseSimulation().Succeeded, "could not pause after observing restored running state");
                _before = null;
                _route++;
                _phase = 1;
                return;

            case 4:
                Require(session.IsChangingWorld && scene.PromptVisible, "reload button omitted dirty-world confirmation");
                AssertChangingRejectsInput(session);
                scene.PressPrompt("cancel");
                Require(!session.IsChangingWorld && !scene.PromptVisible, "reload cancellation did not complete");
                AssertPreserved(session, _before!, "reload cancellation");
                Require(session.PauseSimulation().Succeeded, "could not pause after reload cancellation");
                BreakOriginalSave();
                _before = Capture(session);
                scene.Press("reload_saved");
                Require(scene.PromptVisible && session.IsChangingWorld, "broken-target reload omitted confirmation");
                scene.PressPrompt("discard");
                _phase = 6;
                return;

            case 6:
                if (session.IsChangingWorld)
                {
                    AssertChangingRejectsInput(session);
                    return;
                }
                AssertPreserved(session, _before!, "failed reload");
                RestoreOriginalSave();
                scene.SetSaveName(CopyName);
                _before = Capture(session);
                scene.Press("reload_saved");
                Require(scene.PromptVisible && session.IsChangingWorld, "save-as reload omitted confirmation");
                scene.PressPrompt("save_then_switch");
                _phase = 7;
                return;

            case 7:
                if (session.IsChangingWorld)
                {
                    AssertChangingRejectsInput(session);
                    if (session.SaveId is { } saveId && saveId != _originalId)
                    {
                        _sawReloadCopy = true;
                        _copyId = saveId;
                        Require(File.Exists(Store().PathFor(saveId)), "reload copy was not published before loading the original target");
                    }
                    return;
                }
                Require(_sawReloadCopy && _copyId != Guid.Empty && session.SaveId == _originalId &&
                        session.SimulationPaused && !session.HasUnsavedChanges && session.World.VoxelCount == 0 &&
                        File.Exists(Store().PathFor(_copyId)) && File.ReadAllBytes(_originalPath!).SequenceEqual(_originalBytes!),
                    "save-as reload did not preserve the copy and restore the original paused target");
                var result = scene.RequestWorldChange(new WorldChangeRequest(
                    WorldChangeKind.NewBlank,
                    null,
                    BlankIdentity: BlankIdentity));
                Require(result.Status == WorldChangeStatus.Switching && session.IsChangingWorld,
                    "final blank-world rebuild did not start");
                _phase = 8;
                return;

            case 8:
                if (session.IsChangingWorld)
                    return;
                Require(session.SaveId is null && session.HasUnsavedChanges && session.SimulationPaused &&
                        session.Identity == BlankIdentity && session.World.VoxelCount == 0,
                    "leave smoke did not restore the workspace smoke blank-world precondition");
                CheckFaultedConfirmation(session);
                scene.RequestWorldChange(new WorldChangeRequest(WorldChangeKind.NewBlank, null, BlankIdentity: BlankIdentity));
                Require(scene.PromptVisible && session.IsChangingWorld, "faulted replacement omitted confirmation");
                scene.PressPrompt("discard");
                _phase = 9;
                return;

            case 9:
                if (session.IsChangingWorld)
                    return;
                Require(session.Fault is null && session.SaveId is null && session.HasUnsavedChanges &&
                        session.SimulationPaused && session.World.VoxelCount == 0 && session.Identity == BlankIdentity,
                    "discarding the faulted world did not restore the workspace smoke precondition");
                DeleteOwnedSave(_copyId);
                DeleteOwnedSave(_originalId);
                GD.Print("WORKSPACE_LEAVE_PROTECTION routes=all cancel=preserved reload_failure=preserved save_as_reload=restored");
                _finished = true;
                return;
        }
    }

    private void CheckFaultedConfirmation(VoxelDevelopmentSession session)
    {
        session.EditFaultProbe = _ => throw new InvalidOperationException("injected leave-smoke failure");
        session.SubmitEdit(new VoxelAddEdit(DirtyCell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity));
        session.EditFaultProbe = null;
        var fault = session.Fault;
        Require(fault is not null && session.HasUnsavedChanges, "could not create a faulted unsaved world");
        var before = Capture(session);
        scene.Press("menu");
        Require(scene.PromptVisible && session.IsChangingWorld, "faulted leave omitted confirmation");
        scene.PressPrompt("save_then_switch");
        Require(scene.PromptVisible && session.IsChangingWorld && ReferenceEquals(session.Fault, fault),
            "faulted confirmation accepted an unavailable save");
        scene.PressPrompt("cancel");
        Require(!scene.PromptVisible && !session.IsChangingWorld && ReferenceEquals(session.Fault, fault),
            "faulted cancellation did not preserve the fault and release confirmation");
        AssertPreserved(session, before, "faulted cancellation");
    }

    private void ConfigureCancelRoutes()
    {
        _cancelRoutes.Add(("workspace button", () => scene.Press("workspace")));
        _cancelRoutes.Add(("main-menu button", () => scene.Press("menu")));
        _cancelRoutes.Add(("quit request", _main.RequestQuit));
        _cancelRoutes.Add(("window close", () => _main.Notification((int)Node.NotificationWMCloseRequest)));
        _cancelRoutes.Add(("voxel scene API", () => _main.EnterVoxelDevelopmentScene(VoxelDevelopmentSlice.Slice1)));
        _cancelRoutes.Add(("candidate editor API", RequestCandidateEditor));
        _cancelRoutes.Add(("console quit", () => _console.SubmitLine("quit")));
        _cancelRoutes.Add(("console new", () => _console.SubmitLine("voxel new 33 17 19 42")));
        _cancelRoutes.Add(("console load", () => _console.SubmitLine($"voxel load {Guid.NewGuid():D}")));
        _cancelRoutes.Add(("console reset", () => _console.SubmitLine("voxel reset")));
        _cancelRoutes.Add(("console use", () => _console.SubmitLine("voxel use straight-rail-empty")));
        _cancelRoutes.Add(("console composite reset", () => _console.SubmitLine("composite reset")));
        _cancelRoutes.Add(("direct reset", () => RequireChoice(scene.RequestWorldChange(
            new WorldChangeRequest(WorldChangeKind.Reset, null)), "direct reset")));
        _cancelRoutes.Add(("configuration editor API", () =>
            _main.EnterWorldEditor(WorldEditorConfigurations.StraightRailEmptyId)));
        _cancelRoutes.Add(("direct load", () => RequireChoice(scene.RequestWorldChange(
            new WorldChangeRequest(WorldChangeKind.Load, null, SaveId: Guid.NewGuid())), "direct load")));
    }

    private void RequestCandidateEditor()
    {
        var reloader = _main.Reloader ?? throw new InvalidOperationException("content reloader is unavailable");
        var candidate = new WorldEditorWorkspace(reloader.Catalog, reloader.Composites, Store()).NewBlank(BlankIdentity);
        _main.EnterWorldEditor(candidate);
    }

    private void PrepareRoute(VoxelDevelopmentSession session)
    {
        if (!session.HasUnsavedChanges)
            Add(session, DirtyCell);
        var running = _route % 2 == 1;
        if (running)
            Require(session.ResumeSimulation().Status == TrainMotionStatus.Resumed, "could not prepare a running leave route");
        else if (!session.SimulationPaused)
            Require(session.PauseSimulation().Status == TrainMotionStatus.Paused, "could not prepare a paused leave route");
        _before = Capture(session);
    }

    private void SaveReloadFixture(VoxelDevelopmentSession session)
    {
        if (!session.SimulationPaused)
            Require(session.PauseSimulation().Succeeded, "could not pause before the reload fixture save");
        scene.SetSaveName(OriginalName);
        var saved = scene.SaveWorld();
        Require(saved.Succeeded && saved.SaveId is not null && !session.HasUnsavedChanges,
            "could not create the reload fixture save");
        var id = saved.SaveId.GetValueOrDefault();
        _originalId = id;
        _originalPath = Store().PathFor(id);
        _originalBytes = File.ReadAllBytes(_originalPath);
        Add(session, ReloadCell);
        Require(session.ResumeSimulation().Succeeded, "could not run before reload cancellation");
        _before = Capture(session);
        scene.Press("reload_saved");
    }

    private static Snapshot Capture(VoxelDevelopmentSession session) =>
        new(session.World, session.SimulationStep, session.SaveId, session.HasUnsavedChanges, session.SimulationPaused,
            session.World.VoxelCount, session.QueryPendingEdits().Count);

    private static void AssertPreserved(VoxelDevelopmentSession session, Snapshot before, string operation) =>
        Require(ReferenceEquals(session.World, before.World) && session.SimulationStep == before.Step &&
                session.SaveId == before.SaveId && session.HasUnsavedChanges == before.Dirty &&
                session.SimulationPaused == before.Paused && session.World.VoxelCount == before.Voxels &&
                session.QueryPendingEdits().Count == before.Pending,
            $"{operation} changed the world, identity, dirty state, simulation position or run state");

    private static void AssertChangingRejectsInput(VoxelDevelopmentSession session)
    {
        Require(session.IsChangingWorld, "input-refusal check ran outside a world change");
        Require(session.SubmitEdit(new VoxelAddEdit(RefusedCell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity)).Refusal ==
                WorldEditRefusal.WorldChanging,
            "world change accepted a new edit");
        Require(session.AdvanceSimulation(1).Status == TrainMotionStatus.WorldChanging &&
                session.PauseSimulation().Status == TrainMotionStatus.WorldChanging &&
                session.ResumeSimulation().Status == TrainMotionStatus.WorldChanging,
            "world change accepted simulation control");
    }

    private void BreakOriginalSave() => File.WriteAllText(_originalPath!, "{");

    private void RestoreOriginalSave() => File.WriteAllBytes(_originalPath!, _originalBytes!);

    private FileSaveStore Store() => new(_main.IsolatedUserDataDirectory);

    private void DeleteOwnedSave(Guid id)
    {
        if (id == Guid.Empty)
            return;
        var directory = Path.GetDirectoryName(Store().PathFor(id));
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private static void Add(VoxelDevelopmentSession session, WorldVoxelCoord cell) =>
        Require(session.Add(cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity).Succeeded,
            "leave smoke could not make the world dirty");

    private static void RequireChoice(WorldChangeResult result, string operation) =>
        Require(result.Status == WorldChangeStatus.ChoiceRequired, $"{operation} did not require a choice");

    private static VoxelWorldIdentity CreateBlankIdentity()
    {
        if (!BlankWorldSource.TryIdentity(33, 17, 19, 42, out var identity))
            throw new InvalidOperationException("leave smoke blank identity is unsupported");
        return identity;
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            RestoreAndClean();
            GD.PrintErr($"WORKSPACE_FAILED leg=save leave={error}");
            scene.GetTree().Quit(1);
            _failed = true;
            _finished = true;
        }
    }

    private void RestoreAndClean()
    {
        try
        {
            if (_originalPath is not null && _originalBytes is not null && Directory.Exists(Path.GetDirectoryName(_originalPath)))
                File.WriteAllBytes(_originalPath, _originalBytes);
            DeleteOwnedSave(_copyId);
            DeleteOwnedSave(_originalId);
        }
        catch
        {
            // Preserve the assertion that caused the failure; cleanup is best-effort inside isolated test data.
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed record Snapshot(
        VoxelWorld World,
        ulong Step,
        Guid? SaveId,
        bool Dirty,
        bool Paused,
        int Voxels,
        int Pending);
}
