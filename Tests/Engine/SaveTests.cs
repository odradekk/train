using System.Diagnostics;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class SaveTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Save_and_save_as_buttons_publish_files_and_load_requires_missing_confirmation()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup();
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            await fixture.AddToRoot(app);
            await FramesUntil(() => app.Saves!.Status is not null);
            var saves = app.Saves!;
            owned.Add(saves.Status!.Identity.Id);
            var ownerThread = System.Environment.CurrentManagedThreadId;
            var menu = app.GetNode<MainMenu>("MainMenu");
            var entry = menu.GetNode<Button>("%Saves");
            Require(entry.Visible && !entry.Disabled, "新宿主的存档入口未显示。");
            entry.EmitSignal(BaseButton.SignalName.Pressed);
            var panel = menu.GetChildren().OfType<SavePanel>().Single();
            Require(panel.Visible, "存档入口未打开生产面板。");
            Press(panel, "保存");
            await FramesUntil(() => !saves.HasPendingWork);
            var first = saves.Status!;
            Require(!first.Unsaved && File.Exists(new FileSaveStore(OS.GetUserDataDir()).PathFor(first.Identity.Id)),
                "保存按钮未发布真实文件并确认已保存状态。");
            Require(setup.Session!.CaptureCount == 1 && setup.Content!.CaptureCount == 1 &&
                setup.Session.CaptureThread == ownerThread && setup.Content.CaptureThread == ownerThread,
                "核心与内容没有在 Godot 主线程共同捕获。");
            Press(panel, "另存为");
            var nameDialog = Dialog<AcceptDialog>(panel, "另存为");
            var name = "engine-save-" + Guid.NewGuid().ToString("N");
            nameDialog.GetChildren().OfType<LineEdit>().Single().Text = name;
            nameDialog.Hide();
            nameDialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await FramesUntil(() => !saves.HasPendingWork);
            var second = saves.Status!;
            owned.Add(second.Identity.Id);
            Require(second.Identity.Id != first.Identity.Id && second.Identity.Name == name && !second.Unsaved,
                "另存为没有切换为新身份或没有确认保存版本。");
            var store = new FileSaveStore(OS.GetUserDataDir());
            Require(store.ReadHeader(store.PathFor(first.Identity.Id)).Id == first.Identity.Id &&
                store.ReadHeader(store.PathFor(second.Identity.Id)).Name == name,
                "另存为覆盖了原文件或新文件头部不正确。");
            var list = panel.FindChildren("*", "ItemList", true, false).Cast<ItemList>()
                .Single(candidate => candidate.GetWindow() == panel);
            var row = Enumerable.Range(0, list.ItemCount).Single(index => list.GetItemText(index).StartsWith(name + " ·", StringComparison.Ordinal));
            list.Select(row);
            setup.MissingOnRestore = true;
            var previousSession = setup.Session;
            var previousBinding = second.Binding;
            Press(panel, "读取选中存档");
            await FramesUntil(() => saves.Missing is not null);
            Require(saves.Status!.Binding == previousBinding && previousSession!.DisposeCount == 0,
                "确认缺失内容前替换了活动会话。");
            await FramesUntil(() => Dialog<ConfirmationDialog>(panel, "缺失内容").Visible);
            var missing = Dialog<ConfirmationDialog>(panel, "缺失内容");
            Require(missing.DialogText.Contains("fixture:missing", StringComparison.Ordinal), "缺失弹窗未显示固定引用。");
            missing.Hide();
            missing.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await FramesUntil(() => !saves.HasPendingWork);
            Require(saves.Status!.Identity == second.Identity && saves.Status.Binding != previousBinding &&
                previousSession.DisposeCount == 1 && !saves.Status.Unsaved,
                "缺失确认后未完成读档替换或未保留存档身份。");
            saves.ReturnToMenu();
            await FramesUntil(() => panel.FindChildren("*", "Button", true, false).Cast<Button>()
                .Where(button => button.GetWindow() == panel && button.Text is "导出文件" or "返回菜单")
                .All(button => !button.IsVisibleInTree()));
            Require(saves.Status is null, "返回菜单后仍保留活动会话。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    [Test]
    public async Task Window_close_and_console_quit_share_protection_and_quit_only_after_publication()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup();
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            var quitRequests = 0;
            var publishedAtQuit = false;
            app.QuitAction = () =>
            {
                quitRequests++;
                var status = app.Saves!.Status!;
                var store = new FileSaveStore(OS.GetUserDataDir());
                publishedAtQuit = !status.Unsaved && File.Exists(store.PathFor(status.Identity.Id)) &&
                    store.ReadHeader(store.PathFor(status.Identity.Id)).Id == status.Identity.Id;
            };
            await fixture.AddToRoot(app);
            await FramesUntil(() => app.Saves!.Status is not null);
            var saves = app.Saves!;
            owned.Add(saves.Status!.Identity.Id);
            var panel = app.GetNode<MainMenu>("MainMenu").GetChildren().OfType<SavePanel>().Single();
            app.Notification((int)Node.NotificationWMCloseRequest);
            await FramesUntil(() => Dialog<ConfirmationDialog>(panel, "未保存的更改").Visible);
            Require(quitRequests == 0 && saves.Leaving is { CanSave: true, Waiting: false }, "窗口关闭绕过了未保存保护。");
            var leave = Dialog<ConfirmationDialog>(panel, "未保存的更改");
            leave.Hide();
            leave.EmitSignal(AcceptDialog.SignalName.Canceled);
            Require(saves.Leaving is null && quitRequests == 0 && !app.Host!.GetStatus()!.OperationLocked,
                "取消关闭没有恢复宿主或仍请求退出。");
            app.GetChildren().OfType<GameConsole>().Single().SubmitLine("quit");
            await FramesUntil(() => leave.Visible);
            Require(quitRequests == 0 && saves.Leaving is { CanSave: true, Waiting: false }, "控制台退出没有使用同一保存保护。");
            leave.Hide();
            leave.EmitSignal(AcceptDialog.SignalName.Confirmed);
            Require(quitRequests == 0 && saves.HasPendingWork, "退出没有等待异步文件发布。");
            await FramesUntil(() => quitRequests == 1);
            Require(publishedAtQuit && !saves.HasPendingWork && saves.Leaving is null,
                "保存成功前执行了退出动作或文件不可读取。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }

    private async Task FramesUntil(Func<bool> completed)
    {
        var guard = Stopwatch.StartNew();
        while (!completed() && guard.Elapsed < TimeSpan.FromSeconds(30))
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(completed(), "挂起保护超时：真实 Godot 帧未接入存档结果。");
    }

    private static T Dialog<T>(SavePanel panel, string title) where T : Window =>
        panel.GetChildren().OfType<T>().Single(dialog => dialog.Title == title);

    private static void Press(SavePanel panel, string text)
    {
        var button = panel.FindChildren("*", "Button", true, false).Cast<Button>()
            .Single(candidate => candidate.Text == text && candidate.GetWindow() == panel);
        Require(!button.Disabled && button.Visible, $"按钮不可用：{text}。");
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private async Task Cleanup(Fixture fixture, Train.App.App? app, HashSet<Guid> owned)
    {
        try
        {
            if (app?.Saves is { } saves)
            {
                if (saves.Status is { } status) owned.Add(status.Identity.Id);
                saves.Cancel();
                await FramesUntil(() => !saves.HasPendingWork);
            }
        }
        finally
        {
            try { await fixture.Cleanup(); }
            finally
            {
                var store = new FileSaveStore(OS.GetUserDataDir());
                foreach (var id in owned)
                {
                    var directory = Path.GetFullPath(store.DirectoryFor(id));
                    Require(Path.GetDirectoryName(directory) == Path.Combine(store.Root, FileSaveStore.SavesFolder),
                        "测试清理路径不在本次存档目录内。");
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
            }
        }
    }

    private sealed class SaveSetup
    {
        internal ScriptedCoreSession? Session { get; private set; }
        internal ScriptedContentFactory? Content { get; private set; }
        internal bool MissingOnRestore { get; set; }

        internal HostCoordinator Create(TrainLogger logger)
        {
            var capture = new FixedCoreCapture(new(new(0, 0), "engine save", "engine sha"),
                HostCoreScenario.References, "engine core", HostCoreScenario.Input, new(0));
            var core = new ScriptedCoreFactory
            {
                DecodeStateCall = payload => payload == "engine core"
                    ? new CoreResult<ICoreCapture>.Success(capture) : throw new InvalidOperationException("UNCONFIGURED: core payload"),
                PrepareRestoreCall = (_, projection, _) =>
                {
                    Require(projection.Identity == HostCoreScenario.Input, "恢复时收到错误内容投影。");
                    Session = new(new FixedCoreReadView(new(0, 0), projection.Identity, revision: new(0)));
                    Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(capture));
                    return new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(Session, HostCoreScenario.References));
                }
            };
            Content = new()
            {
                CaptureCall = (view, _) => new ContentResult<IContentCapture>.Success(new FixedContentCapture("engine content", view.PersistentRevision)),
                DecodeCaptureCall = payload => payload == "engine content"
                    ? new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload)) : throw new InvalidOperationException("UNCONFIGURED: content payload"),
                PrepareLoadCall = (request, _, _) =>
                {
                    Require(request.Purpose is ContentLoadPurpose.Restore, "存档引擎情景只允许恢复。");
                    var view = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1));
                    var report = new ContentPreparationReport([], MissingOnRestore
                        ? [new(new ContentReference.Generation(new("fixture:missing")), null, "runtime", [])] : [], [], []);
                    return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view, report: report));
                }
            };
            var host = new HostCoordinator(core, Content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "engine-save", []), logger);
            var options = new CoreSessionOptions(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1);
            Require(host.BeginRestore("engine core", "engine content", options).Problem is null, "无法开始初始恢复。");
            return host;
        }
    }
}
