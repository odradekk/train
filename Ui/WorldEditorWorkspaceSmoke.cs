using Godot;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

/// <summary>Assertions through the menu, workspace, shared editor and ordinary save in separate process legs.</summary>
internal sealed class WorldEditorWorkspaceSmoke(WorldEditorScene scene)
{
    public const string LegEnvironmentVariable = "TRAIN_WORLD_EDITOR_WORKSPACE_LEG";
    private static string Leg => System.Environment.GetEnvironmentVariable(LegEnvironmentVariable) ?? "";
    private static int _workspaceVisits;
    private static int _editorVisits;
    private static Guid _savedId;
    private int _phase;
    private readonly int _visit = ++_editorVisits;
    private bool _finished;
    public static bool Requested() => Leg is "save" or "resume";

    public static void FromMenu(StartMenu menu) => Guard(menu, () =>
    {
        Require(menu.GetTree().CurrentScene == menu, "menu was replaced before choosing world editor");
        menu.WorldEditorButton.EmitSignal(BaseButton.SignalName.Pressed);
        GD.Print("WORKSPACE_MENU no_world=true");
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
        var entry = workspace.Entries.Single(item => item.Name == "工作区验证");
        Require(entry.Recovery.Succeeded && entry.LastSavedUtc is not null && entry.FormatVersion is not null,
            "ordinary save missing from the usable list");
        Require(entry.ExternalDefinitions.Count > 0, "list omitted dependency hints");
        if (Leg == "save" && _workspaceVisits == 3)
        {
            Require(entry.Id == _savedId && workspace.Entries.Count == 1, "return save changed identity or duplicated save");
            GD.Print($"WORKSPACE_SAVED id={entry.Id:D} save_continue=true discard=true cancel=true save_failure=true");
            Done(workspace);
            return;
        }
        if (Leg == "save")
            VerifyOpenFailure(workspace, main, entry.Id);
        Require(workspace.OpenSave(entry.Id).Succeeded, "list selection failed to open ordinary save");
        _savedId = entry.Id;
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
                if (Leg == "save" && _visit == 1)
                {
                    Require(session.SaveId is null && session.HasUnsavedChanges && session.World.VoxelCount == 0 &&
                            session.Vehicles.Count == 0 && session.Rails.Segments.Count == 0, "new world is not truly empty and unsaved");
                    Add(new WorldVoxelCoord(2, 2, 2));
                    Require(session.SubmitEdit(new VoxelAddEdit(new WorldVoxelCoord(3, 2, 2), WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity), 3).Accepted,
                        "queue edit failed");
                    Require(session.AdvanceSimulation(1).Succeeded, "advance before save failed");
                    scene.SetSaveName("工作区验证");
                    var saved = scene.SaveWorld();
                    Require(saved.Succeeded && saved.SaveId is not null, "ordinary save failed");
                    _savedId = saved.SaveId!.Value;
                    Add(new WorldVoxelCoord(4, 2, 2));
                    var world = session.World;
                    scene.Press("workspace");
                    Require(scene.PromptVisible, "unsaved return omitted prompt");
                    scene.PressPrompt("cancel");
                    Require(!scene.PromptVisible && ReferenceEquals(session.World, world) && session.HasUnsavedChanges,
                        "cancel left the world");
                    var path = new FileWorldSaveStore(scene.GetNode<Main>("/root/Main").IsolatedUserDataDirectory).PathFor(_savedId);
                    File.SetAttributes(path, File.GetAttributes(path) | System.IO.FileAttributes.ReadOnly);
                    try
                    {
                        scene.Press("workspace");
                        scene.PressPrompt("save_then_switch");
                        Require(ReferenceEquals(session.World, world) && session.HasUnsavedChanges &&
                                scene.GetTree().CurrentScene == scene, "failed save left the current editor");
                    }
                    finally { File.SetAttributes(path, File.GetAttributes(path) & ~System.IO.FileAttributes.ReadOnly); }
                    scene.Press("workspace");
                    scene.PressPrompt("discard");
                    GD.Print("WORKSPACE_RETURN cancel=kept save_failure=kept discard=workspace");
                    _finished = true;
                    return;
                }
                Require(session.SaveId == _savedId && session.SimulationStep == 1 && session.QueryPendingEdits().Count == 1,
                    "ordinary open lost identity, simulation position or pending input");
                if (Leg == "save")
                {
                    Require(session.World.VoxelCount == 1, "discarded changes leaked into the save");
                    Add(new WorldVoxelCoord(4, 2, 2));
                    scene.Press("workspace");
                    scene.PressPrompt("save_then_switch");
                    _finished = true;
                    return;
                }
                Require(session.World.VoxelCount == 2, "save-and-return did not persist edit");
                Add(new WorldVoxelCoord(5, 2, 2));
                scene.Press("run");
                _phase = 1;
                return;
            }
            if (session.SimulationStep < 5)
                return;
            scene.Press("pause");
            scene.Refresh();
            Require(session.SimulationPaused, "pause control did not pause the continued world");
            Require(session.QueryPendingEdits().Count == 0 && session.World.VoxelCount == 4 &&
                    session.Query(new WorldVoxelCoord(3, 2, 2)).Voxel is not null && session.HasUnsavedChanges,
                "new process did not continue editing and run queued commands");
            Require(scene.Meshes.LastWorldVoxelCount == 4 && scene.Colliders.LastWorldVoxelCount == 4,
                "Godot presentation does not match continued world");
            GD.Print($"WORKSPACE_CONTINUED id={session.SaveId:D} step={session.SimulationStep} voxels=4 pending=0 paused=true");
            _finished = true;
            Done(scene);
        });
    }

    private void Add(WorldVoxelCoord cell) => Require(scene.Session!.Add(cell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity).Succeeded, "formal voxel edit failed");

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
        var index = workspace.Entries.ToList().FindIndex(item => item.Id == id);
        var rows = workspace.FindChildren("*", nameof(ScrollContainer), owned: false).Single().GetChild(0);
        return rows.GetChild(index).GetChildren().OfType<Button>().Single();
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
