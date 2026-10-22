using System.Text.Json;
using Godot;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Scenes.Contracts;
using Train.Ui;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    private static readonly BlueprintAddress EditorBlueprint = new(BlueprintLibrary.Packages, new("core:titan-armed-vehicle"));

    // 内容包蓝图在界面上只能经蓝图库的「复制到本局…」打开；关闭走「退出」和「放弃本次修改」。
    private async Task RunEditorMeasurement()
    {
        var editor = _app.Editor!;
        var panel = _app.GetNode<DesignEditorPanel>("GameUi/Root/DesignEditor");
        _scenePhase = "initial-map";
        var mapStarted = _sceneLastDraw;
        await Wait(() => _sceneLastDraw >= mapStarted + 5);
        _sceneEvidence["initial_map"] = SceneSteady();
        CollectSceneHeap(0, "initial-map");

        _scenePhase = "editor-open";
        var name = _app.Host!.Content!.Blueprints.Single(item => item.Address == EditorBlueprint).Display.Name;
        var library = _app.GetNode<BlueprintLibraryPanel>("GameUi/Root/BlueprintLibrary");
        SceneUiDescendants(_app.GetNode("GameUi")).OfType<Button>().Single(item => item.Name == "BlueprintLibrary")
            .EmitSignal(BaseButton.SignalName.Pressed);
        var copy = SceneUiDescendants(library).OfType<HFlowContainer>()
            .Where(row => row.GetChildCount() > 0 && row.GetChild(0) is Label label && label.Text == name)
            .SelectMany(row => row.GetChildren().OfType<Button>()).Single(item => item.Text == "复制到本局…");
        var click = Seconds();
        copy.EmitSignal(BaseButton.SignalName.Pressed);
        double? session = null;
        long displayedDraw = 0;
        await Wait(() =>
        {
            if (editor.Session is not null) session ??= Seconds();
            if (displayedDraw == 0 && panel.Scene.Display?.DisplayedFrame is { Readiness: SceneReadiness.Ready }) displayedDraw = _sceneDrawCount;
            return displayedDraw > 0 && _sceneDrawCount > displayedDraw;
        });
        var state = editor.State!;
        var evaluation = state.CurrentEvaluation as DesignEvaluation.Complete;
        var meshes = panel.Scene.Display!.Objects.Values.SelectMany(item => item.Source.Visuals)
            .OfType<SceneVisual.Geometry>().Select(item => item.Mesh).ToArray();
        _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "editor-open", blueprint = EditorBlueprint.Id.Value, click, session, first_ready_draw = _sceneLastDraw,
            click_to_session_seconds = session - click, click_to_ready_draw_seconds = _sceneLastDraw - click,
            elements = state.Elements.Length, evaluated_voxels = evaluation?.Scene.Voxels.Length,
            parts = evaluation?.Core?.Design.Parts.Length, meshes = meshes.Length,
            vertices = meshes.Sum(mesh => (long)mesh.Vertices.Length), triangles = meshes.Sum(mesh => (long)mesh.TriangleIndices.Length / 3)
        }, SceneJson));

        _scenePhase = "editor-hold";
        var holdStarted = _sceneLastDraw;
        _sceneTiming = new(holdStarted, _app.Host.GetStatus()!.View.Position.CompletedSteps);
        await Wait(() => _sceneLastDraw >= holdStarted + SampleSeconds);
        _timing = _sceneTiming.Summarize();
        _sceneTiming = null;
        _sceneEvidence["editor_steady"] = SceneSteady();

        await RunEditorSequence(editor, panel);

        _scenePhase = "editor-close";
        var close = Seconds();
        SceneUiDescendants(panel).OfType<Button>().Single(item => item.Name == "ExitEditor").EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(() => editor.Leaving || !editor.IsOpen);
        if (editor.Leaving)
            SceneUiDescendants(panel).OfType<Button>().Single(item => item.Name == "DiscardAndLeave").EmitSignal(BaseButton.SignalName.Pressed);
        var closedDraw = _sceneDrawCount;
        await Wait(() => !editor.IsOpen && !panel.Visible && _sceneDrawCount > closedDraw);
        _sceneOperationTrace.WriteLine(JsonSerializer.Serialize(new
            { kind = "editor-close", click = close, map_drawn = _sceneLastDraw, seconds = _sceneLastDraw - close }, SceneJson));

        _scenePhase = "after-editor-map";
        var afterStarted = _sceneLastDraw;
        await Wait(() => _sceneLastDraw >= afterStarted + 5);
        _sceneEvidence["after_editor_map"] = SceneSteady();
        CollectSceneHeap(1, "after-editor-map");
    }

    // 与 #365 的 TitanEditingTests 相同的序列：泰坦内部一块空长方体整块加入 5,000 格（需确认），
    // 再 49 次单格删除凑满 50 步历史，然后连续撤销 50 步、重做 50 步。每步等新修订实际绘制后才做下一步。
    private async Task RunEditorSequence(EditorService editor, DesignEditorPanel panel)
    {
        var evaluation = editor.State!.CurrentEvaluation as DesignEvaluation.Complete
            ?? throw new InvalidDataException("泰坦样机没有完整评估，无法确定区域。");
        var original = editor.State.Snapshot.Draft;
        var region = EmptyBox(evaluation.Scene);
        var cube = new VoxelPaletteEntry(new("core:structural-metal"), VoxelOrientation.PxPy, null, null);
        var sequenceStarted = Seconds();

        IEditPreview Preview(EditOperation operation, out double seconds)
        {
            var started = Seconds();
            var result = editor.Preview(operation);
            seconds = Seconds() - started;
            return result is SceneResult<IEditPreview>.Success success ? success.Value
                : throw new InvalidDataException("编辑预览失败：" + (result?.ToString() ?? editor.Message));
        }

        async Task Step(string action, int index, Action call, double? previewSeconds = null)
        {
            _scenePhase = "editor-" + action;
            var before = editor.State!.Snapshot.Stamp;
            var started = Seconds();
            call();
            var called = Seconds();
            var after = editor.State!.Snapshot.Stamp;
            if (after == before) throw new InvalidDataException($"编辑序列第 {index} 步（{action}）没有产生新修订：{editor.Message}");
            long drawn = 0;
            await Wait(() =>
            {
                if (drawn == 0 && panel.Scene.DisplayedStamp == after) drawn = _sceneDrawCount;
                return drawn > 0 && _sceneDrawCount > drawn;
            });
            _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new
            {
                kind = "editor-step", index, action, started, displayed = _sceneLastDraw, preview_seconds = previewSeconds,
                call_seconds = called - started, call_to_displayed_seconds = _sceneLastDraw - started,
                revision = after.Revision.Value, cells = editor.State.Snapshot.Draft.Layout.Cells.Length,
                private_bytes = _scenePrivate, working_set_bytes = _sceneWorking
            }, SceneJson));
        }

        var box = Preview(new EditOperation.Cells(new EditFootprint.Box(region), new CellEdit.Add(cube)), out var boxSeconds);
        if (box is not { CoveredCells: 5000, AffectedCells: 5000, CanApply: true, RequiresConfirmation: true })
            throw new InvalidDataException($"5,000 格区域编辑预览不符：覆盖 {box.CoveredCells}，改动 {box.AffectedCells}，冲突 {box.Conflicts.Length}。");
        await Step("edit", 1, () => editor.Apply(box, true), boxSeconds);
        if (editor.State!.Snapshot.Draft.Layout.Cells.Length != original.Layout.Cells.Length + 5000)
            throw new InvalidDataException("区域编辑没有加入 5,000 格。");
        var index = 1;
        foreach (var at in Cells(region).Take(49))
        {
            var remove = Preview(new EditOperation.Cells(new EditFootprint.Cell(at), new CellEdit.Remove()), out var removeSeconds);
            await Step("edit", ++index, () => editor.Apply(remove, false), removeSeconds);
        }
        var final = editor.State!.Snapshot.Draft;
        for (var step = 1; step <= 50; step++) await Step("undo", step, editor.Undo);
        if (editor.State!.CanUndo || !editor.State.Snapshot.Draft.Layout.Cells.SequenceEqual(original.Layout.Cells))
            throw new InvalidDataException("50 步撤销后没有回到原始泰坦。");
        for (var step = 1; step <= 50; step++) await Step("redo", step, editor.Redo);
        if (editor.State!.CanRedo || !editor.State.Snapshot.Draft.Layout.Cells.SequenceEqual(final.Layout.Cells))
            throw new InvalidDataException("50 步重做后没有回到编辑后的设计。");
        _sceneEvidence["editor_sequence"] = new
        {
            source = "Tests/Engine/TitanEditingTests.cs (#365)",
            region = new { minimum = new[] { region.Minimum.X, region.Minimum.Y, region.Minimum.Z },
                maximum = new[] { region.Maximum.X, region.Maximum.Y, region.Maximum.Z } },
            original_cells = original.Layout.Cells.Length, edited_cells = final.Layout.Cells.Length,
            steps = 150, seconds = Seconds() - sequenceStarted, after = SceneSteady()
        };
    }

    // 在泰坦占用范围内找一块体积正好 5,000 格、完全空着的长方体，与 TitanEditingTests 的选法相同。
    private static GridBounds EmptyBox(DesignSceneProjection scene)
    {
        var bounds = scene.OccupiedBounds!;
        var (nx, ny, nz) = (bounds.Maximum.X - bounds.Minimum.X + 1, bounds.Maximum.Y - bounds.Minimum.Y + 1,
            bounds.Maximum.Z - bounds.Minimum.Z + 1);
        var sum = new int[nx + 1, ny + 1, nz + 1];
        foreach (var voxel in scene.Voxels)
            sum[voxel.At.X - bounds.Minimum.X + 1, voxel.At.Y - bounds.Minimum.Y + 1, voxel.At.Z - bounds.Minimum.Z + 1] = 1;
        for (var x = 1; x <= nx; x++)
        for (var y = 1; y <= ny; y++)
        for (var z = 1; z <= nz; z++)
            sum[x, y, z] += sum[x - 1, y, z] + sum[x, y - 1, z] + sum[x, y, z - 1] - sum[x - 1, y - 1, z]
                - sum[x - 1, y, z - 1] - sum[x, y - 1, z - 1] + sum[x - 1, y - 1, z - 1];
        for (var a = 1; a <= nx; a++)
        for (var b = 1; b <= ny; b++)
        {
            if (5000 % (a * b) != 0 || 5000 / (a * b) is var c && c > nz) continue;
            for (var x = 0; x + a <= nx; x++)
            for (var y = 0; y + b <= ny; y++)
            for (var z = 0; z + c <= nz; z++)
                if (sum[x + a, y + b, z + c] - sum[x, y + b, z + c] - sum[x + a, y, z + c] - sum[x + a, y + b, z]
                    + sum[x, y, z + c] + sum[x, y + b, z] + sum[x + a, y, z] - sum[x, y, z] == 0)
                    return new(new(bounds.Minimum.X + x, bounds.Minimum.Y + y, bounds.Minimum.Z + z),
                        new(bounds.Minimum.X + x + a - 1, bounds.Minimum.Y + y + b - 1, bounds.Minimum.Z + z + c - 1));
        }
        throw new InvalidDataException("泰坦范围内没有体积 5,000 格的空长方体。");
    }

    private static IEnumerable<GridPosition> Cells(GridBounds box)
    {
        for (var x = box.Minimum.X; x <= box.Maximum.X; x++)
        for (var y = box.Minimum.Y; y <= box.Maximum.Y; y++)
        for (var z = box.Minimum.Z; z <= box.Maximum.Z; z++)
            yield return new(x, y, z);
    }
}
