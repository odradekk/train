using Train.Core.Content.Voxels;
using Godot;
using Train.Core.Content;
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
    private ulong _surfaceRemovedNextEdit;
    private static readonly WorldVoxelCoord VerticalXCell = new(18, 5, 8);
    private static readonly WorldVoxelCoord VerticalZCell = new(20, 5, 18);
    private static readonly WorldVoxelCoord SurfaceOutsideCell = new(20, 6, 18);
    private static readonly WorldVoxelCoord CellToolMotionAfterClickCell = new(13, 3, 10);
    private readonly int _visit = ++_editorVisits;
    private bool _finished;
    private WorldEditorLeaveSmoke? _leaveSmoke;
    private WorldEditorMultiSelectionSmoke? _multiSelectionSmoke;
    private VoxelDefinitionId _cellToolDefinition;
    private VoxelOrientation _cellToolOrientation;
    private Vector3 _cellToolCamera;
    private ObservationVector _cellToolLookBeforeDrag;
    private ObservedObject? _cellToolSelectionBeforeDrag;
    private Vector2 _vehicleHoverScreen;
    private Vector2 _noTargetScreen;
    private double _cellToolPitchTurn;
    private int _cellToolDefinitionIndex;
    private WorldVoxelCoord _cellToolHoverBeforeSetting;
    private ulong _physicsFrameTarget;
    private VoxelCatalogSnapshot? _catalogBeforeMissingDefinition;
    private string? _historyLineEditText;
    private ulong _regionEditCursor;
    private int _regionUndoCount;
    private IReadOnlyList<WorldVoxelCoord> _committedBrushCells = Array.Empty<WorldVoxelCoord>();
    private IReadOnlyList<WorldVoxelCoord> _outsideReleaseBrushCells = Array.Empty<WorldVoxelCoord>();
    private IReadOnlyList<WorldVoxelCoord> _brushBeforeMove = Array.Empty<WorldVoxelCoord>();
    private IReadOnlyList<WorldVoxelCoord> _redrawnBrushCells = Array.Empty<WorldVoxelCoord>();
    private IReadOnlyList<WorldVoxelCoord> _committedBoxCells = Array.Empty<WorldVoxelCoord>();
    private static readonly WorldVoxelCoord RegionPendingFirst = new(28, 3, 14);
    private static readonly WorldVoxelCoord RegionPendingSecond = new(29, 3, 14);

    public static bool Requested() => Leg is "save" or "resume-old" or "resume-new" or "import-new" or "verify" or
        "example-save" or "example-verify" or "cell-tool-save" or "cell-tool-resume" or
        "history-save" or "history-resume" or "multi-select";

    public static void FromMenu(StartMenu menu) => Guard(menu, () =>
    {
        Require(menu.GetTree().CurrentScene == menu, "menu was replaced before choosing world editor");
        if (Leg == "multi-select")
        {
            menu.GetNode<Main>("/root/Main").EnterWorldEditor(WorldEditorConfigurations.StraightRailTrainId);
            GD.Print("WORKSPACE_MENU ready=true");
            return;
        }
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
            if (Leg == "multi-select")
            {
                _multiSelectionSmoke ??= new WorldEditorMultiSelectionSmoke(scene);
                _finished = _multiSelectionSmoke.Advance();
                return;
            }
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
                    $"real tool click did not activate the cell tool: active={state.Active} layer={state.Layer} definition={state.DefinitionId} draft={state.HasDraft}");
                Motion(PlaneScreen(new WorldVoxelCoord(10, 0, 10)));
                _phase = 2;
                return;
            case 2:
                Require(!view.CommitButton.Disabled && state.Position == new WorldVoxelCoord(10, 0, 10),
                    "horizontal-plane replacement fixture did not establish a real pointer target");
                var replacement = scene.RequestWorldChange(new WorldChangeRequest(
                    WorldChangeKind.NewBlank, null, BlankIdentity: session.Identity));
                Require(replacement.Status == WorldChangeStatus.ChoiceRequired && scene.PromptVisible,
                    "blank-world replacement did not request disposal of the unsaved empty world");
                scene.PressPrompt("discard");
                Require(session.IsChangingWorld, "confirmed blank-world replacement did not start");
                _phase = 3;
                return;
            case 3:
                if (session.IsChangingWorld)
                    return;
                Require(state.Active && state.Plane == VoxelWorkPlane.Horizontal && state.Layer == 0 &&
                        state.Position == default && !state.HasDraft,
                    "world replacement did not reset the horizontal-plane target state");
                RequireTargetGuard(session, view, "world replacement retained a stale horizontal-plane target");
                var look = scene.Observation!.Observer.Pose.Look - scene.Observation.Observer.Pose.Position;
                _cellToolPitchTurn = Math.PI / 4 - Math.Asin(look.Y / look.Length);
                scene.Observation.Observer.Look(0, _cellToolPitchTurn);
                _phase = 4;
                return;
            case 4:
                var horizontalBehind = BehindPlaneTarget(session.Identity.Bounds, VoxelWorkPlane.Horizontal);
                ReplaceNumber(view.LayerInput, horizontalBehind.Layer);
                _noTargetScreen = horizontalBehind.Screen;
                Motion(_noTargetScreen);
                _phase = 5;
                return;
            case 5:
                RequirePointerAt(_noTargetScreen);
                RequireTargetGuard(session, view, "horizontal plane accepted a target behind the camera");
                scene.Observation!.Observer.Look(0, -_cellToolPitchTurn);
                ReplaceNumber(view.LayerInput, 3);
                _phase++;
                return;
            case 6:
                Require(state.Active && state.Layer == 3 && !state.HasDraft,
                    $"real layer input did not establish the horizontal plane: active={state.Active} layer={state.Layer} definition={state.DefinitionId} draft={state.HasDraft}");
                _cellToolDefinition = WorldEditorSequences.EditVoxelId;
                _cellToolDefinitionIndex = state.Definitions.ToList().IndexOf(_cellToolDefinition);
                Require(_cellToolDefinitionIndex >= 0, "cell-tool content omitted the editable voxel definition");
                Click(view.DefinitionInput);
                _phase++;
                return;
            case 7:
                Require(view.DefinitionInput.GetPopup().Visible,
                    $"definition popup did not open from real click: visible={view.DefinitionInput.Visible} rect={view.DefinitionInput.GetGlobalRect()}");
                ChooseOpenOption(view.DefinitionInput, _cellToolDefinitionIndex);
                _phase++;
                return;
            case 8:
                Require(state.Active && state.Layer == 3 && state.DefinitionId == _cellToolDefinition && !state.HasDraft,
                    $"definition setting unexpectedly created a draft: active={state.Active} layer={state.Layer} definition={state.DefinitionId} expected={_cellToolDefinition} draft={state.HasDraft}");
                Require(state.Orientations.Count > 1, "selected voxel definition does not exercise allowed orientation choice");
                _cellToolOrientation = state.Orientations[1];
                Click(view.OrientationInput);
                _phase++;
                return;
            case 9:
                Require(view.OrientationInput.GetPopup().Visible,
                    $"orientation popup did not open from real click: visible={view.OrientationInput.Visible} rect={view.OrientationInput.GetGlobalRect()}");
                ChooseOpenOption(view.OrientationInput, 1);
                _phase++;
                return;
            case 10:
                Require(state.Orientation == _cellToolOrientation && !state.HasDraft,
                    $"orientation setting unexpectedly created a draft: actual={state.Orientation} expected={_cellToolOrientation} draft={state.HasDraft}");
                _cellToolHoverBeforeSetting = state.Position;
                Motion(PlaneScreen(new WorldVoxelCoord(10, 3, 10)));
                _phase++;
                return;
            case 11:
                Require(state.Position == new WorldVoxelCoord(10, 3, 10) && state.Position != _cellToolHoverBeforeSetting &&
                        state.Orientation == _cellToolOrientation &&
                        !state.HasDraft && scene.Camera.GlobalPosition == _cellToolCamera,
                    "definition/orientation settings prevented hover following or moved the camera");
                RequirePlanePointer(state, scene.Observation!.PointerWorldPosition,
                    "horizontal-plane pointer coordinates did not match its preview");
                Click(view.OperationInput);
                _phase++;
                return;
            case 12:
                Require(view.OperationInput.GetPopup().Visible, "operation popup did not open for remove selection");
                ChooseOpenOption(view.OperationInput, (int)VoxelCellOperation.Remove);
                _phase++;
                return;
            case 13:
                Require(state.Operation == VoxelCellOperation.Remove && !state.HasDraft,
                    "remove setting unexpectedly created a draft");
                _cellToolHoverBeforeSetting = state.Position;
                Motion(PlaneScreen(new WorldVoxelCoord(11, 3, 10)));
                _phase++;
                return;
            case 14:
                Require(state.Position == new WorldVoxelCoord(11, 3, 10) &&
                        state.Position != _cellToolHoverBeforeSetting && !state.HasDraft,
                    "remove setting prevented hover following");
                Click(view.OperationInput);
                _phase++;
                return;
            case 15:
                Require(view.OperationInput.GetPopup().Visible, "operation popup did not open for replace selection");
                ChooseOpenOption(view.OperationInput, (int)VoxelCellOperation.Replace);
                _phase++;
                return;
            case 16:
                Require(state.Operation == VoxelCellOperation.Replace && !state.HasDraft,
                    "replace setting unexpectedly created a draft");
                _cellToolHoverBeforeSetting = state.Position;
                Motion(PlaneScreen(new WorldVoxelCoord(12, 3, 10)));
                _phase++;
                return;
            case 17:
                Require(state.Position == new WorldVoxelCoord(12, 3, 10) &&
                        state.Position != _cellToolHoverBeforeSetting && !state.HasDraft,
                    "replace setting prevented hover following");
                Click(view.OperationInput);
                _phase++;
                return;
            case 18:
                Require(view.OperationInput.GetPopup().Visible, "operation popup did not open for add selection");
                ChooseOpenOption(view.OperationInput, (int)VoxelCellOperation.Add);
                Motion(PlaneScreen(new WorldVoxelCoord(10, 3, 10)));
                _phase++;
                return;
            case 19:
                Require(state.Position.Y == 3 && !state.HasDraft && state.Preview.Succeeded &&
                        state.Operation == VoxelCellOperation.Add && state.Definitions.Contains(state.DefinitionId) &&
                        state.Orientations.Contains(state.Orientation),
                    "blank-world mouse did not retain valid settings after operation changes");
                var mouseCell = state.Position;
                ClickAt(PlaneScreen(mouseCell));
                Motion(PlaneScreen(CellToolMotionAfterClickCell));
                CellToolCommittedCellForRun = mouseCell;
                _phase++;
                return;
            case 20:
                var committed = CellToolCommittedCellForRun;
                Require(session.Query(committed).Voxel is { } voxel && voxel.DefinitionId == state.DefinitionId &&
                        voxel.Orientation == state.Orientation && !state.HasDraft,
                    "real mouse click did not commit the shared preview");
                Require(session.Query(CellToolMotionAfterClickCell).Voxel is null,
                    "motion after the click replaced the pending click target");
                ReplaceNumber(view.CoordinateInputs[0], committed.X + 1);
                ReplaceNumber(view.CoordinateInputs[0], committed.X);
                _phase++;
                return;
            case 21:
                Require(state.HasDraft && state.Position == CellToolCommittedCellForRun &&
                        state.Preview.Status == VoxelCellSetStatus.Occupied,
                    "precise parameters did not share the rejected occupied-cell preview");
                ClickAt(PlaneScreen(CellToolCommittedCellForRun));
                Click(view.SelectToolButton);
                _phase++;
                return;
            case 22:
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
            case 23:
                Require(!view.DraftPrompt.Visible && !state.Active && !state.HasDraft &&
                        session.Query(CellToolCommittedCellForRun).Voxel is not null,
                    $"discard did not complete the guarded tool switch while preserving committed state: prompt={view.DraftPrompt.Visible} active={state.Active} draft={state.HasDraft} committed={session.Query(CellToolCommittedCellForRun).Voxel is not null}");
                Click(view.VoxelToolButton);
                _phase++;
                return;
            case 24:
                Require(state.Active && !state.HasDraft, "plane-switch fixture did not activate its tool");
                state.SetExact(new WorldVoxelCoord(7, 3, 5), state.DefinitionId, state.Orientation, VoxelCellOperation.Add);
                ReplaceNumber(view.LayerInput, 4);
                _phase++;
                return;
            case 25:
                Require(view.DraftPrompt.Visible && state.HasDraft && state.Layer == 3,
                    "changing plane layer did not keep the original plane behind a draft decision");
                Click(view.DraftDiscardButton);
                _phase++;
                return;
            case 26:
                Require(!state.HasDraft && state.Layer == 4,
                    $"discarding the plane-switch draft did not apply the requested layer: layer={state.Layer} input={view.LayerInput.Value} draft={state.HasDraft} prompt={view.DraftPrompt.Visible}");
                ReplaceNumber(view.LayerInput, 3);
                _phase++;
                return;
            case 27:
                Require(state.Layer == 3 && !state.HasDraft, "plane-switch continuation did not return to its original Layer");
                Click(view.SelectToolButton);
                _phase++;
                return;
            case 28:
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
            case 29:
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
            case 30:
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
            case 31:
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
            case 32:
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
                _phase = 33;
                return;
            case 33:
                Require(state.Active && !state.HasDraft && state.Definitions.Contains(LedgeDefinition),
                    "missing-definition fixture could not activate with the unused ledge definition available");
                state.SetSettings(LedgeDefinition, VoxelOrientation.Identity, VoxelCellOperation.Add);
                view.Present();
                ReplaceNumber(view.CoordinateInputs[0], CellToolMissingDefinitionCell.X);
                ReplaceNumber(view.CoordinateInputs[1], CellToolMissingDefinitionCell.Y);
                ReplaceNumber(view.CoordinateInputs[2], CellToolMissingDefinitionCell.Z);
                _phase++;
                return;
            case 34:
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
            case 35:
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
            case 36:
                Require(view.DefinitionInput.GetPopup().Visible,
                    "definition popup did not reopen after restoring content");
                ChooseOpenOption(view.DefinitionInput, _cellToolDefinitionIndex);
                _phase++;
                return;
            case 37:
                Require(state.HasDraft && state.DefinitionId == _cellToolDefinition && state.Preview.Succeeded,
                    "reselecting a valid definition did not allow the preserved draft to continue editing");
                Click(view.DiscardButton);
                Click(view.SelectToolButton);
                _phase = 38;
                return;
            case 38:
                Click(view.VoxelToolButton);
                Click(view.PlaneInput);
                _phase++;
                return;
            case 39:
                ChooseOpenOption(view.PlaneInput, (int)VoxelWorkPlane.VerticalX);
                _phase++;
                return;
            case 40:
                var xBehind = BehindPlaneTarget(session.Identity.Bounds, VoxelWorkPlane.VerticalX);
                ReplaceNumber(view.LayerInput, xBehind.Layer);
                _noTargetScreen = xBehind.Screen;
                Motion(_noTargetScreen);
                _phase = 41;
                return;
            case 41:
                Require(state.Plane == VoxelWorkPlane.VerticalX && !state.HasDraft,
                    "vertical X no-target fixture changed plane or created a draft");
                RequirePointerAt(_noTargetScreen);
                RequireTargetGuard(session, view, "vertical X plane accepted a target behind the camera");
                ReplaceNumber(view.LayerInput, VerticalXCell.X);
                _phase = 42;
                return;
            case 42:
                Require(state.Plane == VoxelWorkPlane.VerticalX && state.Layer == VerticalXCell.X && !state.HasDraft,
                    "real plane selection did not establish the X layer");
                Motion(VerticalScreen(VerticalXCell, VoxelWorkPlane.VerticalX));
                _phase++;
                return;
            case 43:
                Require(state.Position == VerticalXCell && state.Preview.Succeeded && !state.HasDraft,
                    $"X plane mouse preview targeted {state.Position}, expected {VerticalXCell}");
                ClickAt(VerticalScreen(VerticalXCell, VoxelWorkPlane.VerticalX));
                _phase++;
                return;
            case 44:
                Require(session.Query(VerticalXCell).Voxel is not null && !state.HasDraft,
                    "real X plane click did not commit its preview");
                Click(view.PlaneInput);
                _phase++;
                return;
            case 45:
                ChooseOpenOption(view.PlaneInput, (int)VoxelWorkPlane.VerticalZ);
                _phase = 46;
                return;
            case 46:
                scene.Observation!.Observer.Look(Math.PI, 0);
                _phase++;
                return;
            case 47:
                var zBehind = BehindPlaneTarget(session.Identity.Bounds, VoxelWorkPlane.VerticalZ);
                ReplaceNumber(view.LayerInput, zBehind.Layer);
                _noTargetScreen = zBehind.Screen;
                Motion(_noTargetScreen);
                _phase++;
                return;
            case 48:
                Require(state.Plane == VoxelWorkPlane.VerticalZ && !state.HasDraft,
                    "vertical Z no-target fixture changed plane or created a draft");
                RequirePointerAt(_noTargetScreen);
                RequireTargetGuard(session, view, "vertical Z plane accepted a target behind the camera");
                scene.Observation!.Observer.Look(-Math.PI, 0);
                ReplaceNumber(view.LayerInput, VerticalZCell.Z);
                _phase = 49;
                return;
            case 49:
                Require(state.Plane == VoxelWorkPlane.VerticalZ && state.Layer == VerticalZCell.Z,
                    "real plane selection did not establish the Z layer");
                Motion(VerticalScreen(VerticalZCell, VoxelWorkPlane.VerticalZ));
                _phase++;
                return;
            case 50:
                Require(state.Position == VerticalZCell && state.Preview.Succeeded,
                    $"Z plane mouse preview targeted {state.Position}, expected {VerticalZCell}");
                ClickAt(VerticalScreen(VerticalZCell, VoxelWorkPlane.VerticalZ));
                _phase++;
                return;
            case 51:
                Require(session.Query(VerticalZCell).Voxel is not null && !state.HasDraft,
                    "real Z plane click did not commit its preview");
                _physicsFrameTarget = Engine.GetPhysicsFrames() + 2;
                Click(view.PlaneInput);
                _phase++;
                return;
            case 52:
                ChooseOpenOption(view.PlaneInput, (int)VoxelWorkPlane.Surface);
                _phase++;
                return;
            case 53:
                if (Engine.GetPhysicsFrames() < _physicsFrameTarget)
                    return;
                _noTargetScreen = PlaneScreen(new WorldVoxelCoord(2, 3, 2));
                Motion(_noTargetScreen);
                _phase = 54;
                return;
            case 54:
                Require(state.Plane == VoxelWorkPlane.Surface && !state.HasDraft,
                    "surface no-target fixture changed plane or created a draft");
                RequirePointerAt(_noTargetScreen);
                RequireTargetGuard(session, view, "surface plane accepted a pointer without a world hit");
                Require(!scene.Observation!.PointerCoordinatesVisible,
                    "pointer coordinates remained visible without a surface, plane target or vehicle hit");
                Motion(SurfaceScreen(VerticalZCell));
                _phase = 55;
                return;
            case 55:
                var hit = scene.Observation!.LastPointerPick?.WorldHit;
                Require(state.Plane == VoxelWorkPlane.Surface && hit?.Cell == VerticalZCell && hit.Normal == Vector3.Up &&
                        hit.AdjacentCell == SurfaceOutsideCell && state.Position == SurfaceOutsideCell && state.Preview.Succeeded,
                    $"surface preview did not target the outside cell: hit={hit?.Cell} normal={hit?.Normal} target={state.Position}");
                _cellToolLookBeforeDrag = scene.Observation.Observer.Pose.Look;
                _cellToolSelectionBeforeDrag = scene.Observation.Observer.Selection;
                var dragScreen = SurfaceScreen(VerticalZCell);
                var dragMotion = scene.GetViewport().GetScreenTransform().BasisXform(new Vector2(6, 0));
                Input.ParseInputEvent(new InputEventMouseButton
                    { ButtonIndex = MouseButton.Right, Pressed = true, Position = dragScreen, GlobalPosition = dragScreen });
                Motion(dragScreen + dragMotion, dragMotion, MouseButtonMask.Right);
                _phase++;
                return;
            case 56:
                Require(Input.IsMouseButtonPressed(MouseButton.Right) &&
                        (scene.Observation!.Observer.Pose.Look - _cellToolLookBeforeDrag).Length > 1e-3,
                    "right-drag input did not turn the camera before the cell-tool click");
                var dragClickScreen = scene.Camera.UnprojectPosition(new Vector3(VerticalZCell.X + 0.5f, VerticalZCell.Y + 1, VerticalZCell.Z + 0.5f));
                Require(scene.Observation!.WorldRect.HasPoint(dragClickScreen), "drag fixture clicked outside the world");
                // Viewport-local clicks keep window stretching from releasing focus during this held-button gesture.
                scene.GetViewport().PushInput(new InputEventMouseButton
                    { ButtonIndex = MouseButton.Left, Pressed = true, Position = dragClickScreen, GlobalPosition = dragClickScreen }, inLocalCoords: true);
                _phase++;
                return;
            case 57:
                Require(Input.IsMouseButtonPressed(MouseButton.Right), "drag-click fixture released the right button before committing");
                Require(session.Query(SurfaceOutsideCell).Voxel is not null && !state.HasDraft,
                    "left click during right-drag did not place outside the picked surface");
                Require(scene.Observation!.Observer.Selection == _cellToolSelectionBeforeDrag && !view.DraftPrompt.Visible,
                    "left click during right-drag changed the inspector selection or requested a draft decision");
                var releasePoint = scene.Camera.UnprojectPosition(new Vector3(VerticalZCell.X + 0.5f, VerticalZCell.Y + 1, VerticalZCell.Z + 0.5f));
                scene.GetViewport().PushInput(new InputEventMouseButton
                    { ButtonIndex = MouseButton.Left, Pressed = false, Position = releasePoint, GlobalPosition = releasePoint }, inLocalCoords: true);
                var releaseScreen = SurfaceScreen(VerticalZCell);
                Motion(releaseScreen, scene.GetViewport().GetScreenTransform().BasisXform(new Vector2(-6, 0)), MouseButtonMask.Right);
                Input.ParseInputEvent(new InputEventMouseButton
                    { ButtonIndex = MouseButton.Right, Pressed = false, Position = releaseScreen, GlobalPosition = releaseScreen });
                Motion(ScreenCenter(view.OperationInput.GetGlobalRect()));
                _phase++;
                return;
            case 58:
                Require((scene.Observation!.Observer.Pose.Look - _cellToolLookBeforeDrag).Length < 1e-8,
                    "right-drag fixture did not restore the camera direction");
                Click(view.OperationInput);
                _phase++;
                return;
            case 59:
                ChooseOpenOption(view.OperationInput, (int)VoxelCellOperation.Remove);
                _phase++;
                return;
            case 60:
                Require(state.Position == VerticalZCell && state.Operation == VoxelCellOperation.Remove && state.Preview.Succeeded && !state.HasDraft,
                    "changing the surface operation did not retarget the shared preview to the hit cell");
                Click(view.CommitButton);
                _phase++;
                return;
            case 61:
                Require(!session.Query(VerticalZCell).IsOccupied && session.Query(SurfaceOutsideCell).IsOccupied && !state.HasDraft,
                    "surface operation button changed the neighbor instead of removing the hit cell");
                Click(view.OperationInput);
                _phase++;
                return;
            case 62:
                ChooseOpenOption(view.OperationInput, (int)VoxelCellOperation.Add);
                _phase++;
                return;
            case 63:
                Require(view.CommitButton.Disabled && !state.HasDraft, "removed surface still allowed a cached target to be committed");
                _surfaceRemovedNextEdit = session.NextEditId;
                Click(view.CommitButton);
                _phase++;
                return;
            case 64:
                Require(session.NextEditId == _surfaceRemovedNextEdit && !state.HasDraft &&
                        !session.Query(VerticalZCell).IsOccupied && session.Query(SurfaceOutsideCell).IsOccupied,
                    "a commit without a live surface changed the world");
                Require(session.SubmitEdit(new VoxelCellSetEdit(new[]
                {
                    new VoxelCellTarget(VerticalZCell, VoxelCellOperation.Add, new VoxelCellContent(state.DefinitionId, state.Orientation))
                })).Result is VoxelCellSetResult { Status: VoxelCellSetStatus.Applied }, "could not restore the surface fixture");
                _physicsFrameTarget = Engine.GetPhysicsFrames() + 2;
                _phase++;
                return;
            case 65:
                if (Engine.GetPhysicsFrames() < _physicsFrameTarget)
                    return;
                Motion(SurfaceScreen(SurfaceOutsideCell));
                _phase++;
                return;
            case 66:
                Require(state.Position == SurfaceOutsideCell with { Y = 7 } && state.Operation == VoxelCellOperation.Add && !state.HasDraft,
                    $"a fresh surface pick did not restore addition at the new outside neighbor: {state.Position}");
                ReplaceNumber(view.CoordinateInputs[1], 9);
                _phase++;
                return;
            case 67:
                Require(state.HasDraft && state.Position == SurfaceOutsideCell with { Y = 9 } &&
                        state.CurrentEdit.Targets.Single().Position == state.Position,
                    "surface precise input did not share the preview across heights");
                Motion(SurfaceScreen(VerticalZCell));
                Click(view.PlaneInput);
                _phase++;
                return;
            case 68:
                ChooseOpenOption(view.PlaneInput, (int)VoxelWorkPlane.Horizontal);
                _phase++;
                return;
            case 69:
                Require(state.Position == SurfaceOutsideCell with { Y = 9 } && state.Plane == VoxelWorkPlane.Surface && view.DraftPrompt.Visible,
                    "plane switch redirected an existing surface draft");
                Click(view.DraftDiscardButton);
                _phase++;
                return;
            case 70:
                Require(!state.HasDraft && state.Plane == VoxelWorkPlane.Horizontal,
                    "explicit draft discard did not apply the requested plane");
                ReplaceNumber(view.LayerInput, 3);
                _phase++;
                return;
            case 71:
                Require(state.Active && !state.HasDraft && state.Layer == 3 && scene.Camera.GlobalPosition == _cellToolCamera,
                    "work-plane editing changed camera state or left a draft");
                Click(view.SelectToolButton);
                GD.Print("WORKSPACE_WORK_PLANES vertical_x=true vertical_z=true surface_outside=true operation_retarget=true removed_surface_invalid=true exact_shared=true draft_guard=true local_box_faces=true non_pickable=true");
                Click(view.VoxelToolButton);
                Click(view.PlaneInput);
                _phase++;
                return;
            case 72:
                Require(view.PlaneInput.GetPopup().Visible, "plane popup did not open before brush mode selection");
                ChooseOpenOption(view.PlaneInput, (int)VoxelWorkPlane.VerticalX);
                _phase++;
                return;
            case 73:
                Require(state.Mode == WorldEditorVoxelMode.Cell && state.Plane == VoxelWorkPlane.VerticalX && !state.HasDraft,
                    "real plane input did not establish a non-horizontal cell plane before brush selection");
                Click(view.ModeInput);
                _phase++;
                return;
            case 74:
                Require(view.ModeInput.GetPopup().Visible, "region mode popup did not open for brush selection");
                ChooseOpenOption(view.ModeInput, (int)WorldEditorVoxelMode.Brush);
                ReplaceNumber(view.BrushSizeInput, 2);
                Motion(PlaneScreen(new WorldVoxelCoord(20, 3, 10)));
                _phase++;
                return;
            case 75:
                Require(state.Mode == WorldEditorVoxelMode.Brush && state.Plane == VoxelWorkPlane.Horizontal &&
                        state.Layer == 3 && state.Position == new WorldVoxelCoord(20, 3, 10) &&
                        state.BrushSize == 2 && state.Preview.Succeeded && !state.HasDraft,
                    "brush mode did not restore horizontal targeting after a non-horizontal cell plane");
                _regionEditCursor = session.NextEditId;
                _regionUndoCount = session.UndoCount;
                MouseLeft(PlaneScreen(new WorldVoxelCoord(20, 3, 10)), true);
                _phase++;
                return;
            case 76:
                Require(state.StrokeInProgress && state.HasDraft && session.NextEditId == _regionEditCursor &&
                        session.UndoCount == _regionUndoCount,
                    "brush press wrote the world before release");
                HeldMotion(PlaneScreen(new WorldVoxelCoord(21, 3, 10)));
                _phase++;
                return;
            case 77:
                Require(state.StrokeInProgress && session.NextEditId == _regionEditCursor &&
                        session.UndoCount == _regionUndoCount,
                    "first held brush motion wrote the world");
                HeldMotion(PlaneScreen(new WorldVoxelCoord(22, 3, 10)));
                _phase++;
                return;
            case 78:
                Require(state.StrokeInProgress && state.CurrentEdit.Targets.Count == 8 &&
                        session.NextEditId == _regionEditCursor && session.UndoCount == _regionUndoCount,
                    "multi-motion brush did not retain one unwritten continuous draft");
                _committedBrushCells = state.CurrentEdit.Targets.Select(target => target.Position).ToArray();
                MouseLeft(ScreenCenter(scene.Observation!.PanelRect), false);
                _phase++;
                return;
            case 79:
                Require(!state.StrokeInProgress && !state.HasDraft && session.NextEditId == _regionEditCursor + 1 &&
                        session.UndoCount == _regionUndoCount + 1 &&
                        _committedBrushCells.All(cell => session.Query(cell).Voxel is not null),
                    "brush release over the inspector panel did not create exactly one formal edit and one undo record");
                _regionEditCursor = session.NextEditId;
                var outsideStart = PlaneScreen(new WorldVoxelCoord(20, 3, 13));
                var outsideStartViewport = scene.GetViewport().GetScreenTransform().AffineInverse() * outsideStart;
                Require(scene.Observation!.WorldRect.HasPoint(outsideStartViewport),
                    $"outside-release fixture start is outside WorldRect: start={outsideStart} " +
                    $"viewport={outsideStartViewport} world={scene.Observation.WorldRect} " +
                    $"screen_transform={scene.GetViewport().GetScreenTransform()}");
                MouseLeft(outsideStart, true);
                _phase++;
                return;
            case 80:
                Require(state.StrokeInProgress && state.HasDraft && state.Position == new WorldVoxelCoord(20, 3, 13) &&
                        session.NextEditId == _regionEditCursor,
                    $"outside-release press did not begin a legal stroke: stroke={state.StrokeInProgress} draft={state.HasDraft} " +
                    $"position={state.Position} next={session.NextEditId} expected_next={_regionEditCursor}");
                _outsideReleaseBrushCells = state.CurrentEdit.Targets.Select(target => target.Position).ToArray();
                HeldMotion(ScreenCenter(scene.Observation!.PanelRect));
                _phase++;
                return;
            case 81:
                Require(state.StrokeInProgress && state.HasDraft &&
                        state.CurrentEdit.Targets.Select(target => target.Position).SequenceEqual(_outsideReleaseBrushCells),
                    $"panel motion changed or interrupted the legal stroke: stroke={state.StrokeInProgress} draft={state.HasDraft} " +
                    $"targets={state.CurrentEdit.Targets.Count} expected={_outsideReleaseBrushCells.Count} " +
                    $"cells={string.Join(';', state.CurrentEdit.Targets.Select(target => target.Position))}");
                scene.Observation!.Observer.Look(0, Math.PI / 6);
                _phase++;
                return;
            case 82:
                var noPlane = FindNoPlaneScreen();
                MouseLeft(noPlane, false);
                _phase++;
                return;
            case 83:
                Require(!state.StrokeInProgress && !state.HasDraft && session.NextEditId == _regionEditCursor + 1 &&
                        _outsideReleaseBrushCells.Count > 0 &&
                        _outsideReleaseBrushCells.All(cell => session.Query(cell).Voxel is not null),
                    $"release without a work-plane intersection did not submit the last legal brush range exactly once: " +
                    $"stroke={state.StrokeInProgress} draft={state.HasDraft} next={session.NextEditId} " +
                    $"expected_next={_regionEditCursor + 1} targets={state.CurrentEdit.Targets.Count} " +
                    $"region_status={state.RegionPreview.Region.Status} region_count={state.RegionPreview.Region.Count} " +
                    $"region_cells={string.Join(';', state.RegionPreview.Region.Cells)} " +
                    $"expected_cells={string.Join(';', _outsideReleaseBrushCells)}");
                scene.Observation!.Observer.Look(0, -Math.PI / 6);
                _phase++;
                return;
            case 84:
                _regionEditCursor = session.NextEditId;
                var focusStart = PlaneScreen(new WorldVoxelCoord(20, 3, 13));
                RequireWorldInputPoint(focusStart, "focus-out press");
                MouseLeft(focusStart, true);
                _phase++;
                return;
            case 85:
                Require(state.StrokeInProgress && state.HasDraft && state.Position == new WorldVoxelCoord(20, 3, 13),
                    BrushInputState("focus-out brush press did not begin a stroke", state, session));
                HeldMotion(PlaneScreen(new WorldVoxelCoord(21, 3, 13)));
                _phase++;
                return;
            case 86:
                Require(state.StrokeInProgress && state.HasDraft && state.CurrentEdit.Targets.Count > 0,
                    BrushInputState("focus-out brush motion did not extend the active stroke", state, session));
                scene.GetTree().Root.PropagateNotification((int)Node.NotificationWMWindowFocusOut);
                _phase++;
                return;
            case 87:
                Require(!state.StrokeInProgress && state.HasDraft && session.NextEditId == _regionEditCursor,
                    BrushInputState("window focus-out submitted or discarded the interrupted brush draft", state, session));
                MouseLeft(PlaneScreen(new WorldVoxelCoord(21, 3, 13)), false);
                _phase++;
                return;
            case 88:
                Require(!state.StrokeInProgress && state.HasDraft && session.NextEditId == _regionEditCursor,
                    BrushInputState("release after focus-out submitted the interrupted brush draft", state, session));
                Click(view.CommitButton);
                _phase++;
                return;
            case 89:
                Require(!state.HasDraft && session.NextEditId == _regionEditCursor + 1,
                    BrushInputState("explicit commit after focus-out failed", state, session));
                _regionEditCursor = session.NextEditId;
                var consoleStart = PlaneScreen(new WorldVoxelCoord(24, 3, 14));
                RequireWorldInputPoint(consoleStart, "console-interruption press");
                MouseLeft(consoleStart, true);
                _phase++;
                return;
            case 90:
                Require(state.StrokeInProgress && state.HasDraft && state.Position == new WorldVoxelCoord(24, 3, 14),
                    BrushInputState("console-interruption press did not begin a stroke", state, session));
                HeldMotion(PlaneScreen(new WorldVoxelCoord(25, 3, 14)));
                _phase++;
                return;
            case 91:
                Require(state.StrokeInProgress && state.HasDraft && state.CurrentEdit.Targets.Count > 0,
                    BrushInputState("console-interruption motion did not extend the active stroke", state, session));
                SendKey(Godot.Key.Quoteleft, pressed: true);
                SendKey(Godot.Key.Quoteleft, pressed: false);
                _phase++;
                return;
            case 92:
                Require(scene.GetNode<Main>("/root/Main").ConsoleOpen && !state.StrokeInProgress && state.HasDraft &&
                        session.NextEditId == _regionEditCursor,
                    BrushInputState("opening the console did not preserve and interrupt the active brush draft", state, session));
                MouseLeft(PlaneScreen(new WorldVoxelCoord(25, 3, 14)), false);
                _phase++;
                return;
            case 93:
                Require(scene.GetNode<Main>("/root/Main").ConsoleOpen && !state.StrokeInProgress && state.HasDraft &&
                        session.NextEditId == _regionEditCursor,
                    BrushInputState("release while console was open submitted the brush draft", state, session));
                SendKey(Godot.Key.Quoteleft, pressed: true);
                SendKey(Godot.Key.Quoteleft, pressed: false);
                _phase++;
                return;
            case 94:
                Require(!scene.GetNode<Main>("/root/Main").ConsoleOpen && state.HasDraft &&
                        session.NextEditId == _regionEditCursor,
                    BrushInputState("closing the console changed the interrupted brush draft", state, session));
                _brushBeforeMove = state.CurrentEdit.Targets.Select(target => target.Position).ToArray();
                ReplaceNumber(view.CoordinateInputs[0], state.Position.X + 1);
                _phase++;
                return;
            case 95:
                var movedBrush = state.CurrentEdit.Targets.Select(target => target.Position).ToArray();
                var expectedMovedBrush = _brushBeforeMove.Select(cell => cell with { X = cell.X + 1 }).ToArray();
                Require(movedBrush.SequenceEqual(expectedMovedBrush),
                    BrushInputState("brush inspector coordinate did not translate the complete stroke without changing its shape", state, session) +
                    $" input_x={view.CoordinateInputs[0].Value} before={string.Join(';', _brushBeforeMove)} " +
                    $"expected={string.Join(';', expectedMovedBrush)} actual={string.Join(';', movedBrush)}");
                Require(scene.ApplyHistory(redo: false) == "DraftApplied",
                    BrushInputState("brush draft undo command was not applied", state, session));
                _phase++;
                return;
            case 96:
                Require(state.CurrentEdit.Targets.Select(target => target.Position).SequenceEqual(_brushBeforeMove),
                    BrushInputState("brush draft undo did not restore the translated stroke", state, session) +
                    $" input_x={view.CoordinateInputs[0].Value} before={string.Join(';', _brushBeforeMove)} " +
                    $"actual={string.Join(';', state.CurrentEdit.Targets.Select(target => target.Position))}");
                var redrawStart = PlaneScreen(new WorldVoxelCoord(24, 3, 14));
                RequireWorldInputPoint(redrawStart, "brush-redraw prompt press");
                ClickAt(redrawStart);
                _phase++;
                return;
            case 97:
                Require(view.DraftPrompt.Visible && state.HasDraft,
                    BrushInputState("pressing with a complete brush draft did not offer the shared draft decision", state, session));
                Click(view.DraftDiscardButton);
                _phase++;
                return;
            case 98:
                Require(!state.HasDraft && !state.StrokeInProgress && state.Position == new WorldVoxelCoord(24, 3, 14),
                    BrushInputState("discarding the interrupted brush did not leave a hover target for a fresh press", state, session));
                var freshStart = PlaneScreen(new WorldVoxelCoord(24, 3, 14));
                RequireWorldInputPoint(freshStart, "brush-redraw fresh press");
                MouseLeft(freshStart, true);
                _phase++;
                return;
            case 99:
                Require(state.StrokeInProgress && state.HasDraft && state.Position == new WorldVoxelCoord(24, 3, 14),
                    BrushInputState("fresh brush press after discard did not begin a stroke", state, session));
                HeldMotion(PlaneScreen(new WorldVoxelCoord(25, 3, 14)));
                _phase++;
                return;
            case 100:
                Require(state.StrokeInProgress && state.HasDraft,
                    BrushInputState("fresh brush motion after discard did not remain active", state, session));
                _redrawnBrushCells = state.CurrentEdit.Targets.Select(target => target.Position).ToArray();
                MouseLeft(PlaneScreen(new WorldVoxelCoord(25, 3, 14)), false);
                _phase++;
                return;
            case 101:
                Require(!state.HasDraft && !state.StrokeInProgress && session.NextEditId == _regionEditCursor + 1 &&
                        _redrawnBrushCells.Count == 6 && _redrawnBrushCells.All(cell => session.Query(cell).Voxel is not null),
                    BrushInputState("fresh press-drag-release after discarding an interrupted brush did not submit one replacement stroke", state, session));
                Click(view.ModeInput);
                _phase++;
                return;
            case 102:
                Require(view.ModeInput.GetPopup().Visible, "mode popup did not open before the box plane fixture");
                ChooseOpenOption(view.ModeInput, (int)WorldEditorVoxelMode.Cell);
                _phase++;
                return;
            case 103:
                Require(state.Mode == WorldEditorVoxelMode.Cell && state.Plane == VoxelWorkPlane.Horizontal && !state.HasDraft,
                    "returning to cell mode changed the completed brush state");
                Click(view.PlaneInput);
                _phase++;
                return;
            case 104:
                Require(view.PlaneInput.GetPopup().Visible, "plane popup did not open before box mode selection");
                ChooseOpenOption(view.PlaneInput, (int)VoxelWorkPlane.Surface);
                _phase++;
                return;
            case 105:
                Require(state.Mode == WorldEditorVoxelMode.Cell && state.Plane == VoxelWorkPlane.Surface && !state.HasDraft,
                    "real plane input did not establish surface mode before box selection");
                Click(view.ModeInput);
                _phase++;
                return;
            case 106:
                Require(view.ModeInput.GetPopup().Visible, "discarding and redrawing the brush did not restore mode selection");
                ChooseOpenOption(view.ModeInput, (int)WorldEditorVoxelMode.Box);
                _phase++;
                return;
            case 107:
                Require(state.Mode == WorldEditorVoxelMode.Box && state.Plane == VoxelWorkPlane.Horizontal && !state.HasDraft,
                    "box mode did not restore horizontal targeting after a surface cell plane");
                ReplaceNumber(view.LayerInput, 4);
                ReplaceNumber(view.BoxHeightInput, 2);
                Motion(PlaneScreen(new WorldVoxelCoord(24, 4, 10)));
                _phase++;
                return;
            case 108:
                Require(state.Mode == WorldEditorVoxelMode.Box && state.Plane == VoxelWorkPlane.Horizontal &&
                        state.Layer == 4 && state.Position == new WorldVoxelCoord(24, 4, 10) &&
                        state.BoxHeight == 2 && !state.HasDraft,
                    "box controls did not retain horizontal target calculation before corner input");
                ClickAt(PlaneScreen(new WorldVoxelCoord(24, 4, 10)));
                Motion(PlaneScreen(new WorldVoxelCoord(26, 4, 11)));
                _phase++;
                return;
            case 109:
                Require(state.Mode == WorldEditorVoxelMode.Box && state.BoxHeight == 2 && state.BoxCornerPending &&
                        state.Position == new WorldVoxelCoord(24, 4, 10) && state.BoxCorner == new WorldVoxelCoord(26, 4, 11),
                    $"box first corner and mouse-motion second corner did not share one preview: mode={state.Mode} layer={state.Layer} boxHeight={state.BoxHeight} draft={state.HasDraft} pending={state.BoxCornerPending} first={state.Position} second={state.BoxCorner}");
                ClickAt(PlaneScreen(new WorldVoxelCoord(26, 4, 11)));
                _phase++;
                return;
            case 110:
                Require(state.HasDraft && !state.BoxCornerPending && state.BoxCorner == new WorldVoxelCoord(26, 4, 11),
                    "second box click did not complete the shared region draft");
                ClickAt(PlaneScreen(new WorldVoxelCoord(28, 4, 12)));
                _phase++;
                return;
            case 111:
                Require(view.DraftPrompt.Visible && state.HasDraft && !state.BoxCornerPending,
                    "pressing with a complete box draft did not offer the shared draft decision");
                Click(view.DraftPrompt.GetCancelButton());
                _phase++;
                return;
            case 112:
                Require(state.HasDraft && !state.BoxCornerPending, "continuing a complete box draft changed its corners");
                ReplaceNumber(view.BoxCornerInputs[0], 27);
                _phase++;
                return;
            case 113:
                Require(state.HasDraft && !state.BoxCornerPending && state.BoxCorner == new WorldVoxelCoord(27, 4, 11) &&
                        state.RegionPreview.Region.Count == 16 && state.RegionPreview.AffectedCount == 16,
                    "box inspector input did not modify the mouse-created region preview");
                _committedBoxCells = state.CurrentEdit.Targets.Select(target => target.Position).ToArray();
                Click(view.CommitButton);
                _phase++;
                return;
            case 114:
                Require(!state.HasDraft && _committedBoxCells.All(cell => session.Query(cell).Voxel is not null),
                    "box commit did not apply the shared mouse and inspector state");
                ReplaceNumber(view.BoxHeightInput, 1);
                ReplaceNumber(view.CoordinateInputs[0], 0);
                ReplaceNumber(view.CoordinateInputs[1], 6);
                ReplaceNumber(view.CoordinateInputs[2], 0);
                ReplaceNumber(view.BoxCornerInputs[0], 32);
                ReplaceNumber(view.BoxCornerInputs[2], 18);
                _regionEditCursor = session.NextEditId;
                _regionUndoCount = session.UndoCount;
                _phase++;
                return;
            case 115:
                Require(state.RegionPreview.Region.Count == 627 && state.RegionPreview.AffectedCount == 626 &&
                        state.RegionPreview.SkippedCount == 1 && state.RegionPreview.RequiresConfirmation,
                    "large box did not skip the existing surface-neighbor cell or require confirmation");
                Click(view.CommitButton);
                _phase++;
                return;
            case 116:
                Require(view.LargeEditPrompt.Visible && state.HasDraft && session.NextEditId == _regionEditCursor &&
                        session.Query(new WorldVoxelCoord(0, 6, 0)).Voxel is null,
                    "large box submission wrote before confirmation");
                Click(view.LargeEditPrompt.GetCancelButton());
                _phase++;
                return;
            case 117:
                Require(!view.LargeEditPrompt.Visible && state.HasDraft && session.NextEditId == _regionEditCursor,
                    "cancelling large box confirmation wrote or discarded its draft");
                Click(view.CommitButton);
                _phase++;
                return;
            case 118:
                Require(view.LargeEditPrompt.Visible, "large box confirmation did not reopen");
                Click(view.LargeEditPrompt.GetOkButton());
                _phase++;
                return;
            case 119:
                Require(!state.HasDraft && session.NextEditId == _regionEditCursor + 1 &&
                        session.UndoCount == _regionUndoCount + 1 && session.World.VoxelCount == 666,
                    "confirmed large box did not submit once as a single undo record");
                Click(scene.ActionButton("undo"));
                _phase++;
                return;
            case 120:
                Require(session.World.VoxelCount == 40 && session.Query(new WorldVoxelCoord(0, 6, 0)).Voxel is null,
                    "large box undo did not remove the whole confirmed range");
                Click(scene.ActionButton("redo"));
                _phase++;
                return;
            case 121:
                Require(session.World.VoxelCount == 666, "large box redo did not restore the whole range");
                Click(scene.ActionButton("undo"));
                _phase++;
                return;
            case 122:
                Require(session.World.VoxelCount == 40 && !state.HasDraft,
                    "large box cleanup changed the retained region edits");
                Click(view.ModeInput);
                _phase++;
                return;
            case 123:
                Require(view.ModeInput.GetPopup().Visible, "cell mode popup did not open after box checks");
                ChooseOpenOption(view.ModeInput, (int)WorldEditorVoxelMode.Cell);
                Click(view.SelectToolButton);
                Require(session.Query(CellToolCommittedCellForRun).Voxel is not null &&
                        session.Query(VerticalXCell).Voxel is not null &&
                        CellToolCommittedCellForRun != VerticalXCell,
                    "selection release/motion fixture did not retain two distinct pickable world cells");
                ClickAt(WorldScreen(CellToolCommittedCellForRun));
                Motion(WorldScreen(VerticalXCell));
                _phase++;
                return;
            case 124:
                Require(!state.Active && !state.HasDraft &&
                        scene.Observation!.Observer.Selection is { Kind: ObservedObjectKind.WorldCell } clicked &&
                        clicked.Cell == CellToolCommittedCellForRun,
                    "selection click resolved against the following same-frame pointer motion instead of its release target");
                var pending = VoxelCellRegion.Box(RegionPendingFirst, RegionPendingSecond, 1);
                Require(session.SubmitRegion(pending, VoxelCellOperation.Add,
                            new VoxelCellContent(state.DefinitionId, state.Orientation), atStep: 3,
                            confirmEndHistory: true).Accepted && session.QueryPendingEdits().Count == 1 &&
                        ((VoxelCellSetEdit)session.QueryPendingEdits().Single().Edit).Targets.Count == 2,
                    "region continuation fixture was not accepted as an explicit pending cell list");
                scene.SetSaveName("单格工具跨进程验证");
                var saved = scene.SaveWorld();
                Require(saved.Succeeded && saved.SaveId.HasValue && !state.HasDraft &&
                        session.Query(CellToolCommittedCellForRun).Voxel is not null &&
                        session.Query(RegionPendingFirst).Voxel is null && session.Query(RegionPendingSecond).Voxel is null,
                    "cell-tool save did not capture committed and pending state without its draft");
                state.SetExact(new WorldVoxelCoord(7, 3, 5), state.DefinitionId, state.Orientation, VoxelCellOperation.Add);
                Require(state.HasDraft && scene.SaveWorld().Succeeded && state.HasDraft,
                    "ordinary save discarded the cell draft");
                ClickAt(WorldScreen(VerticalXCell));
                _phase++;
                return;
            case 125:
                Require(view.DraftPrompt.Visible && state.HasDraft,
                    "real target click did not require an explicit draft decision");
                Click(view.DraftDiscardButton);
                _phase++;
                return;
            case 126:
                Require(!state.HasDraft && scene.Observation!.Observer.Selection is { Kind: ObservedObjectKind.WorldCell } selected &&
                        selected.Cell == VerticalXCell,
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
                Require(frame.Transform == session.Vehicles.Single().Transform,
                    "paused vehicle display transform differed from its formal transform");
                _vehicleHoverScreen = scene.GetViewport().GetScreenTransform() *
                    scene.Camera.UnprojectPosition(WorldEditorObservationView.To(frame.Center));
                Motion(_vehicleHoverScreen);
                _phase++;
                return;
            case 127:
                var hover = scene.Observation!.HoverVehicle;
                Require(hover is not null && hover.VehicleId == session.Vehicles.Single().Id && scene.Observation.PointerCoordinatesVisible,
                    "real vehicle hover did not display its world/local coordinates");
                var projected = scene.GetViewport().GetScreenTransform() * scene.Camera.UnprojectPosition(
                    WorldEditorObservationView.To(hover!.WorldPosition));
                Require(projected.DistanceTo(_vehicleHoverScreen) < 2 &&
                        hover.LocalPosition.X >= hover.Cell.X - 1e-6 && hover.LocalPosition.X <= hover.Cell.X + 1 + 1e-6 &&
                        hover.LocalPosition.Y >= hover.Cell.Y - 1e-6 && hover.LocalPosition.Y <= hover.Cell.Y + 1 + 1e-6 &&
                        hover.LocalPosition.Z >= hover.Cell.Z - 1e-6 && hover.LocalPosition.Z <= hover.Cell.Z + 1 + 1e-6,
                    $"vehicle hit coordinates did not match the real pointer or hit cell: screen={projected} expected={_vehicleHoverScreen} local={hover.LocalPosition} cell={hover.Cell}");
                GD.Print($"WORKSPACE_VEHICLE_COORDINATES world={hover.WorldPosition} local={hover.LocalPosition} visible=true");
                _phase++;
                return;
            case 128:
                Click(scene.Observation!.ActionButton("whole"));
                _phase++;
                return;
            case 129:
                Require(view.DraftPrompt.Visible && state.HasDraft && scene.Observation!.Observer.PartVehicleId is not null,
                    "level switch did not require an explicit draft decision");
                Click(view.DraftCommitButton);
                _phase++;
                return;
            case 130:
                Require(!state.HasDraft && scene.Observation!.Observer.PartVehicleId is null &&
                        session.Query(new WorldVoxelCoord(8, 3, 5)).Voxel is not null,
                    "committing the level-switch draft did not apply it and return to the whole vehicle");
                state.SetExact(new WorldVoxelCoord(9, 3, 5), state.DefinitionId, state.Orientation, VoxelCellOperation.Add);
                Click(scene.ActionButton("workspace"));
                _phase++;
                return;
            case 131:
                Require(scene.PromptVisible && state.HasDraft && scene.WorldChangePrompt?.Visible == true,
                    "leaving the world did not route its draft through the unified protection");
                Click(scene.DraftLeaveDiscardButton);
                _phase++;
                return;
            case 132:
                Require(scene.PromptVisible && !state.HasDraft,
                    "leaving draft discard did not return to the remaining unsaved-world decision");
                Click(scene.WorldChangePrompt?.CancelButton ?? throw new InvalidOperationException("leave cancel is unavailable"));
                _phase++;
                return;
            case 133:
                Require(!scene.PromptVisible && scene.GetTree().CurrentScene == scene,
                    "cancelling after the draft decision left the editor");
                GD.Print($"WORKSPACE_CELL_TOOL_INPUT layer={state.Layer} mouse_cell={CellToolCommittedCellForRun} settings_follow=true replace_collision=true missing_definition_kept=true exact_shared=true rejected_kept=true tool_guard=discard target_guard=discard level_guard=commit leave_guard=discard save_kept_draft=true");
                GD.Print($"WORKSPACE_REGION_INPUT brush_cells={_committedBrushCells.Count} brush_release_one_edit=true no_plane_release_commit=true outside_motion_ignored=true focus_commit=true console_discard=true brush_draft_prompt=true brush_translate_undo=true brush_redraw_after_discard=true box_cells={_committedBoxCells.Count} box_draft_prompt=true shared_preview=true large_confirm_cancel=true large_confirm_commit=true large_undo_redo=true");
                GD.Print($"WORKSPACE_CELL_TOOL_SAVED id={session.SaveId.GetValueOrDefault():D} committed=true pending=1 draft=false pending_region_cells=2");
                _finished = true;
                Done(scene);
                return;
        }
    }

    private WorldVoxelCoord CellToolCommittedCellForRun { get; set; }

    private Vector2 VerticalScreen(WorldVoxelCoord cell, VoxelWorkPlane plane) => scene.GetViewport().GetScreenTransform() *
        scene.Camera.UnprojectPosition(plane == VoxelWorkPlane.VerticalX
            ? new Vector3(cell.X, cell.Y + 0.5f, cell.Z + 0.5f)
            : new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z));

    private Vector2 SurfaceScreen(WorldVoxelCoord cell) => scene.GetViewport().GetScreenTransform() *
        scene.Camera.UnprojectPosition(new Vector3(cell.X + 0.5f, cell.Y + 1, cell.Z + 0.5f));

    private (int Layer, Vector2 Screen) BehindPlaneTarget(VoxelWorldBounds bounds, VoxelWorkPlane plane)
    {
        var axis = plane switch
        {
            VoxelWorkPlane.Horizontal => 1,
            VoxelWorkPlane.VerticalX => 0,
            VoxelWorkPlane.VerticalZ => 2,
            _ => throw new InvalidOperationException($"{plane} has no fixed-axis plane")
        };
        var layerCount = axis switch { 0 => bounds.SizeX, 1 => bounds.SizeY, _ => bounds.SizeZ };
        var rect = scene.Observation!.WorldRect;
        for (var layer = 0; layer < layerCount; layer++)
        for (var row = 1; row < 8; row++)
        for (var column = 1; column < 8; column++)
        {
            var screen = rect.Position + new Vector2(rect.Size.X * column / 8, rect.Size.Y * row / 8);
            var origin = scene.Camera.ProjectRayOrigin(screen);
            var direction = scene.Camera.ProjectRayNormal(screen);
            var originComponent = axis switch { 0 => origin.X, 1 => origin.Y, _ => origin.Z };
            var directionComponent = axis switch { 0 => direction.X, 1 => direction.Y, _ => direction.Z };
            if (Math.Abs(directionComponent) < 1e-7 ||
                (layer - originComponent) / directionComponent < 0)
                return (layer, scene.GetViewport().GetScreenTransform() * screen);
        }
        throw new InvalidOperationException($"no legal {plane} layer lies behind or parallel to a real world-input ray: camera={scene.Camera.GlobalPosition} direction={scene.Camera.ProjectRayNormal(ScreenCenter(rect))} bounds={bounds}");
    }

    private void RequirePointerAt(Vector2 window)
    {
        var pick = scene.Observation!.LastPointerPick;
        Require(pick is not null &&
                (scene.GetViewport().GetScreenTransform() * pick.Ray.Screen).DistanceTo(window) < 1,
            $"no-target motion did not reach the world view: expected={window} actual={pick?.Ray.Screen}");
    }

    private static void RequireTargetGuard(
        VoxelDevelopmentSession session,
        WorldEditorVoxelToolView view,
        string failure)
    {
        var state = view.State;
        var context = $"plane={state.Plane} layer={state.Layer} previewPosition={state.Position} preview={state.Preview.Status}";
        Require(view.CommitButton.Disabled, $"{failure}: commit button remained enabled; {context}");
        var nextEdit = session.NextEditId;
        var voxelCount = session.World.VoxelCount;
        view.Commit();
        Require(session.NextEditId == nextEdit && session.World.VoxelCount == voxelCount && !state.HasDraft,
            $"{failure}: direct commit changed the edit sequence or world; {context}");
    }

    private static void RequirePlanePointer(
        WorldEditorVoxelTool state,
        ObservationVector? pointer,
        string failure)
    {
        Require(pointer is { } point &&
                (int)Math.Floor(point.X) == state.Position.X &&
                (int)Math.Floor(point.Y) == state.Position.Y &&
                (int)Math.Floor(point.Z) == state.Position.Z,
            $"{failure}: pointer={pointer} preview={state.Position}");
    }

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
                        session.World.VoxelCount == 40 && session.QueryPendingEdits().Count == 1 && !state.HasDraft &&
                        session.Query(VerticalXCell).IsOccupied && session.Query(VerticalZCell).IsOccupied && session.Query(SurfaceOutsideCell).IsOccupied &&
                        session.Query(RegionPendingFirst).Voxel is null && session.Query(RegionPendingSecond).Voxel is null &&
                        ExpectedBrushCells().All(cell => session.Query(cell).Voxel is not null) &&
                        ExpectedBoxCells().All(cell => session.Query(cell).Voxel is not null) &&
                        ((VoxelCellSetEdit)session.QueryPendingEdits().Single().Edit).Targets.Select(target => target.Position)
                            .SequenceEqual(new[] { RegionPendingFirst, RegionPendingSecond }),
                    "fresh process did not restore committed work-plane, brush/box edits and the explicit pending region without a UI draft");
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
                Require(session.Query(RegionPendingFirst).Voxel is not null && session.Query(RegionPendingSecond).Voxel is not null &&
                        session.QueryPendingEdits().Count == 0 &&
                        session.Query(CellToolCommittedCellForRun).Voxel is not null && !state.HasDraft,
                    "continued process did not execute the restored pending edit and keep its mouse edit");
                var saved = scene.SaveWorld();
                Require(saved.Succeeded && saved.SaveId == session.SaveId && !session.HasUnsavedChanges,
                    "continued cell-tool world was not saved back to its own identity");
                GD.Print("WORKSPACE_CELL_TOOL_CONTINUED committed=true pending_executed=true mouse_edit=true draft=false pending_region_cells=2 brush_restored=true box_restored=true");
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

    private Vector2 FindNoPlaneScreen()
    {
        var rect = scene.Observation!.WorldRect;
        var screenTransform = scene.GetViewport().GetScreenTransform();
        for (var yIndex = 0; yIndex <= 32; yIndex++)
        for (var xIndex = 0; xIndex <= 32; xIndex++)
        {
            var screen = new Vector2(
                Mathf.Lerp(rect.Position.X + 1, rect.End.X - 1, xIndex / 32f),
                Mathf.Lerp(rect.Position.Y + 1, rect.End.Y - 1, yIndex / 32f));
            var origin = scene.Camera.ProjectRayOrigin(screen);
            var direction = scene.Camera.ProjectRayNormal(screen);
            if (Math.Abs(direction.Y) >= 1e-7 && (scene.CellTool!.State.Layer - origin.Y) / direction.Y < 0)
            {
                Require(rect.HasPoint(screen), $"no-plane release candidate escaped WorldRect: point={screen} rect={rect}");
                return screenTransform * screen;
            }
        }
        throw new InvalidOperationException($"no viewport point misses the work plane behind the camera: " +
                                            $"world={rect} camera={scene.Camera.GlobalPosition} layer={scene.CellTool!.State.Layer}");
    }

    private void RequireWorldInputPoint(Vector2 screen, string label)
    {
        var transform = scene.GetViewport().GetScreenTransform();
        var viewport = transform.AffineInverse() * screen;
        Require(scene.Observation!.WorldRect.HasPoint(viewport),
            $"{label} is outside world input: screen={screen} viewport={viewport} " +
            $"world={scene.Observation.WorldRect} transform={transform}");
    }

    private string BrushInputState(string reason, WorldEditorVoxelTool state, VoxelDevelopmentSession session) =>
        $"{reason}: stroke={state.StrokeInProgress} draft={state.HasDraft} position={state.Position} " +
        $"targets={state.CurrentEdit.Targets.Count} next={session.NextEditId} expected_next={_regionEditCursor} " +
        $"world_focus={scene.Observation!.WorldHasInput} world={scene.Observation.WorldRect}";

    private static void Click(Control control) =>
        ClickAt(control.GetViewport().GetScreenTransform() * control.GetGlobalRect().GetCenter());

    private static void ClickAt(Vector2 position)
        => GodotSmokeInput.Click(position);

    private static void MouseLeft(Vector2 position, bool pressed) => Input.ParseInputEvent(new InputEventMouseButton
    {
        ButtonIndex = MouseButton.Left,
        ButtonMask = pressed ? MouseButtonMask.Left : (MouseButtonMask)0,
        Pressed = pressed,
        Position = position,
        GlobalPosition = position
    });

    private static void HeldMotion(Vector2 position) => Input.ParseInputEvent(new InputEventMouseMotion
    {
        ButtonMask = MouseButtonMask.Left,
        Position = position,
        GlobalPosition = position
    });

    private static IEnumerable<WorldVoxelCoord> ExpectedBrushCells()
    {
        for (var x = 19; x <= 22; x++)
        for (var z = 9; z <= 10; z++)
            yield return new WorldVoxelCoord(x, 3, z);
        for (var x = 19; x <= 21; x++)
        for (var z = 12; z <= 13; z++)
            yield return new WorldVoxelCoord(x, 3, z);
        for (var x = 23; x <= 25; x++)
        for (var z = 13; z <= 14; z++)
            yield return new WorldVoxelCoord(x, 3, z);
    }

    private static IEnumerable<WorldVoxelCoord> ExpectedBoxCells()
    {
        for (var x = 24; x <= 27; x++)
        for (var y = 4; y <= 5; y++)
        for (var z = 10; z <= 11; z++)
            yield return new WorldVoxelCoord(x, y, z);
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

    private static void Motion(Vector2 position, Vector2 relative = default, MouseButtonMask buttons = default) =>
        Input.ParseInputEvent(new InputEventMouseMotion
            { Position = position, GlobalPosition = position, Relative = relative, ButtonMask = buttons });

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
