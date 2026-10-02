using Godot;
using Train.Content;
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
    public const string ImportedIdEnvironmentVariable = "TRAIN_WORLD_EDITOR_WORKSPACE_IMPORTED_ID";
    public const string ExportPathEnvironmentVariable = "TRAIN_WORLD_EDITOR_WORKSPACE_EXPORT_PATH";
    public const string ExampleSaveIdEnvironmentVariable = "TRAIN_WORLD_EDITOR_WORKSPACE_EXAMPLE_SAVE_ID";
    public const string ExampleEvidencePathEnvironmentVariable = "TRAIN_WORLD_EDITOR_WORKSPACE_EXAMPLE_EVIDENCE_PATH";
    public const string CellToolSaveIdEnvironmentVariable = "TRAIN_WORLD_EDITOR_WORKSPACE_CELL_TOOL_SAVE_ID";
    public const string HistorySaveIdEnvironmentVariable = "TRAIN_WORLD_EDITOR_WORKSPACE_HISTORY_SAVE_ID";
    private const string OldName = "工作区验证原存档";
    private const string NewName = "工作区验证副本";
    private static readonly WorldVoxelCoord InitialCell = new(2, 2, 2);
    private static readonly WorldVoxelCoord PendingCell = new(3, 2, 2);
    private static readonly WorldVoxelCoord CopiedCell = new(4, 2, 2);
    private static readonly WorldVoxelCoord OldOnlyCell = new(5, 2, 2);
    private static readonly WorldVoxelCoord NewOnlyCell = new(6, 2, 2);
    private static readonly WorldVoxelCoord DiscardedCell = new(7, 2, 2);
    private static readonly WorldVoxelCoord SaveContinueCell = new(8, 2, 2);
    private static readonly WorldVoxelCoord ImportedOnlyCell = new(9, 2, 2);
    private static readonly WorldVoxelCoord ExampleQueuedCell = new(5, 1, 2);
    private static readonly WorldVoxelCoord ExampleCopyOnlyCell = new(6, 1, 2);
    private static readonly WorldVoxelCoord ExampleFutureCell = new(7, 1, 2);
    private static readonly WorldVoxelCoord ExampleDiscardedCell = new(8, 1, 2);
    private static readonly WorldVoxelCoord CellToolPendingCell = new(5, 3, 5);
    private static readonly WorldVoxelCoord CellToolCollisionCell = new(12, 2, 12);
    private static readonly WorldVoxelCoord CellToolMissingDefinitionCell = new(11, 3, 11);
    private static readonly WorldVoxelCoord HistoryCell = new(2, 2, 2);
    private static readonly WorldVoxelCoord HistorySavedCell = new(3, 2, 2);
    private static readonly VoxelDefinitionId LedgeDefinition = new("core", "ledge");
    private static string Leg => System.Environment.GetEnvironmentVariable(LegEnvironmentVariable) ?? "";
    private static int _workspaceVisits;
    private static int _menuVisits;
    private static int _editorVisits;
    private static Guid _oldId;
    private static Guid _newId;
    private static Guid _importedId;
    private static Guid _exampleDocumentId;
    private int _phase;
    private int _workPlanePhase;
    private bool _workPlanesChecked;
    private ulong _surfaceRemovedNextEdit;
    private static readonly WorldVoxelCoord VerticalXCell = new(18, 5, 8);
    private static readonly WorldVoxelCoord VerticalZCell = new(20, 5, 18);
    private static readonly WorldVoxelCoord SurfaceOutsideCell = new(20, 6, 18);
    private readonly int _visit = ++_editorVisits;
    private bool _finished;
    private WorldEditorLeaveSmoke? _leaveSmoke;
    private VoxelDefinitionId _cellToolDefinition;
    private VoxelOrientation _cellToolOrientation;
    private Vector3 _cellToolCamera;
    private int _cellToolDefinitionIndex;
    private WorldVoxelCoord _cellToolHoverBeforeSetting;
    private ulong _physicsFrameTarget;
    private VoxelCatalogSnapshot? _catalogBeforeMissingDefinition;
    private string? _historyLineEditText;

    public static bool Requested() => Leg is "save" or "resume-old" or "resume-new" or "import-new" or "verify" or
        "example-save" or "example-verify" or "cell-tool-save" or "cell-tool-resume" or
        "history-save" or "history-resume";

    public static void FromMenu(StartMenu menu) => Guard(menu, () =>
    {
        Require(menu.GetTree().CurrentScene == menu, "menu was replaced before choosing world editor");
        if (Leg == "save" && ++_menuVisits == 1)
        {
            menu.GetNode<Main>("/root/Main").EnterWorldEditor("missing-smoke-configuration");
            return;
        }
        if (Leg == "save" && _menuVisits == 2)
            GD.Print("WORKSPACE_NO_WORLD_LEAVE completed=true");
        menu.WorldEditorButton.EmitSignal(BaseButton.SignalName.Pressed);
        GD.Print("WORKSPACE_MENU ready=true");
    });

    public static void FromFailedEditor(WorldEditorScene editor, WorldEditorEstablishStatus status) => Guard(editor, () =>
    {
        Require(Leg == "save" && _menuVisits == 1 && status == WorldEditorEstablishStatus.UnknownConfiguration &&
                editor.Session is null,
            "editor failed to establish outside the deliberately unavailable configuration");
        Require(editor.RequestWorldChange(new WorldChangeRequest(WorldChangeKind.Reset, null)).Status ==
                WorldChangeStatus.CandidateFailed && editor.GetTree().CurrentScene == editor,
            "world replacement succeeded without an established world");
        editor.Press("menu");
    });

    public static void FromWorkspace(WorldEditorWorkspaceScene workspace) => Guard(workspace, () =>
    {
        _workspaceVisits++;
        var main = workspace.GetNode<Main>("/root/Main");
        Require(workspace.GetTree().CurrentScene == workspace, "workspace was not displayed first");
        if (Leg is "example-save" or "example-verify")
        {
            FromExampleWorkspace(workspace);
            return;
        }
        if (Leg == "cell-tool-save")
        {
            Require(workspace.Entries.Count == 0, "cell-tool process unexpectedly restored or created a save");
            workspace.SetBlankInputs(33, 17, 19, "181");
            workspace.CreateBlank();
            return;
        }
        if (Leg == "cell-tool-resume")
        {
            var id = RequiredId(CellToolSaveIdEnvironmentVariable);
            Require(workspace.Entries.Count == 1 && workspace.Entries.Single().Id == id && workspace.OpenSave(id).Succeeded,
                "cell-tool saved world was unavailable to the continuation process");
            GD.Print($"WORKSPACE_CELL_TOOL_OPEN id={id:D}");
            return;
        }
        if (Leg == "history-save")
        {
            Require(workspace.Entries.Count == 0, "history process unexpectedly restored or created a save");
            workspace.SetBlankInputs(8, 8, 8, "182");
            workspace.CreateBlank();
            return;
        }
        if (Leg == "history-resume")
        {
            var id = RequiredId(HistorySaveIdEnvironmentVariable);
            Require(workspace.Entries.Count == 1 && workspace.Entries.Single().Id == id && workspace.OpenSave(id).Succeeded,
                "history save was unavailable to the continuation process");
            GD.Print($"WORKSPACE_HISTORY_OPEN id={id:D}");
            return;
        }
        if (Leg == "save" && _workspaceVisits == 1)
        {
            Require(workspace.Entries.Count == 0, "new process unexpectedly restored or created a save");
            workspace.SetBlankInputs(33, 17, 19, "42");
            workspace.CreateBlank();
            return;
        }

        var entries = workspace.Entries;
        var oldEntry = entries.Single(item => item.Id == ExpectedId(OldIdEnvironmentVariable, _oldId));
        var newEntry = entries.Single(item => item.Id == ExpectedId(NewIdEnvironmentVariable, _newId));
        Require(entries.Count == (Leg == "verify" ? 3 : 2) && oldEntry.Name == OldName && newEntry.Name == NewName,
            "workspace did not retain the original and save-as copy with their identities");
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
                GD.Print($"WORKSPACE_LIST_OPEN id={newEntry.Id:D} leg={Leg}");
                return;
            }
            VerifyOpenFailure(workspace, main, oldEntry.Id);
            GD.Print($"WORKSPACE_SAVED old={oldEntry.Id:D} copy={newEntry.Id:D} cancel=true save_failure=true");
            Done(workspace);
            return;
        }

        if (Leg == "import-new")
        {
            var imported = workspace.ImportSave(RequiredPath(ExportPathEnvironmentVariable));
            Require(imported.Status == WorldSaveStatus.Saved && imported.SaveId.HasValue,
                "workspace import did not create a local save identity");
            var importedId = imported.SaveId.GetValueOrDefault();
            _importedId = importedId;
            Require(importedId != oldEntry.Id && importedId != newEntry.Id,
                "workspace import did not create a distinct local save identity");
            Require(workspace.Entries.Count == 3 &&
                    workspace.Entries.Single(item => item.Id == importedId).Recovery.Succeeded,
                "workspace import did not expose the imported save as usable");
            Require(workspace.OpenSave(importedId).Succeeded, "imported save could not be opened from the workspace");
            GD.Print($"WORKSPACE_IMPORTED id={importedId:D} listed=true opened=true");
            return;
        }

        var entry = Leg switch
        {
            "resume-old" => oldEntry,
            "resume-new" => newEntry,
            "verify" when _workspaceVisits == 1 => oldEntry,
            "verify" when _workspaceVisits == 2 => newEntry,
            "verify" => entries.Single(item => item.Id == RequiredId(ImportedIdEnvironmentVariable)),
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
            if (Leg is "example-save" or "example-verify")
            {
                AdvanceExample(session);
                return;
            }
            if (Leg is "cell-tool-save" or "cell-tool-resume")
            {
                AdvanceCellTool(session);
                return;
            }
            if (Leg is "history-save" or "history-resume")
            {
                AdvanceHistory(session);
                return;
            }
            if (_phase == 0)
            {
                if (Leg == "save" && _visit == 1)
                {
                    _leaveSmoke ??= new WorldEditorLeaveSmoke(scene);
                    if (!_leaveSmoke.Advance())
                        return;
                }
                Require(session.SimulationPaused && session.WorldSource?.Kind == WorldSource.BlankKind,
                    "workspace did not enter the same paused blank editor");
                Require(session.Identity.Bounds == new VoxelWorldBounds(33, 17, 19) && session.Identity.Seed == 42,
                    "blank inputs were not preserved");
                if (Leg == "save")
                {
                    if (_visit == 1)
                    {
                        CreateSaveAsPair(session);
                    }
                    else
                        SaveThenReturn(session);
                    return;
                }
                if (Leg == "verify")
                {
                    VerifyIsolatedSave(session);
                    return;
                }

                var expectedId = Leg == "import-new" ? _importedId :
                    RequiredId(Leg == "resume-old" ? OldIdEnvironmentVariable : NewIdEnvironmentVariable);
                var expectedVoxels = Leg switch { "resume-old" => 1, "resume-new" => 3, _ => 2 };
                Require(session.SaveId == expectedId && session.SimulationStep == 1 && session.QueryPendingEdits().Count == 1 &&
                        session.World.VoxelCount == expectedVoxels &&
                        (Leg != "import-new" ||
                         (session.Query(InitialCell).Voxel is not null && session.Query(CopiedCell).Voxel is not null &&
                          session.Query(OldOnlyCell).Voxel is null && session.Query(NewOnlyCell).Voxel is null)),
                    "ordinary open lost identity, simulation position, copied state or pending input");
                Add(Leg switch { "resume-old" => OldOnlyCell, "resume-new" => NewOnlyCell, _ => ImportedOnlyCell });
                scene.Press("run");
                _phase = 1;
                return;
            }
            if (session.SimulationStep < 5)
                return;
            scene.Press("pause");
            scene.Refresh();
            var expected = Leg switch { "resume-old" => 3, "resume-new" => 5, _ => 4 };
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

    private void AdvanceHistory(VoxelDevelopmentSession session)
    {
        if (Leg == "history-resume")
        {
            Require(session.SaveId == RequiredId(HistorySaveIdEnvironmentVariable) && session.SimulationPaused &&
                    session.World.VoxelCount == 2 && session.Query(HistoryCell).Voxel?.CurrentDurability == 75 &&
                    session.Query(HistorySavedCell).Voxel is not null && session.UndoCount == 0 && session.RedoCount == 0 &&
                    !session.CanUndo && !session.CanRedo && scene.ApplyHistory(redo: false) == nameof(WorldEditRefusal.NoHistory),
                "fresh process restored session history or lost the saved post-history world");
            GD.Print("WORKSPACE_HISTORY_RESUMED voxels=2 durability=75 undo=0 redo=0 no_history=true");
            _finished = true;
            Done(scene);
            return;
        }

        var view = scene.CellTool ?? throw new InvalidOperationException("cell tool view is unavailable");
        var tool = view.State;
        switch (_phase)
        {
            case 0:
                Require(session.SimulationPaused && session.World.VoxelCount == 0 && session.UndoCount == 0,
                    "history save did not start in an empty paused world");
                scene.SetEditInputs(HistoryCell, default, 1, 0);
                Click(scene.ActionButton("edit_add"));
                _phase++;
                return;
            case 1:
                Require(session.Query(HistoryCell).Voxel is not null && session.UndoCount == 1 && session.RedoCount == 0,
                    "panel add did not establish world history");
                FocusWorld();
                SendShortcut(Godot.Key.Z);
                _phase++;
                return;
            case 2:
                Require(session.Query(HistoryCell).Voxel is null && session.UndoCount == 0 && session.RedoCount == 1,
                    "world-focused Ctrl+Z did not undo the formal edit");
                SendShortcut(Godot.Key.Z, shift: true);
                _phase++;
                return;
            case 3:
                Require(session.Query(HistoryCell).Voxel is not null && session.UndoCount == 1 && session.RedoCount == 0,
                    "world-focused Ctrl+Shift+Z did not redo the formal edit");
                Click(scene.ActionButton("undo"));
                _phase++;
                return;
            case 4:
                Require(session.Query(HistoryCell).Voxel is null && session.RedoCount == 1,
                    "panel undo did not update the shared history");
                scene.GetNode<Main>("/root/Main").GetChildren().OfType<GameConsole>().Single().SubmitLine("voxel redo");
                _phase++;
                return;
            case 5:
                Require(session.Query(HistoryCell).Voxel is not null && session.UndoCount == 1 && session.RedoCount == 0,
                    "console redo did not consume the panel-created redo branch");
                var line = view.LayerInput.GetLineEdit();
                _historyLineEditText = line.Text;
                Click(line);
                SendKey(Godot.Key.A, pressed: true, ctrl: true);
                SendKey(Godot.Key.A, pressed: false, ctrl: true);
                TypeText("7");
                SendShortcut(Godot.Key.Z);
                _phase++;
                return;
            case 6:
                Require(view.LayerInput.GetLineEdit().Text == _historyLineEditText &&
                        session.Query(HistoryCell).Voxel is not null && session.UndoCount == 1 && session.RedoCount == 0,
                    "focused LineEdit Ctrl+Z changed world history or failed to undo its own text");
                scene.SetEditInputs(HistoryCell, default, 25, 0);
                Click(scene.ActionButton("edit_damage"));
                _phase++;
                return;
            case 7:
                Require(scene.HistoryPrompt.Visible && session.Query(HistoryCell).Voxel?.CurrentDurability == 100 &&
                        session.UndoCount == 1 && session.RedoCount == 0,
                    "non-reversible panel damage was not held behind confirmation");
                Click(scene.HistoryPrompt.GetCancelButton());
                _phase++;
                return;
            case 8:
                Require(!scene.HistoryPrompt.Visible && session.Query(HistoryCell).Voxel?.CurrentDurability == 100 &&
                        session.UndoCount == 1 && session.RedoCount == 0,
                    "cancelling history-ending damage changed the world or history");
                Click(scene.ActionButton("edit_damage"));
                _phase++;
                return;
            case 9:
                Require(scene.HistoryPrompt.Visible, "second history-ending damage omitted confirmation");
                Click(scene.HistoryPrompt.GetOkButton());
                _phase++;
                return;
            case 10:
                Require(!scene.HistoryPrompt.Visible && session.Query(HistoryCell).Voxel?.CurrentDurability == 75 &&
                        session.UndoCount == 0 && session.RedoCount == 0,
                    "confirmed damage did not apply and clear world history");
                tool.SetExact(new WorldVoxelCoord(4, 2, 2), WorldEditorSequences.EditVoxelId,
                    VoxelOrientation.Identity, VoxelCellOperation.Add);
                tool.SetExact(new WorldVoxelCoord(5, 2, 2), WorldEditorSequences.EditVoxelId,
                    VoxelOrientation.Yaw90, VoxelCellOperation.Replace);
                FocusWorld();
                SendShortcut(Godot.Key.Z);
                _phase++;
                return;
            case 11:
                Require(tool.HasDraft && tool.Position == new WorldVoxelCoord(4, 2, 2) &&
                        tool.Operation == VoxelCellOperation.Add && session.World.VoxelCount == 1 &&
                        session.UndoCount == 0 && session.RedoCount == 0,
                    "world-focused draft shortcut changed the world instead of only undoing precise draft input");
                tool.Discard();
                scene.SetEditInputs(HistorySavedCell, default, 1, 0);
                Click(scene.ActionButton("edit_add"));
                _phase++;
                return;
            case 12:
                Require(session.World.VoxelCount == 2 && session.UndoCount == 1 && session.RedoCount == 0 &&
                        session.HistoryEvictionCount == 0,
                    "post-confirmation edit did not establish the history saved only in this session");
                scene.SetSaveName("历史跨进程验证");
                var saved = scene.SaveWorld();
                Require(saved.Succeeded && saved.SaveId.HasValue && session.UndoCount == 1 &&
                        session.Query(HistoryCell).Voxel?.CurrentDurability == 75,
                    "saving changed session history or post-undo/redo world content");
                GD.Print($"WORKSPACE_HISTORY_SAVED id={saved.SaveId.GetValueOrDefault():D} voxels=2 durability=75 undo=1 redo=0 " +
                         "shortcuts=true line_edit=true shared=true prompt=true draft_only=true evictions=0");
                _finished = true;
                Done(scene);
                return;
        }
    }

    private static void FromExampleWorkspace(WorldEditorWorkspaceScene workspace)
    {
        var descriptor = WorldEditorExamples.All[0];
        var entry = workspace.ExampleEntries.Single(item => item.Example.Path == descriptor.Path);
        Require(entry.Document is not null && entry.Recovery.Succeeded,
            "the distributed example was not parsed as a usable document");
        var document = entry.Document!;
        _exampleDocumentId = document.Id;
        var source = Godot.FileAccess.GetFileAsString(descriptor.Path);
        Require(!string.IsNullOrEmpty(source), "Godot FileAccess could not read the distributed example");
        var evidencePath = RequiredPath(ExampleEvidencePathEnvironmentVariable);
        if (Leg == "example-save" && _workspaceVisits == 1)
            File.WriteAllText(evidencePath, source);
        else
            Require(File.Exists(evidencePath) && File.ReadAllText(evidencePath) == source,
                "the distributed example bytes changed after creating or saving a copy");

        if (Leg == "example-verify" && _workspaceVisits == 1)
        {
            var savedId = RequiredId(ExampleSaveIdEnvironmentVariable);
            Require(savedId != document.Id && workspace.OpenSave(savedId).Succeeded,
                "the saved example copy is missing or reused the example document identity");
            GD.Print($"WORKSPACE_EXAMPLE_COPY_OPEN id={savedId:D} example={document.Id:D}");
            return;
        }

        var created = workspace.CreateExampleCopy(descriptor);
        Require(created.Succeeded, "the workspace could not create an editable example copy");
        GD.Print($"WORKSPACE_EXAMPLE_CREATED path={descriptor.Path} original={document.Id:D} leg={Leg}");
    }

    private void AdvanceExample(VoxelDevelopmentSession session)
    {
        if (Leg == "example-save")
        {
            if (_phase == 0)
            {
                RequireExampleBoundary(session);
                Require(session.SaveId is null && session.HasUnsavedChanges,
                    "example copy did not enter as an unsaved editing session");
                Add(ExampleCopyOnlyCell);
                Require(session.SubmitEdit(new VoxelAddEdit(ExampleFutureCell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity),
                        100, confirmEndHistory: true).Accepted,
                    "example copy rejected a formal future edit");
                scene.Press("run");
                _phase = 1;
                return;
            }
            if (session.SimulationStep < 5)
                return;
            scene.Press("pause");
            scene.Refresh();
            Require(session.SimulationPaused && session.World.VoxelCount == 5 &&
                    session.Query(ExampleQueuedCell).Voxel is not null && session.Query(ExampleCopyOnlyCell).Voxel is not null &&
                    session.Query(ExampleFutureCell).Voxel is null && session.QueryPendingEdits().Count == 1,
                "example copy did not preserve editing, running and pending-command state");
            scene.SetSaveName("示例副本跨进程验证");
            var saved = scene.SaveWorld();
            Require(saved.Succeeded && saved.SaveId.HasValue && !session.HasUnsavedChanges,
                "example copy did not receive a save identity");
            var savedId = saved.SaveId.GetValueOrDefault();
            var originalId = _exampleDocumentId;
            Require(savedId != originalId, "the first save reused the distributed example document UUID");
            GD.Print($"WORKSPACE_EXAMPLE_SAVED id={savedId:D} original={originalId:D} step={session.SimulationStep} voxels=5 pending=1 paused=true");
            _finished = true;
            Done(scene);
            return;
        }

        if (_visit == 1)
        {
            Require(session.SaveId == RequiredId(ExampleSaveIdEnvironmentVariable) && session.SimulationPaused &&
                    session.SimulationStep >= 5 && session.World.VoxelCount == 5 && session.QueryPendingEdits().Count == 1 &&
                    session.Query(ExampleQueuedCell).Voxel is not null && session.Query(ExampleCopyOnlyCell).Voxel is not null &&
                    session.Query(ExampleFutureCell).Voxel is null,
                "fresh process did not restore the saved example copy independently");
            Add(ExampleDiscardedCell);
            scene.Press("workspace");
            Require(scene.PromptVisible, "edited example copy omitted workspace leave protection");
            scene.PressPrompt("cancel");
            Require(!scene.PromptVisible && scene.GetTree().CurrentScene == scene && session.HasUnsavedChanges &&
                    session.Query(ExampleDiscardedCell).Voxel is not null,
                "cancelling workspace leave did not preserve the edited example copy");
            scene.Press("workspace");
            Require(scene.PromptVisible, "second example-copy leave omitted workspace protection");
            scene.PressPrompt("discard");
            WorldChangeSmoke.Finish(session);
            _finished = true;
            return;
        }

        RequireExampleBoundary(session);
        Require(session.SaveId is null && session.Query(ExampleCopyOnlyCell).Voxel is null &&
                session.Query(ExampleFutureCell).Voxel is null && session.Query(ExampleDiscardedCell).Voxel is null &&
                session.HasUnsavedChanges,
            "creating the example again inherited edits or identity from the saved copy");
        GD.Print("WORKSPACE_EXAMPLE_ISOLATED saved_copy=true leave_cancel=true leave_discard=true recreated_original=true step=1 voxels=3 pending=1 paused=true");
        _finished = true;
        Done(scene);
    }

    private void AdvanceCellTool(VoxelDevelopmentSession session)
    {
        var view = scene.CellTool ?? throw new InvalidOperationException("cell tool view is unavailable");
        var state = view.State;
        if (Leg == "cell-tool-resume")
        {
            AdvanceCellToolResume(session, view, state);
            return;
        }

        switch (_phase)
        {
            case 0:
                Require(session.SaveId is null && session.SimulationPaused && session.World.VoxelCount == 0 &&
                        session.QueryPendingEdits().Count == 0 && !state.HasDraft,
                    "cell-tool save did not start in a blank paused world");
                Click(view.VoxelToolButton);
                _cellToolCamera = scene.Camera.GlobalPosition;
                _phase++;
                return;
            case 1:
                Require(state.Active,
                    $"real tool click did not activate the cell tool: active={state.Active} Layer={state.Layer} definition={state.DefinitionId} draft={state.HasDraft}");
                ReplaceNumber(view.LayerInput, 3);
                _phase++;
                return;
            case 2:
                Require(state.Active && state.Layer == 3 && !state.HasDraft,
                    $"real Layer input did not establish the horizontal plane: active={state.Active} Layer={state.Layer} definition={state.DefinitionId} draft={state.HasDraft}");
                _cellToolDefinition = WorldEditorSequences.EditVoxelId;
                _cellToolDefinitionIndex = state.Definitions.ToList().IndexOf(_cellToolDefinition);
                Require(_cellToolDefinitionIndex >= 0, "cell-tool content omitted the editable voxel definition");
                Click(view.DefinitionInput);
                _phase++;
                return;
            case 3:
                Require(view.DefinitionInput.GetPopup().Visible,
                    $"definition popup did not open from real click: visible={view.DefinitionInput.Visible} rect={view.DefinitionInput.GetGlobalRect()}");
                ChooseOpenOption(view.DefinitionInput, _cellToolDefinitionIndex);
                _phase++;
                return;
            case 4:
                Require(state.Active && state.Layer == 3 && state.DefinitionId == _cellToolDefinition && !state.HasDraft,
                    $"definition setting unexpectedly created a draft: active={state.Active} Layer={state.Layer} definition={state.DefinitionId} expected={_cellToolDefinition} draft={state.HasDraft}");
                Require(state.Orientations.Count > 1, "selected voxel definition does not exercise allowed orientation choice");
                _cellToolOrientation = state.Orientations[1];
                Click(view.OrientationInput);
                _phase++;
                return;
            case 5:
                Require(view.OrientationInput.GetPopup().Visible,
                    $"orientation popup did not open from real click: visible={view.OrientationInput.Visible} rect={view.OrientationInput.GetGlobalRect()}");
                ChooseOpenOption(view.OrientationInput, 1);
                _phase++;
                return;
            case 6:
                Require(state.Orientation == _cellToolOrientation && !state.HasDraft,
                    $"orientation setting unexpectedly created a draft: actual={state.Orientation} expected={_cellToolOrientation} draft={state.HasDraft}");
                _cellToolHoverBeforeSetting = state.Position;
                Motion(PlaneScreen(new WorldVoxelCoord(10, 3, 10)));
                _phase++;
                return;
            case 7:
                Require(state.Position == new WorldVoxelCoord(10, 3, 10) && state.Position != _cellToolHoverBeforeSetting &&
                        state.Orientation == _cellToolOrientation &&
                        !state.HasDraft && scene.Camera.GlobalPosition == _cellToolCamera,
                    "definition/orientation settings prevented hover following or moved the camera");
                Click(view.OperationInput);
                _phase++;
                return;
            case 8:
                Require(view.OperationInput.GetPopup().Visible, "operation popup did not open for remove selection");
                ChooseOpenOption(view.OperationInput, (int)VoxelCellOperation.Remove);
                _phase++;
                return;
            case 9:
                Require(state.Operation == VoxelCellOperation.Remove && !state.HasDraft,
                    "remove setting unexpectedly created a draft");
                _cellToolHoverBeforeSetting = state.Position;
                Motion(PlaneScreen(new WorldVoxelCoord(11, 3, 10)));
                _phase++;
                return;
            case 10:
                Require(state.Position == new WorldVoxelCoord(11, 3, 10) &&
                        state.Position != _cellToolHoverBeforeSetting && !state.HasDraft,
                    "remove setting prevented hover following");
                Click(view.OperationInput);
                _phase++;
                return;
            case 11:
                Require(view.OperationInput.GetPopup().Visible, "operation popup did not open for replace selection");
                ChooseOpenOption(view.OperationInput, (int)VoxelCellOperation.Replace);
                _phase++;
                return;
            case 12:
                Require(state.Operation == VoxelCellOperation.Replace && !state.HasDraft,
                    "replace setting unexpectedly created a draft");
                _cellToolHoverBeforeSetting = state.Position;
                Motion(PlaneScreen(new WorldVoxelCoord(12, 3, 10)));
                _phase++;
                return;
            case 13:
                Require(state.Position == new WorldVoxelCoord(12, 3, 10) &&
                        state.Position != _cellToolHoverBeforeSetting && !state.HasDraft,
                    "replace setting prevented hover following");
                Click(view.OperationInput);
                _phase++;
                return;
            case 14:
                Require(view.OperationInput.GetPopup().Visible, "operation popup did not open for add selection");
                ChooseOpenOption(view.OperationInput, (int)VoxelCellOperation.Add);
                Motion(PlaneScreen(new WorldVoxelCoord(10, 3, 10)));
                _phase++;
                return;
            case 15:
                Require(state.Position.Y == 3 && !state.HasDraft && state.Preview.Succeeded &&
                        state.Operation == VoxelCellOperation.Add && state.Definitions.Contains(state.DefinitionId) &&
                        state.Orientations.Contains(state.Orientation),
                    "blank-world mouse did not retain valid settings after operation changes");
                var mouseCell = state.Position;
                ClickAt(PlaneScreen(mouseCell));
                CellToolCommittedCellForRun = mouseCell;
                _phase++;
                return;
            case 16:
                var committed = CellToolCommittedCellForRun;
                Require(session.Query(committed).Voxel is { } voxel && voxel.DefinitionId == state.DefinitionId &&
                        voxel.Orientation == state.Orientation && !state.HasDraft,
                    "real mouse click did not commit the shared preview");
                ReplaceNumber(view.CoordinateInputs[0], committed.X + 1);
                ReplaceNumber(view.CoordinateInputs[0], committed.X);
                _phase++;
                return;
            case 17:
                Require(state.HasDraft && state.Position == CellToolCommittedCellForRun &&
                        state.Preview.Status == VoxelCellSetStatus.Occupied,
                    "precise parameters did not share the rejected occupied-cell preview");
                Click(view.CommitButton);
                Click(view.SelectToolButton);
                _phase++;
                return;
            case 18:
                Require(session.QueryEditLog().Last().State == WorldEditState.Rejected &&
                        state.Preview.Status == VoxelCellSetStatus.Occupied && session.World.VoxelCount == 1 &&
                        session.Query(CellToolCommittedCellForRun).Voxel is { } kept &&
                        kept.DefinitionId == _cellToolDefinition && kept.Orientation == _cellToolOrientation,
                    "rejected cell commit changed world content or lost its reason");
                Require(view.DraftPrompt.Visible && state.Active && state.HasDraft,
                    "tool switch did not require an explicit draft decision");
                Click(view.DraftDiscardButton);
                _phase++;
                return;
            case 19:
                Require(!view.DraftPrompt.Visible && !state.Active && !state.HasDraft &&
                        session.Query(CellToolCommittedCellForRun).Voxel is not null,
                    $"discard did not complete the guarded tool switch while preserving committed state: prompt={view.DraftPrompt.Visible} active={state.Active} draft={state.HasDraft} committed={session.Query(CellToolCommittedCellForRun).Voxel is not null}");
                Click(view.VoxelToolButton);
                _phase++;
                return;
            case 20:
                Require(state.Active && !state.HasDraft, "plane-switch fixture did not activate its tool");
                state.SetExact(new WorldVoxelCoord(7, 3, 5), state.DefinitionId, state.Orientation, VoxelCellOperation.Add);
                ReplaceNumber(view.LayerInput, 4);
                _phase++;
                return;
            case 21:
                Require(view.DraftPrompt.Visible && state.HasDraft && state.Layer == 3,
                    "changing plane Layer did not keep the original plane behind a draft decision");
                Click(view.DraftDiscardButton);
                _phase++;
                return;
            case 22:
                Require(!state.HasDraft && state.Layer == 4,
                    $"discarding the plane-switch draft did not apply the requested Layer: Layer={state.Layer} input={view.LayerInput.Value} draft={state.HasDraft} prompt={view.DraftPrompt.Visible}");
                ReplaceNumber(view.LayerInput, 3);
                _phase++;
                return;
            case 23:
                Require(state.Layer == 3 && !state.HasDraft, "plane-switch continuation did not return to its original Layer");
                Click(view.SelectToolButton);
                _phase++;
                return;
            case 24:
                Require(!state.Active && !state.HasDraft && session.Query(CellToolCollisionCell).Voxel is null,
                    "collision replacement fixture did not start from an isolated empty cell");
                Require(session.SubmitEdit(new VoxelCellSetEdit(new[]
                {
                    new VoxelCellTarget(CellToolCollisionCell, VoxelCellOperation.Add,
                        new VoxelCellContent(WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity))
                })).Result is VoxelCellSetResult { Status: VoxelCellSetStatus.Applied },
                    "formal cell-set could not add the full-box collision fixture");
                _physicsFrameTarget = Engine.GetPhysicsFrames() + 2;
                _phase++;
                return;
            case 25:
                if (Engine.GetPhysicsFrames() < _physicsFrameTarget)
                    return;
                RequirePublishedCollision(
                    CellToolCollisionCell,
                    new VoxelCollisionBox(12.78, 2.38, 12.48, 12.82, 2.42, 12.52),
                    new Vector3(11.5f, 2.5f, 12.5f),
                    new Vector3(13.5f, 2.5f, 12.5f),
                    12f);
                Require(session.SubmitEdit(new VoxelCellSetEdit(new[]
                {
                    new VoxelCellTarget(CellToolCollisionCell, VoxelCellOperation.Replace,
                        new VoxelCellContent(LedgeDefinition, VoxelOrientation.Identity))
                })).Result is VoxelCellSetResult { Status: VoxelCellSetStatus.Applied },
                    "formal cell-set could not replace full-box with identity local-box");
                _physicsFrameTarget = Engine.GetPhysicsFrames() + 2;
                _phase++;
                return;
            case 26:
                if (Engine.GetPhysicsFrames() < _physicsFrameTarget)
                    return;
                RequirePublishedCollision(
                    CellToolCollisionCell,
                    new VoxelCollisionBox(12.18, 2.08, 12.48, 12.22, 2.12, 12.52),
                    new Vector3(11.5f, 2.5f, 12.5f),
                    new Vector3(13.5f, 2.5f, 12.5f),
                    12.125f);
                Require(!scene.Colliders.QueryPublishedHits(scene.GetWorld3D(),
                        new VoxelCollisionBox(12.68, 2.38, 12.48, 12.72, 2.42, 12.52))
                        .Contains(CellToolCollisionCell),
                    "identity local-box replacement retained the old full-box collider");
                RequireCellFaces(CellToolCollisionCell, new Vector3(12.25f, 2.4375f, 12.5f));
                Require(session.SubmitEdit(new VoxelCellSetEdit(new[]
                {
                    new VoxelCellTarget(CellToolCollisionCell, VoxelCellOperation.Replace,
                        new VoxelCellContent(LedgeDefinition, VoxelOrientation.Yaw90))
                })).Result is VoxelCellSetResult { Status: VoxelCellSetStatus.Applied },
                    "formal cell-set could not replace the local-box orientation");
                _physicsFrameTarget = Engine.GetPhysicsFrames() + 2;
                _phase++;
                return;
            case 27:
                if (Engine.GetPhysicsFrames() < _physicsFrameTarget)
                    return;
                Require(!scene.Colliders.QueryPublishedHits(scene.GetWorld3D(),
                        new VoxelCollisionBox(12.18, 2.08, 12.48, 12.22, 2.12, 12.52))
                        .Contains(CellToolCollisionCell),
                    "orientation replacement retained the identity local-box collider");
                RequirePublishedCollision(
                    CellToolCollisionCell,
                    new VoxelCollisionBox(12.48, 2.38, 12.18, 12.52, 2.42, 12.22),
                    new Vector3(12.5f, 2.5f, 11.5f),
                    new Vector3(12.5f, 2.5f, 13.5f),
                    12.125f);
                Require(session.SubmitEdit(new VoxelCellSetEdit(new[]
                {
                    new VoxelCellTarget(CellToolCollisionCell, VoxelCellOperation.Replace,
                        new VoxelCellContent(VoxelDefinitionId.Parse("core:hollow"), VoxelOrientation.Identity))
                })).Result is VoxelCellSetResult { Status: VoxelCellSetStatus.Applied },
                    "could not establish a non-colliding cell");
                _physicsFrameTarget = Engine.GetPhysicsFrames() + 2;
                _phase++;
                return;
            case 28:
                if (Engine.GetPhysicsFrames() < _physicsFrameTarget)
                    return;
                Require(session.Query(CellToolCollisionCell).IsOccupied &&
                        scene.Colliders.QueryPublishedRay(scene.GetWorld3D(), new(11.5f, 2.5f, 12.5f), new(13.5f, 2.5f, 12.5f)) is null,
                    "non-colliding voxel became a pickable surface");
                state.SetLayer(2);
                Require(state.TryPlaneTarget(new(12.5, 6, 12.5), new(0, -1, 0), out var planeCell) &&
                        planeCell == CellToolCollisionCell,
                    "an absent collider prevented targeting a plane layer");
                state.SetLayer(3);
                Require(session.SubmitEdit(new VoxelCellSetEdit(new[]
                {
                    new VoxelCellTarget(CellToolCollisionCell, VoxelCellOperation.Remove, null)
                })).Result is VoxelCellSetResult { Status: VoxelCellSetStatus.Applied },
                    "formal cell-set could not clean up the collision fixture");
                Require(session.Query(CellToolCollisionCell).Voxel is null,
                    "collision fixture cleanup did not restore the blank world cell");
                Click(view.VoxelToolButton);
                _phase = 29;
                return;
            case 29:
                Require(state.Active && !state.HasDraft && state.Definitions.Contains(LedgeDefinition),
                    "missing-definition fixture could not activate with the unused ledge definition available");
                state.SetSettings(LedgeDefinition, VoxelOrientation.Identity, VoxelCellOperation.Add);
                view.Present();
                ReplaceNumber(view.CoordinateInputs[0], CellToolMissingDefinitionCell.X);
                ReplaceNumber(view.CoordinateInputs[1], CellToolMissingDefinitionCell.Y);
                ReplaceNumber(view.CoordinateInputs[2], CellToolMissingDefinitionCell.Z);
                _phase++;
                return;
            case 30:
                Require(state.HasDraft && state.Position == CellToolMissingDefinitionCell &&
                        state.DefinitionId == LedgeDefinition && state.Preview.Succeeded,
                    "exact keyboard input did not create the isolated missing-definition draft");
                var catalog = scene.GetNode<Main>("/root/Main").Reloader?.Catalog
                    ?? throw new InvalidOperationException("content catalog is unavailable");
                _catalogBeforeMissingDefinition = catalog.Current
                    ?? throw new InvalidOperationException("published voxel catalog is unavailable");
                Require(!session.World.OccupiedVoxels().Any(item => item.Value.DefinitionId == LedgeDefinition),
                    "missing-definition fixture selected a definition already used by the world");
                catalog.Publish(new VoxelCatalogSnapshot(
                    _catalogBeforeMissingDefinition.Generation + 1,
                    _catalogBeforeMissingDefinition.Entries
                        .Where(item => item.Key != LedgeDefinition)
                        .ToDictionary(item => item.Key, item => item.Value)));
                view.Present();
                ReplaceNumber(view.CoordinateInputs[0], CellToolMissingDefinitionCell.X + 1);
                _phase++;
                return;
            case 31:
                Require(state.HasDraft && state.Position == CellToolMissingDefinitionCell &&
                        state.DefinitionId == LedgeDefinition && state.CurrentEdit.Targets.Single().Content?.DefinitionId == LedgeDefinition &&
                        state.Preview.Status == VoxelCellSetStatus.UnknownDefinition,
                    "keyboard input after content removal crashed, rewrote the draft, or lost UnknownDefinition");
                var restoreCatalog = scene.GetNode<Main>("/root/Main").Reloader?.Catalog
                    ?? throw new InvalidOperationException("content catalog is unavailable during restore");
                restoreCatalog.Publish(new VoxelCatalogSnapshot(
                    restoreCatalog.Current!.Generation + 1,
                    _catalogBeforeMissingDefinition!.Entries));
                view.Present();
                _cellToolDefinitionIndex = state.Definitions.ToList().IndexOf(_cellToolDefinition);
                Require(_cellToolDefinitionIndex >= 0, "restored catalog omitted the valid cell-tool definition");
                Click(view.DefinitionInput);
                _phase++;
                return;
            case 32:
                Require(view.DefinitionInput.GetPopup().Visible,
                    "definition popup did not reopen after restoring content");
                ChooseOpenOption(view.DefinitionInput, _cellToolDefinitionIndex);
                _phase++;
                return;
            case 33:
                Require(state.HasDraft && state.DefinitionId == _cellToolDefinition && state.Preview.Succeeded,
                    "reselecting a valid definition did not allow the preserved draft to continue editing");
                Click(view.DiscardButton);
                Click(view.SelectToolButton);
                _phase = 40;
                return;
            case 40:
                if (!_workPlanesChecked)
                {
                    AdvanceWorkPlanes(session, view);
                    return;
                }
                Require(!state.Active && !state.HasDraft, "target-switch fixture did not activate the selection tool");
                var pending = new VoxelCellSetEdit(new[]
                {
                    new VoxelCellTarget(CellToolPendingCell, VoxelCellOperation.Add,
                        new VoxelCellContent(state.DefinitionId, state.Orientation))
                });
                Require(session.SubmitEdit(pending, 3, confirmEndHistory: true).Accepted && session.QueryPendingEdits().Count == 1,
                    "cell-tool continuation fixture was not accepted as pending formal input");
                scene.SetSaveName("单格工具跨进程验证");
                var saved = scene.SaveWorld();
                Require(saved.Succeeded && saved.SaveId.HasValue && !state.HasDraft &&
                        session.Query(CellToolCommittedCellForRun).Voxel is not null &&
                        session.Query(CellToolPendingCell).Voxel is null,
                    "cell-tool save did not capture committed and pending state without its draft");
                state.SetExact(new WorldVoxelCoord(7, 3, 5), state.DefinitionId, state.Orientation, VoxelCellOperation.Add);
                Require(state.HasDraft && scene.SaveWorld().Succeeded && state.HasDraft,
                    "ordinary save discarded the cell draft");
                ClickAt(WorldScreen(CellToolCommittedCellForRun));
                _phase++;
                return;
            case 41:
                Require(view.DraftPrompt.Visible && state.HasDraft,
                    "real target click did not require an explicit draft decision");
                Click(view.DraftDiscardButton);
                _phase++;
                return;
            case 42:
                Require(!state.HasDraft && scene.Observation!.Observer.Selection is { Kind: ObservedObjectKind.WorldCell } selected &&
                        selected.Cell == CellToolCommittedCellForRun,
                    "discarding the target-switch draft did not select the clicked committed cell");
                scene.SetEditInputs(new WorldVoxelCoord(15, 4, 10), default, 1, 0);
                scene.ApplyOperation(scene.EditOperation("edit_manufacture"));
                Require(session.Vehicles.Count == 1, "level-switch fixture vehicle could not be manufactured");
                var vehicle = session.Vehicles.Single().Id;
                var observation = scene.Observation ?? throw new InvalidOperationException("observation view is unavailable");
                Require(observation.Observer.Select(ObservedObject.Vehicle(vehicle)) == WorldEditorObservationStatus.Selected &&
                        observation.Observer.EnterVehicleParts() == WorldEditorObservationStatus.Selected,
                    "level-switch fixture could not enter vehicle parts");
                state.SetExact(new WorldVoxelCoord(8, 3, 5), state.DefinitionId, state.Orientation, VoxelCellOperation.Add);
                var frame = WorldEditorObserver.VehicleFrame(session, session.QueryDisplayPoses(), vehicle)!;
                Motion(scene.GetViewport().GetScreenTransform() * scene.Camera.UnprojectPosition(WorldEditorObservationView.To(frame.Center)));
                _phase++;
                return;
            case 43:
                var hover = scene.Observation!.HoverVehicle;
                Require(hover is not null && hover.VehicleId == session.Vehicles.Single().Id && scene.Observation.PointerCoordinatesVisible,
                    "real vehicle hover did not display its world/local coordinates");
                var worldPoint = session.Vehicles.Single().Transform.TransformPoint(hover!.LocalPosition.X, hover.LocalPosition.Y, hover.LocalPosition.Z);
                Require((hover.WorldPosition - new ObservationVector(worldPoint.X, worldPoint.Y, worldPoint.Z)).Length < 1e-6,
                    "vehicle cursor coordinates used a different transform from occupancy");
                GD.Print($"WORKSPACE_VEHICLE_COORDINATES world={hover.WorldPosition} local={hover.LocalPosition} visible=true");
                Click(scene.Observation!.ActionButton("whole"));
                _phase++;
                return;
            case 44:
                Require(view.DraftPrompt.Visible && state.HasDraft && scene.Observation!.Observer.PartVehicleId is not null,
                    "level switch did not require an explicit draft decision");
                Click(view.DraftCommitButton);
                _phase++;
                return;
            case 45:
                Require(!state.HasDraft && scene.Observation!.Observer.PartVehicleId is null &&
                        session.Query(new WorldVoxelCoord(8, 3, 5)).Voxel is not null,
                    "committing the level-switch draft did not apply it and return to the whole vehicle");
                state.SetExact(new WorldVoxelCoord(9, 3, 5), state.DefinitionId, state.Orientation, VoxelCellOperation.Add);
                Click(scene.ActionButton("workspace"));
                _phase++;
                return;
            case 46:
                Require(scene.PromptVisible && state.HasDraft && scene.WorldChangePrompt?.Visible == true,
                    "leaving the world did not route its draft through the unified protection");
                Click(scene.DraftLeaveDiscardButton);
                _phase++;
                return;
            case 47:
                Require(scene.PromptVisible && !state.HasDraft,
                    "leaving draft discard did not return to the remaining unsaved-world decision");
                Click(scene.WorldChangePrompt?.CancelButton ?? throw new InvalidOperationException("leave cancel is unavailable"));
                _phase++;
                return;
            case 48:
                Require(!scene.PromptVisible && scene.GetTree().CurrentScene == scene,
                    "cancelling after the draft decision left the editor");
                GD.Print($"WORKSPACE_CELL_TOOL_INPUT Layer={state.Layer} mouse_cell={CellToolCommittedCellForRun} settings_follow=true replace_collision=true missing_definition_kept=true exact_shared=true rejected_kept=true tool_guard=discard target_guard=discard level_guard=commit leave_guard=discard save_kept_draft=true");
                GD.Print($"WORKSPACE_CELL_TOOL_SAVED id={session.SaveId.GetValueOrDefault():D} committed=true pending=1 draft=false");
                _finished = true;
                Done(scene);
                return;
        }
    }

    private WorldVoxelCoord CellToolCommittedCellForRun { get; set; }

    private void AdvanceWorkPlanes(VoxelDevelopmentSession session, WorldEditorVoxelToolView view)
    {
        var state = view.State;
        switch (_workPlanePhase)
        {
            case 0:
                Click(view.VoxelToolButton);
                Click(view.PlaneInput);
                break;
            case 1:
                ChooseOpenOption(view.PlaneInput, (int)VoxelWorkPlane.VerticalX);
                break;
            case 2:
                ReplaceNumber(view.LayerInput, VerticalXCell.X);
                break;
            case 3:
                Require(state.Plane == VoxelWorkPlane.VerticalX && state.Layer == VerticalXCell.X && !state.HasDraft,
                    "real plane selection did not establish the X layer");
                Motion(VerticalScreen(VerticalXCell, VoxelWorkPlane.VerticalX));
                break;
            case 4:
                Require(state.Position == VerticalXCell && state.Preview.Succeeded && !state.HasDraft,
                    $"X plane mouse preview targeted {state.Position}, expected {VerticalXCell}");
                ClickAt(VerticalScreen(VerticalXCell, VoxelWorkPlane.VerticalX));
                break;
            case 5:
                Require(session.Query(VerticalXCell).Voxel is not null && !state.HasDraft,
                    "real X plane click did not commit its preview");
                Click(view.PlaneInput);
                break;
            case 6:
                ChooseOpenOption(view.PlaneInput, (int)VoxelWorkPlane.VerticalZ);
                break;
            case 7:
                Require(state.Plane == VoxelWorkPlane.VerticalZ && state.Layer == VerticalZCell.Z,
                    "real plane selection did not establish the Z layer");
                Motion(VerticalScreen(VerticalZCell, VoxelWorkPlane.VerticalZ));
                break;
            case 8:
                Require(state.Position == VerticalZCell && state.Preview.Succeeded,
                    $"Z plane mouse preview targeted {state.Position}, expected {VerticalZCell}");
                ClickAt(VerticalScreen(VerticalZCell, VoxelWorkPlane.VerticalZ));
                break;
            case 9:
                Require(session.Query(VerticalZCell).Voxel is not null && !state.HasDraft,
                    "real Z plane click did not commit its preview");
                _physicsFrameTarget = Engine.GetPhysicsFrames() + 2;
                Click(view.PlaneInput);
                break;
            case 10:
                ChooseOpenOption(view.PlaneInput, (int)VoxelWorkPlane.Surface);
                break;
            case 11:
                if (Engine.GetPhysicsFrames() < _physicsFrameTarget)
                    return;
                Motion(SurfaceScreen(VerticalZCell));
                break;
            case 12:
                var pick = scene.Observation!.QueryPick(scene.GetViewport().GetScreenTransform().AffineInverse() * SurfaceScreen(VerticalZCell));
                Require(state.Plane == VoxelWorkPlane.Surface && pick.WorldCell == VerticalZCell && pick.WorldNormal == Vector3.Up &&
                        pick.AdjacentCell == SurfaceOutsideCell && state.Position == SurfaceOutsideCell && state.Preview.Succeeded,
                    $"surface preview did not target the outside cell: hit={pick.WorldCell} normal={pick.WorldNormal} target={state.Position}");
                ClickAt(SurfaceScreen(VerticalZCell));
                break;
            case 13:
                Require(session.Query(SurfaceOutsideCell).Voxel is not null && !state.HasDraft,
                    "real picked surface click did not place outside the hit cell");
                Click(view.OperationInput);
                break;
            case 14:
                ChooseOpenOption(view.OperationInput, (int)VoxelCellOperation.Remove);
                break;
            case 15:
                Require(state.Position == VerticalZCell && state.Operation == VoxelCellOperation.Remove && state.Preview.Succeeded && !state.HasDraft,
                    "changing the surface operation did not retarget the shared preview to the hit cell");
                Click(view.CommitButton);
                break;
            case 16:
                Require(!session.Query(VerticalZCell).IsOccupied && session.Query(SurfaceOutsideCell).IsOccupied && !state.HasDraft,
                    "surface operation button changed the neighbor instead of removing the hit cell");
                Click(view.OperationInput);
                break;
            case 17:
                ChooseOpenOption(view.OperationInput, (int)VoxelCellOperation.Add);
                break;
            case 18:
                Require(view.CommitButton.Disabled && !state.HasDraft, "removed surface still allowed a cached target to be committed");
                _surfaceRemovedNextEdit = session.NextEditId;
                Click(view.CommitButton);
                break;
            case 19:
                Require(session.NextEditId == _surfaceRemovedNextEdit && !state.HasDraft &&
                        !session.Query(VerticalZCell).IsOccupied && session.Query(SurfaceOutsideCell).IsOccupied,
                    "a commit without a live surface changed the world");
                Require(session.SubmitEdit(new VoxelCellSetEdit(new[]
                {
                    new VoxelCellTarget(VerticalZCell, VoxelCellOperation.Add, new VoxelCellContent(state.DefinitionId, state.Orientation))
                })).Result is VoxelCellSetResult { Status: VoxelCellSetStatus.Applied }, "could not restore the surface fixture");
                _physicsFrameTarget = Engine.GetPhysicsFrames() + 2;
                break;
            case 20:
                if (Engine.GetPhysicsFrames() < _physicsFrameTarget)
                    return;
                Motion(SurfaceScreen(SurfaceOutsideCell));
                break;
            case 21:
                Require(state.Position == SurfaceOutsideCell with { Y = 7 } && state.Operation == VoxelCellOperation.Add && !state.HasDraft,
                    $"a fresh surface pick did not restore addition at the new outside neighbor: {state.Position}");
                ReplaceNumber(view.CoordinateInputs[1], 9);
                break;
            case 22:
                Require(state.HasDraft && state.Position == SurfaceOutsideCell with { Y = 9 } &&
                        state.CurrentEdit.Targets.Single().Position == state.Position,
                    "surface precise input did not share the preview across heights");
                Motion(SurfaceScreen(VerticalZCell));
                Click(view.PlaneInput);
                break;
            case 23:
                ChooseOpenOption(view.PlaneInput, (int)VoxelWorkPlane.Horizontal);
                break;
            case 24:
                Require(state.Position == SurfaceOutsideCell with { Y = 9 } && state.Plane == VoxelWorkPlane.Surface && view.DraftPrompt.Visible,
                    "plane switch redirected an existing surface draft");
                Click(view.DraftDiscardButton);
                break;
            case 25:
                Require(!state.HasDraft && state.Plane == VoxelWorkPlane.Horizontal,
                    "explicit draft discard did not apply the requested plane");
                ReplaceNumber(view.LayerInput, 3);
                Click(view.SelectToolButton);
                break;
            case 26:
                Require(!state.Active && !state.HasDraft && state.Layer == 3 && scene.Camera.GlobalPosition == _cellToolCamera,
                    "work-plane editing changed camera state or left a draft");
                GD.Print("WORKSPACE_WORK_PLANES vertical_x=true vertical_z=true surface_outside=true operation_retarget=true removed_surface_invalid=true exact_shared=true draft_guard=true local_box_faces=true non_pickable=true");
                _workPlanesChecked = true;
                return;
        }
        _workPlanePhase++;
    }

    private Vector2 VerticalScreen(WorldVoxelCoord cell, VoxelWorkPlane plane) => scene.GetViewport().GetScreenTransform() *
        scene.Camera.UnprojectPosition(plane == VoxelWorkPlane.VerticalX
            ? new Vector3(cell.X, cell.Y + 0.5f, cell.Z + 0.5f)
            : new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z));

    private Vector2 SurfaceScreen(WorldVoxelCoord cell) => scene.GetViewport().GetScreenTransform() *
        scene.Camera.UnprojectPosition(new Vector3(cell.X + 0.5f, cell.Y + 1, cell.Z + 0.5f));

    private void RequireCellFaces(WorldVoxelCoord cell, Vector3 center)
    {
        foreach (var normal in new[] { Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Back, Vector3.Forward })
        {
            var hit = scene.Colliders.QueryPublishedRay(scene.GetWorld3D(), center + normal * 2, center);
            var adjacent = new WorldVoxelCoord(cell.X + (int)normal.X, cell.Y + (int)normal.Y, cell.Z + (int)normal.Z);
            Require(hit?.Cell == cell && hit?.Normal == normal && hit?.AdjacentCell == adjacent,
                $"LocalBox logical face failed: expected normal={normal} adjacent={adjacent}, got {hit}");
        }
    }

    private void RequirePublishedCollision(
        WorldVoxelCoord cell,
        VoxelCollisionBox probe,
        Vector3 rayFrom,
        Vector3 rayTo,
        float expectedRayCoordinate)
    {
        Require(scene.Session!.QueryCollision(probe).Hits.Any(hit => hit.Cell == cell),
            $"domain collision omitted {cell} for the expected shape pocket");
        Require(scene.Colliders.QueryPublishedHits(scene.GetWorld3D(), probe).Contains(cell),
            $"published collision omitted {cell} for the expected shape pocket");
        var ray = scene.Colliders.QueryPublishedRay(scene.GetWorld3D(), rayFrom, rayTo);
        var actualCoordinate = rayFrom.X != rayTo.X ? ray?.Position.X : ray?.Position.Z;
        Require(ray?.Cell == cell && actualCoordinate.HasValue && Math.Abs(actualCoordinate.Value - expectedRayCoordinate) < 0.02f,
            $"published ray hit {ray?.Cell} at {actualCoordinate}, expected {cell} near {expectedRayCoordinate}");
    }

    private void AdvanceCellToolResume(VoxelDevelopmentSession session, WorldEditorVoxelToolView view, WorldEditorVoxelTool state)
    {
        switch (_phase)
        {
            case 0:
                Require(session.SaveId == RequiredId(CellToolSaveIdEnvironmentVariable) && session.SimulationPaused &&
                        session.World.VoxelCount == 4 && session.QueryPendingEdits().Count == 1 && !state.HasDraft &&
                        session.Query(VerticalXCell).IsOccupied && session.Query(VerticalZCell).IsOccupied && session.Query(SurfaceOutsideCell).IsOccupied &&
                        session.Query(CellToolPendingCell).Voxel is null,
                    "fresh process did not restore committed and pending cell edits without a UI draft");
                Click(view.VoxelToolButton);
                ReplaceNumber(view.LayerInput, 4);
                _phase++;
                return;
            case 1:
                Require(state.Active && state.Layer == 4 && !state.HasDraft,
                    "continued cell tool did not accept the horizontal plane input");
                var world = ScreenCenter(scene.Observation!.WorldRect);
                Motion(world);
                _phase++;
                return;
            case 2:
                CellToolCommittedCellForRun = state.Position;
                Require(state.Position.Y == 4 && state.Preview.Succeeded && !state.HasDraft,
                    "continued blank plane did not expose an empty mouse target");
                ClickAt(ScreenCenter(scene.Observation!.WorldRect));
                _phase++;
                return;
            case 3:
                Require(session.Query(CellToolCommittedCellForRun).Voxel is not null && !state.HasDraft,
                    "fresh process could not continue editing through real mouse input");
                scene.Press("run");
                _phase++;
                return;
            case 4 when session.SimulationStep < 3:
                return;
            case 4:
                scene.Press("pause");
                scene.Refresh();
                Require(session.Query(CellToolPendingCell).Voxel is not null && session.QueryPendingEdits().Count == 0 &&
                        session.Query(CellToolCommittedCellForRun).Voxel is not null && !state.HasDraft,
                    "continued process did not execute the restored pending edit and keep its mouse edit");
                var saved = scene.SaveWorld();
                Require(saved.Succeeded && saved.SaveId == session.SaveId && !session.HasUnsavedChanges,
                    "continued cell-tool world was not saved back to its own identity");
                GD.Print("WORKSPACE_CELL_TOOL_CONTINUED committed=true pending_executed=true mouse_edit=true draft=false");
                _finished = true;
                Done(scene);
                return;
        }
    }

    private static Vector2 ScreenCenter(Rect2 globalRect)
    {
        var viewport = Engine.GetMainLoop() as SceneTree ?? throw new InvalidOperationException("scene tree is unavailable");
        return viewport.Root.GetScreenTransform() * globalRect.GetCenter();
    }

    private Vector2 WorldScreen(WorldVoxelCoord cell)
    {
        var viewport = scene.GetViewport();
        var point = scene.Camera.UnprojectPosition(new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f));
        return viewport.GetScreenTransform() * point;
    }

    private Vector2 PlaneScreen(WorldVoxelCoord cell) => scene.GetViewport().GetScreenTransform() *
        scene.Camera.UnprojectPosition(new Vector3(cell.X + 0.5f, cell.Y, cell.Z + 0.5f));

    private static void Click(Control control) =>
        ClickAt(control.GetViewport().GetScreenTransform() * control.GetGlobalRect().GetCenter());

    private static void ClickAt(Vector2 position)
    {
        Input.ParseInputEvent(new InputEventMouseButton
            { ButtonIndex = MouseButton.Left, Pressed = true, Position = position, GlobalPosition = position });
        Input.ParseInputEvent(new InputEventMouseButton
            { ButtonIndex = MouseButton.Left, Pressed = false, Position = position, GlobalPosition = position });
    }

    private void FocusWorld()
    {
        var position = ScreenCenter(scene.Observation!.WorldRect);
        Input.ParseInputEvent(new InputEventMouseButton
            { ButtonIndex = MouseButton.Right, Pressed = true, Position = position, GlobalPosition = position });
        Input.ParseInputEvent(new InputEventMouseButton
            { ButtonIndex = MouseButton.Right, Pressed = false, Position = position, GlobalPosition = position });
        Input.FlushBufferedEvents();
        Require(scene.Observation.WorldHasInput, "world input did not take focus for a keyboard shortcut");
    }

    private static void Motion(Vector2 position) => Input.ParseInputEvent(new InputEventMouseMotion
        { Position = position, GlobalPosition = position });

    private static void ReplaceNumber(SpinBox input, int value)
    {
        Click(input.GetLineEdit());
        SendKey(Godot.Key.A, pressed: true, ctrl: true);
        SendKey(Godot.Key.A, pressed: false, ctrl: true);
        foreach (var character in value.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            var key = character == '-' ? Godot.Key.Minus : (Godot.Key)((long)Godot.Key.Key0 + character - '0');
            Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = character, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = character, Pressed = false });
        }
        SendKey(Godot.Key.Enter, pressed: true);
        SendKey(Godot.Key.Enter, pressed: false);
    }

    private static void TypeText(string text)
    {
        foreach (var character in text)
        {
            var key = (Godot.Key)((long)Godot.Key.Key0 + character - '0');
            Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = character, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = key, Unicode = character, Pressed = false });
        }
    }

    private static void SendShortcut(Godot.Key key, bool shift = false)
    {
        Input.ParseInputEvent(new InputEventKey
            { Keycode = key, PhysicalKeycode = key, Pressed = true, CtrlPressed = true, ShiftPressed = shift });
        Input.ParseInputEvent(new InputEventKey
            { Keycode = key, PhysicalKeycode = key, Pressed = false, CtrlPressed = true, ShiftPressed = shift });
    }

    private static void ChooseOpenOption(OptionButton input, int index)
    {
        var focused = input.GetPopup().GetFocusedItem();
        var steps = focused < 0 ? index + 1 : (index - focused + input.ItemCount) % input.ItemCount;
        for (var item = 0; item < steps; item++)
        {
            SendKey(Godot.Key.Down, pressed: true);
            SendKey(Godot.Key.Down, pressed: false);
        }
        SendKey(Godot.Key.Enter, pressed: true);
        SendKey(Godot.Key.Enter, pressed: false);
    }

    private static void SendKey(Godot.Key key, bool pressed, bool ctrl = false) => Input.ParseInputEvent(new InputEventKey
        { Keycode = key, PhysicalKeycode = key, Pressed = pressed, CtrlPressed = ctrl });

    private void RequireExampleBoundary(VoxelDevelopmentSession session)
    {
        Require(session.SimulationPaused && session.Identity.Bounds == new VoxelWorldBounds(16, 8, 16) &&
                session.Identity.Seed == 42 && session.SimulationStep == 1 && session.World.VoxelCount == 3 &&
                session.QueryPendingEdits().Count == 1 && session.Query(ExampleQueuedCell).Voxel is null &&
                session.Query(new WorldVoxelCoord(2, 1, 2)).Voxel?.DefinitionId == WorldEditorSequences.EditVoxelId &&
                session.Query(new WorldVoxelCoord(3, 1, 2)).Voxel?.DefinitionId == WorldEditorSequences.EditVoxelId &&
                session.Query(new WorldVoxelCoord(4, 1, 2)).Voxel?.DefinitionId == WorldEditorSequences.EditVoxelId,
            "distributed example did not restore its documented paused boundary");
        foreach (var (cell, _) in session.World.OccupiedVoxels())
        for (var x = 0; x <= 1; x++)
        for (var y = 0; y <= 1; y++)
        for (var z = 0; z <= 1; z++)
        {
            var point = new Vector3(cell.X + x, cell.Y + y, cell.Z + z);
            Require(scene.Camera.IsPositionInFrustum(point),
                $"initial view excludes example voxel at {point}; camera={scene.Camera.GlobalPosition}");
        }
    }

    private void CreateSaveAsPair(VoxelDevelopmentSession session)
    {
        Require(session.SaveId is null && session.HasUnsavedChanges && session.World.VoxelCount == 0 &&
                session.Vehicles.Count == 0 && session.Rails.Segments.Count == 0,
            "new world is not truly empty and unsaved");
        Add(InitialCell);
        Require(session.SubmitEdit(new VoxelAddEdit(PendingCell, WorldEditorSequences.EditVoxelId, VoxelOrientation.Identity),
                3, confirmEndHistory: true).Accepted,
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
            WorldChangeSmoke.Finish(session);
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
        var exportPath = RequiredPath(ExportPathEnvironmentVariable);
        var exported = scene.ExportWorld(exportPath, overwrite: false);
        Require(exported.Status == WorldSaveStatus.Saved && File.Exists(exportPath),
            "current save-as world was not exported to the requested path");
        GD.Print($"WORKSPACE_EXPORTED path={exportPath} status=Saved");
        Add(DiscardedCell);
        scene.Press("workspace");
        Require(scene.PromptVisible, "post-save-as unsaved return omitted prompt");
        scene.PressPrompt("discard");
        WorldChangeSmoke.Finish(session);
        GD.Print("WORKSPACE_RETURN cancel=kept save_failure=kept discard=workspace");
        _finished = true;
    }

    private void VerifyIsolatedSave(VoxelDevelopmentSession session)
    {
        if (_visit == 1)
        {
            Require(session.SaveId == RequiredId(OldIdEnvironmentVariable) && session.World.VoxelCount == 3 &&
                    session.Query(OldOnlyCell).Voxel is not null && session.Query(NewOnlyCell).Voxel is null &&
                    session.Query(ImportedOnlyCell).Voxel is null &&
                    session.Query(CopiedCell).Voxel is null && session.Query(SaveContinueCell).Voxel is null &&
                    session.Query(PendingCell).Voxel is not null && session.QueryPendingEdits().Count == 0,
                "original save contains copy-only state or lost its continued state");
            scene.Press("workspace");
            WorldChangeSmoke.Finish(session);
            _finished = true;
            return;
        }
        if (_visit == 2)
        {
            Require(session.SaveId == RequiredId(NewIdEnvironmentVariable) && session.World.VoxelCount == 5 &&
                    session.Query(NewOnlyCell).Voxel is not null && session.Query(ImportedOnlyCell).Voxel is null &&
                    session.Query(OldOnlyCell).Voxel is null &&
                    session.Query(CopiedCell).Voxel is not null && session.Query(SaveContinueCell).Voxel is not null &&
                    session.Query(PendingCell).Voxel is not null && session.QueryPendingEdits().Count == 0,
                "save-as copy contains another branch's edit or lost its saved state");
            scene.Press("workspace");
            WorldChangeSmoke.Finish(session);
            _finished = true;
            return;
        }
        Require(session.SaveId == RequiredId(ImportedIdEnvironmentVariable) && session.World.VoxelCount == 4 &&
                session.Query(ImportedOnlyCell).Voxel is not null && session.Query(NewOnlyCell).Voxel is null &&
                session.Query(OldOnlyCell).Voxel is null &&
                session.Query(CopiedCell).Voxel is not null && session.Query(SaveContinueCell).Voxel is null &&
                session.Query(PendingCell).Voxel is not null && session.QueryPendingEdits().Count == 0,
            "imported save contains another branch's edit or lost its continued state");
        GD.Print("WORKSPACE_ISOLATED original_voxels=3 copy_voxels=5 imported_voxels=4 cross_edits=false pending=0");
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
        WorldChangeSmoke.Finish(session);
        Require(session.SaveId == _newId && !session.HasUnsavedChanges && session.World.VoxelCount == 3,
            "save-and-return did not save the edited copy to its current identity");
        GD.Print($"WORKSPACE_SAVE_CONTINUE id={_newId:D} voxels=3");
        _finished = true;
    }

    private static Guid RequiredId(string variable)
    {
        var value = System.Environment.GetEnvironmentVariable(variable);
        if (!Guid.TryParse(value, out var id))
            throw new InvalidOperationException($"{variable} is missing or invalid.");
        return id;
    }

    private static string RequiredPath(string variable)
    {
        var value = System.Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"{variable} is missing or is not an absolute path.");
        return value;
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
