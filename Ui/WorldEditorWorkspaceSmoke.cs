using Godot;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

/// <summary>Assertions through the menu, workspace, shared editor and ordinary save in separate process legs.</summary>
internal sealed class WorldEditorWorkspaceSmoke(WorldEditorScene scene)
{
    public const string LegEnvironmentVariable = "TRAIN_WORLD_EDITOR_WORKSPACE_LEG";
    public const string OldIdEnvironmentVariable = "TRAIN_WORLD_EDITOR_WORKSPACE_OLD_ID";
    public const string NewIdEnvironmentVariable = "TRAIN_WORLD_EDITOR_WORKSPACE_NEW_ID";
    private const string OldName = "工作区验证原档";
    private const string NewName = "工作区验证副本";
    private static readonly WorldVoxelCoord InitialCell = new(2, 2, 2);
    private static readonly WorldVoxelCoord PendingCell = new(3, 2, 2);
    private static readonly WorldVoxelCoord CopiedCell = new(4, 2, 2);
    private static readonly WorldVoxelCoord OldOnlyCell = new(5, 2, 2);
    private static readonly WorldVoxelCoord NewOnlyCell = new(6, 2, 2);
    private static readonly WorldVoxelCoord DiscardedCell = new(7, 2, 2);
    private static readonly WorldVoxelCoord SaveContinueCell = new(8, 2, 2);
    private static string Leg => System.Environment.GetEnvironmentVariable(LegEnvironmentVariable) ?? "";
    private static int _workspaceVisits;
    private static int _editorVisits;
    private static Guid _oldId;
    private static Guid _newId;
    private int _phase;
    private readonly int _visit = ++_editorVisits;
    private bool _finished;

    public static bool Requested() => Leg is "save" or "resume-old" or "resume-new" or "verify";

    public static void FromMenu(StartMenu menu) => Guard(menu, () =>
    {
        Require(menu.GetTree().CurrentScene == menu, "menu was replaced before choosing world editor");
        menu.WorldEditorButton.EmitSignal(BaseButton.SignalName.Pressed);
        GD.Print("WORKSPACE_MENU ready=true");
    });

    public static void FromWorkspace(WorldEditorWorkspaceScene workspace) => Guard(workspace, () =>
    {
        _workspaceVisits++;
        var main = workspace.GetNode<Main>("/root/Main");
        Require(workspace.GetTree().CurrentScene == workspace, "workspace was not displayed first");
        if (Leg == "save" && _workspaceVisits == 1)
        {
            Require(workspace.Entries.Count == 0, "new process unexpectedly restored or created a save");
            workspace.SetBlankInputs(33, 17, 19, "42");
            workspace.CreateBlank();
            return;
        }

        var entries = workspace.Entries;
        Require(entries.Count == 2, "workspace did not retain the original and save-as copy");
        var oldEntry = entries.Single(item => item.Name == OldName);
        var newEntry = entries.Single(item => item.Name == NewName);
        Require(oldEntry.Id == ExpectedId(OldIdEnvironmentVariable, _oldId) &&
                newEntry.Id == ExpectedId(NewIdEnvironmentVariable, _newId),
            "workspace save identities differ from the creating process");
        Require(oldEntry.Recovery.Succeeded && newEntry.Recovery.Succeeded &&
                oldEntry.LastSavedUtc is not null && newEntry.LastSavedUtc is not null &&
                oldEntry.FormatVersion is not null && newEntry.FormatVersion is not null,
            "save-as pair is missing from the usable list");
        Require(oldEntry.ExternalDefinitions.Count > 0 && newEntry.ExternalDefinitions.Count > 0,
            "list omitted dependency hints");

        if (Leg == "save")
        {
            if (_workspaceVisits == 2)
            {
                Require(workspace.OpenSave(newEntry.Id).Succeeded,
                    "save-as copy could not be reopened for save-and-return");
                GD.Print($"WORKSPACE_LIST_OPEN id={newEntry.Id:D} leg={Leg} save_continue=true");
                return;
            }
            VerifyOpenFailure(workspace, main, oldEntry.Id);
            GD.Print($"WORKSPACE_SAVED old={oldEntry.Id:D} copy={newEntry.Id:D} cancel=true save_failure=true isolated=true");
            Done(workspace);
            return;
        }

        var entry = Leg switch
        {
            "resume-old" => oldEntry,
            "resume-new" => newEntry,
            "verify" when _workspaceVisits == 1 => oldEntry,
            "verify" => newEntry,
            _ => throw new InvalidOperationException($"Unknown workspace leg {Leg}.")
        };
        Require(workspace.OpenSave(entry.Id).Succeeded, "list selection failed to open ordinary save");
        GD.Print($"WORKSPACE_LIST_OPEN id={entry.Id:D} leg={Leg}");
    });

