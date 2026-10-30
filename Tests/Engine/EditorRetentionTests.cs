using System.Runtime.CompilerServices;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Saves;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class EditorRetentionTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Closing_editor_releases_presented_design_before_the_next_session()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy, timeout: ContentPreparationTimeout);
            await LoadStandardWorld(app);
            var oldContent = ContentReference(app);
            OpenFirstVehicle(app);
            var panel = app.GetNode<DesignEditorPanel>("GameUi/Root/DesignEditor");
            await FramesUntil(TestScene, () => panel.Scene.Display?.DisplayedFrame is { Readiness: SceneReadiness.Ready });
            var held = ClosePresentedEditor(app, panel);
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(!app.Editor!.IsOpen && panel.Scene.Display is null && !panel.Scene.View.Visible,
                "关闭编辑器后显示控制器未清理。");
            Collect();
            Require(!held.Frame.IsAlive, "关闭编辑器后仍持有已显示的 CPU 帧。");
            Require(!held.Evaluation.IsAlive, "隐藏的编辑器面板仍持有已显示的设计求值结果。");
            await LoadStandardWorld(app);
            Collect();
            Require(!oldContent.IsAlive, "重新读档后关闭的编辑器仍持有前一局内容视图。");
            OpenFirstVehicle(app);
            await FramesUntil(TestScene, () => panel.Scene.Display?.DisplayedFrame is { Readiness: SceneReadiness.Ready });
        }
        finally { await fixture.Cleanup(); }
    }

    private async Task LoadStandardWorld(Train.App.App app)
    {
        var path = ProjectSettings.GlobalizePath("res://examples/standard-world/save.json");
        var store = new FileSaveStore(OS.GetUserDataDir());
        var before = app.Host!.GetStatus()?.Binding;
        Require(app.Saves!.Load(new(path, store.ReadHeader(path), false)) is null, "无法读取标准存档。");
        if (app.Saves.Leaving is { Waiting: false }) app.Saves.ChooseLeave(LeaveChoice.Discard);
        await FramesUntil(TestScene, () => app.Host.GetStatus() is { } status && status.Binding != before &&
            !app.Saves.IsBusy && !app.Recording!.IsBusy, timeout: ContentPreparationTimeout);
        Require(app.Host.SetPaused(true) is null, "读档后无法暂停。");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ContentReference(Train.App.App app) => new(app.Host!.Content);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OpenFirstVehicle(Train.App.App app)
    {
        var train = ((Lookup<TrainView>.Found)app.Host!.GetStatus()!.View.FindTrain(new(1))).Value;
        app.Editor!.OpenInstance(new InstanceId.Vehicle(train.Vehicles[0]));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Frame, WeakReference Evaluation) ClosePresentedEditor(Train.App.App app, DesignEditorPanel panel)
    {
        var frame = new WeakReference(panel.Scene.Display!.DisplayedFrame);
        var evaluation = new WeakReference(app.Editor!.State!.CurrentEvaluation);
        var closed = false;
        Require(app.Editor.RequestClose(() => closed = true) is null && closed, "未修改的编辑器未能关闭。");
        return (frame, evaluation);
    }

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
    }
}
