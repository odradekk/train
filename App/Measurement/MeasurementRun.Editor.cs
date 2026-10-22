using System.Text.Json;
using Godot;
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
}