    public void Advance()
    {
        if (_finished)
            return;
        Guard(scene, () =>
        {
            var session = scene.Session!;
            if (_phase == 0)
            {
                Require(session.SimulationPaused && session.WorldSource?.Kind == WorldSource.BlankKind,
                    "workspace did not enter the same paused blank editor");
                Require(session.Identity.Bounds == new VoxelWorldBounds(33, 17, 19) && session.Identity.Seed == 42,
                    "blank inputs were not preserved");
                if (Leg == "save")
                {
                    if (_visit == 1)
                        CreateSaveAsPair(session);
                    else
                        SaveThenReturn(session);
                    return;
                }
                if (Leg == "verify")
                {
                    VerifyIsolatedSave(session);
                    return;
                }

                var expectedId = RequiredId(Leg == "resume-old" ? OldIdEnvironmentVariable : NewIdEnvironmentVariable);
                var expectedVoxels = Leg == "resume-old" ? 1 : 3;
                Require(session.SaveId == expectedId && session.SimulationStep == 1 && session.QueryPendingEdits().Count == 1 &&
                        session.World.VoxelCount == expectedVoxels,
                    "ordinary open lost identity, simulation position, copied state or pending input");
                Add(Leg == "resume-old" ? OldOnlyCell : NewOnlyCell);
                scene.Press("run");
                _phase = 1;
                return;
            }
            if (session.SimulationStep < 5)
                return;
            scene.Press("pause");
            scene.Refresh();
            var expected = Leg == "resume-old" ? 3 : 5;
            Require(session.SimulationPaused && session.QueryPendingEdits().Count == 0 && session.World.VoxelCount == expected &&
                    session.Query(PendingCell).Voxel is not null && session.HasUnsavedChanges,
                "new process did not continue editing and run queued commands");
            Require(scene.Meshes.LastWorldVoxelCount == expected && scene.Colliders.LastWorldVoxelCount == expected,
                "Godot presentation does not match continued world");
            var saved = scene.SaveWorld();
            Require(saved.Succeeded && saved.SaveId == session.SaveId && !session.HasUnsavedChanges,
                "continued world was not saved to its own identity");
            GD.Print($"WORKSPACE_CONTINUED leg={Leg} id={session.SaveId:D} step={session.SimulationStep} voxels={expected} pending=0 paused=true");
            _finished = true;
            Done(scene);
        });
    }

    private void CreateSaveAsPair(VoxelDevelopmentSession session)
    {
        Require(session.SaveId is null && session.HasUnsavedChanges && session.World.VoxelCount == 0 &&
                session.Vehicles.Count == 0 && session.Rails.Segments.Count == 0,
            "new world is not truly empty and unsaved");
        Add(InitialCell);
        Require(session.SubmitEdit(new VoxelAddEdit(PendingCell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity), 3).Accepted,
            "queue edit failed");
        Require(session.AdvanceSimulation(1).Succeeded, "advance before save failed");
        scene.SetSaveName(OldName);
        var saved = scene.SaveWorld();
        Require(saved.Succeeded && saved.SaveId is not null, "ordinary save failed");
        var oldId = saved.SaveId.GetValueOrDefault();
        _oldId = oldId;
        var store = new FileWorldSaveStore(scene.GetNode<Main>("/root/Main").IsolatedUserDataDirectory);
        var oldPath = store.PathFor(oldId);
        var oldBytes = File.ReadAllBytes(oldPath);

        Add(CopiedCell);
        var world = session.World;
        scene.Press("workspace");
        Require(scene.PromptVisible, "unsaved return omitted prompt");
        scene.PressPrompt("cancel");
        Require(!scene.PromptVisible && ReferenceEquals(session.World, world) && session.HasUnsavedChanges,
            "cancel left the world");
        File.SetAttributes(oldPath, File.GetAttributes(oldPath) | System.IO.FileAttributes.ReadOnly);
        try
        {
            scene.Press("workspace");
            scene.PressPrompt("save_then_switch");
            Require(ReferenceEquals(session.World, world) && session.HasUnsavedChanges &&
                    scene.GetTree().CurrentScene == scene, "failed save left the current editor");
        }
        finally { File.SetAttributes(oldPath, File.GetAttributes(oldPath) & ~System.IO.FileAttributes.ReadOnly); }

        scene.Press("save_as");
        Require(scene.SaveAsPromptVisible, "save-as button omitted its confirmation");
        scene.SetSaveAsName(NewName);
        scene.PressSaveAsCancel();
        Require(!scene.SaveAsPromptVisible && session.SaveId == oldId && session.HasUnsavedChanges &&
                File.ReadAllBytes(oldPath).SequenceEqual(oldBytes),
            "save-as cancellation changed identity, dirty state or original file");

        scene.Press("save_as");
        Require(scene.SaveAsPromptVisible, "second save-as request omitted its confirmation");
        scene.SetSaveAsName(NewName);
        scene.PressSaveAsConfirm();
        Require(!scene.SaveAsPromptVisible && session.SaveId.HasValue && session.SaveId.Value != oldId &&
                !session.HasUnsavedChanges && File.Exists(store.PathFor(session.SaveId.Value)) &&
                File.ReadAllBytes(oldPath).SequenceEqual(oldBytes),
            "successful save-as did not switch identity while preserving the original");
        var newId = session.SaveId.GetValueOrDefault();
        _newId = newId;
        GD.Print($"WORKSPACE_SAVE_AS old={oldId:D} copy={newId:D} cancel=kept dirty=true original=kept");
        Add(DiscardedCell);
        scene.Press("workspace");
        Require(scene.PromptVisible, "post-save-as unsaved return omitted prompt");
        scene.PressPrompt("discard");
        GD.Print("WORKSPACE_RETURN cancel=kept save_failure=kept discard=workspace");
        _finished = true;
    }

    private void VerifyIsolatedSave(VoxelDevelopmentSession session)
    {
        if (_visit == 1)
        {
            Require(session.SaveId == RequiredId(OldIdEnvironmentVariable) && session.World.VoxelCount == 3 &&
                    session.Query(OldOnlyCell).Voxel is not null && session.Query(NewOnlyCell).Voxel is null &&
                    session.Query(CopiedCell).Voxel is null && session.QueryPendingEdits().Count == 0,
                "original save contains copy-only state or lost its continued state");
            scene.Press("workspace");
            _finished = true;
            return;
        }
        Require(session.SaveId == RequiredId(NewIdEnvironmentVariable) && session.World.VoxelCount == 5 &&
                session.Query(NewOnlyCell).Voxel is not null && session.Query(OldOnlyCell).Voxel is null &&
                session.Query(CopiedCell).Voxel is not null && session.Query(SaveContinueCell).Voxel is not null &&
                session.QueryPendingEdits().Count == 0,
            "save-as copy contains original-only state or lost its copied state");
        GD.Print("WORKSPACE_ISOLATED original_voxels=3 copy_voxels=5 cross_edits=false pending=0");
        _finished = true;
        Done(scene);
    }

    private void Add(WorldVoxelCoord cell) =>
        Require(scene.Session!.Add(cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity).Succeeded,
            "formal voxel edit failed");

    private void SaveThenReturn(VoxelDevelopmentSession session)
    {
        Require(session.SaveId == _newId && session.World.VoxelCount == 2 && !session.HasUnsavedChanges,
            "reopened save-as copy does not match its saved boundary");
        Add(SaveContinueCell);
        scene.Press("workspace");
        Require(scene.PromptVisible, "save-and-return edit omitted the unsaved prompt");
        scene.PressPrompt("save_then_switch");
        GD.Print($"WORKSPACE_SAVE_CONTINUE id={_newId:D} voxels=3 returned=true");
        _finished = true;
    }

    private static Guid RequiredId(string variable)
    {
        var value = System.Environment.GetEnvironmentVariable(variable);
        if (!Guid.TryParse(value, out var id))
            throw new InvalidOperationException($"{variable} is missing or invalid.");
        return id;
    }

    private static Guid ExpectedId(string variable, Guid inProcessId) =>
        inProcessId != Guid.Empty ? inProcessId : RequiredId(variable);

    private static void VerifyOpenFailure(WorldEditorWorkspaceScene workspace, Main main, Guid id)
    {
        var path = new FileWorldSaveStore(main.IsolatedUserDataDirectory).PathFor(id);
        var original = File.ReadAllText(path);
        try
        {
            var button = SaveOpenButton(workspace, id);
            Require(!button.Disabled, "valid save is unavailable before the file changes");
            var previousResult = workspace.Result;
            File.WriteAllText(path, "{");
            button.EmitSignal(BaseButton.SignalName.Pressed);
            Require(workspace.GetTree().CurrentScene == workspace && workspace.Result.Length > 0 && workspace.Result != previousResult,
                "failed open replaced the workspace or omitted the reason");
            var entry = workspace.Entries.Single(item => item.Id == id);
            Require(!entry.Recovery.Succeeded && entry.Recovery.Diagnostic is not null && SaveOpenButton(workspace, id).Disabled,
                "failed open left a usable stale row without a reason");
            GD.Print($"WORKSPACE_OPEN_FAILED fault=corrupt status={entry.Recovery.Status} kept=true row_disabled=true");
        }
        finally
        {
            File.WriteAllText(path, original);
            workspace.RefreshSaves();
        }
    }

    private static Button SaveOpenButton(WorldEditorWorkspaceScene workspace, Guid id)
    {
        var label = workspace.FindChildren("*", nameof(Label), owned: false).OfType<Label>()
            .Single(item => item.Text.Contains(id.ToString("D"), StringComparison.Ordinal));
        var row = label.GetParent();
        return row.GetChildren().OfType<Button>().Single();
    }

    private static void Done(Node node)
    {
        GD.Print($"WORKSPACE_DONE leg={Leg}");
        node.GetTree().Quit();
    }

    private static void Guard(Node node, Action action)
    {
        try { action(); }
        catch (Exception error)
        {
            GD.PrintErr($"WORKSPACE_FAILED leg={Leg} {error}");
            node.GetTree().Quit(1);
        }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
