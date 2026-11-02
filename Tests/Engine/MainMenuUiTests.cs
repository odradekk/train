using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class MainMenuUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Loading_blocks_start_then_focuses_new_game_without_saves()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = new Fixture(TestScene.GetTree());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var content = new ScriptedContentFactory
        {
            PrepareLoadCall = (_, _, _) =>
            {
                entered.Set();
                Require(release.Wait(TimeSpan.FromSeconds(20)), "测试没有释放内容加载。");
                return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(ContentView()));
            }
        };
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new(new ScriptedCoreFactory(), content, new ScriptedContentFiles(),
                new UnconfiguredAssetInspector(), new(new("builtin"), new("mods"), null, "fixture", []), logger,
                sessionOptions: HostOptions());
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => entered.IsSet);
            var menu = app.GetNode<MainMenu>("MainMenu");
            Require(menu.HasNode("%Continue"), "主菜单缺少继续游戏入口。");
            var start = menu.GetNode<Button>("%NewGame");
            var resume = menu.GetNode<Button>("%Continue");
            var progress = menu.GetNode<ProgressBar>("%LoadingProgress");
            Require(start.Disabled && resume.Disabled && progress.IsVisibleInTree() && progress.Indeterminate &&
                menu.GetNode<Label>("%LoadingStatus").Text.Contains("加载完成后"),
                "启动内容加载时必须阻止开始并说明原因、显示不确定进度。");
            await MenuCapture.Capture(TestScene, "menu-loading");
            Require(menu.GetNode<Label>("%VersionPackages").Text.Contains(
                ProjectSettings.GetSetting("application/config/version").AsString()), "内容尚未加载时也应显示版本。");
            release.Set();
            await _input.WaitUntil(() => !app.Host!.IsBusy && start.HasFocus());
            Require(resume.Disabled && menu.GetNode<Label>("%RecentSave").Text == "暂无存档" &&
                !progress.IsVisibleInTree(), "无存档时应禁用继续并聚焦新游戏。");
            await MenuCapture.Capture(TestScene, "menu-empty");
            var names = new[] { "%Continue", "%NewGame", "%Saves", "%Settings", "%QuitGame" };
            var buttons = names.Select(name => menu.GetNode<Button>(name)).ToArray();
            Require(buttons.Select(button => button.Text).SequenceEqual(new[]
                { "继续游戏", "新游戏", "读取存档", "设置", "退出游戏" }) &&
                buttons.Zip(buttons.Skip(1)).All(pair => pair.First.GlobalPosition.Y < pair.Second.GlobalPosition.Y),
                "主菜单操作顺序或文字不符合要求。");
            var footer = menu.GetNode<Label>("%VersionPackages").Text;
            Require(footer.Contains(ProjectSettings.GetSetting("application/config/version").AsString()) &&
                footer.Contains("测试内容 1.2"), "菜单底部没有显示游戏版本和可用包的名称与版本。");
            Require(menu.GetNode<Button>("%Example").Visible == OS.IsDebugBuild() &&
                menu.GetNode<Button>("%Example").Text == "从示例存档开始", "示例入口必须只提供给开发构建。");
        }
        finally { release.Set(); await fixture.Cleanup(); }
    }

    [Test]
    public async Task Enter_continues_latest_save_and_returning_to_menu_restores_focus()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = new Fixture(TestScene.GetTree());
        var store = new FileSaveStore(OS.GetUserDataDir());
        var older = Guid.NewGuid();
        var latest = Guid.NewGuid();
        var now = DateTimeOffset.Now;
        var view = new FixedCoreReadView(new(216000, 0), HostCoreScenario.Input)
        { Economy = UiWorldFixture.Economy, World = new(42, new("fixture:railway"), 100, 100, [], [], [], [], []) };
        var capture = new FixedCoreCapture(new(view.Position, "menu", "menu-checkpoint"),
            HostCoreScenario.References, "menu core", HostCoreScenario.Input, new(0));
        var core = new ScriptedCoreFactory
        {
            DecodeStateCall = payload => payload == "menu core"
                ? new CoreResult<ICoreCapture>.Success(capture) : throw new InvalidOperationException("错误存档载荷。"),
            PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(
                new ScriptedCoreCandidate(UiWorldFixture.Session(view), HostCoreScenario.References))
        };
        var content = new ScriptedContentFactory
        {
            PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(ContentView())),
            DecodeCaptureCall = payload => new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload)),
            CaptureReplayCall = (history, _) => new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "menu recording"))
        };
        Train.App.App? app = null;
        try
        {
            Require(store.Write(store.PathFor(older), new(new(SaveContainer.FormatVersion, older, "旧存档",
                now.AddDays(-1), new(0, 0), []), "menu core", "menu content"), false).Published, "无法准备旧存档。");
            Require(store.Write(store.AutoPathFor(latest, 0), new(new(SaveContainer.FormatVersion, latest,
                string.Concat(Enumerable.Repeat("山间列车沿着长长的线路前往遥远聚居地", 4)),
                now, new(216000, 0), []), "menu core", "menu content"), false).Published, "无法准备最近的自动存档。");
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new(core, content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "fixture", []), logger, sessionOptions: HostOptions());
            await fixture.AddToRoot(app);
            var menu = app.GetNode<MainMenu>("MainMenu");
            Require(menu.HasNode("%Continue"), "主菜单缺少继续游戏入口。");
            var resume = menu.GetNode<Button>("%Continue");
            await _input.WaitUntil(() => !app.Host!.IsBusy && resume.HasFocus());
            var summary = menu.GetNode<Label>("%RecentSave").Text;
            Require(summary.Contains("山间列车") && summary.Contains("第1天 01:00") &&
                summary.Contains("今天 " + now.ToString("HH:mm")) && !summary.Contains(latest.ToString()),
                "继续游戏必须显示最近存档的名称、游戏时间和本地保存时间，不能显示身份编号。");
            await MenuCapture.Capture(TestScene, "menu-continue");
            await _input.PressKey(Key.Enter);
            await _input.WaitUntil(() => app.Saves!.Status?.Identity.Id == latest && !app.Saves.IsBusy);
            await _input.WaitUntil(() => !menu.IsVisibleInTree());
            Require(app.Host!.GetStatus()!.PlayerPaused && app.Saves!.Status!.Unsaved,
                "回车必须恢复最近的自动存档并保留暂停与未保存状态。");
            Require(app.Saves!.ReturnToMenu() is null, "无法请求返回菜单。");
            app.Saves.ChooseLeave(LeaveChoice.Discard);
            await _input.WaitUntil(() => app.Host.GetStatus() is null && menu.IsVisibleInTree() && resume.HasFocus());
        }
        finally
        {
            app?.Saves?.Cancel();
            await fixture.Cleanup();
            UiTestInput.DeleteSaves([older, latest]);
        }
    }

    private static CoreSessionOptions HostOptions() => new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1);

    [Test]
    public async Task Save_time_uses_local_today_month_day_and_year()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var store = new FileSaveStore(OS.GetUserDataDir());
        var id = Guid.NewGuid();
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            var content = new ScriptedContentFactory
            { PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(ContentView())) };
            app.CreateHost = logger => new(new ScriptedCoreFactory(), content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "fixture", []), logger, sessionOptions: HostOptions());
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => !app.Host!.IsBusy && app.Host.Content is not null);
            var menu = app.GetNode<MainMenu>("MainMenu");
            var today = DateTime.Today;
            var day = today.Month == 1 && today.Day == 1 ? 2 : 1;
            var cases = new[]
            {
                (Local: today.AddHours(9).AddMinutes(7), Text: "今天 09:07"),
                (Local: new DateTime(today.Year, 1, day, 9, 7, 0), Text: $"1 月 {day} 日 09:07"),
                (Local: new DateTime(today.Year - 1, 7, 8, 9, 7, 0), Text: $"{today.Year - 1} 年 7 月 8 日 09:07")
            };
            foreach (var sample in cases)
            {
                menu.Hide(); await _input.Frames(2);
                Require(store.Write(store.PathFor(id), new(new(SaveContainer.FormatVersion, id, "时间测试",
                    new DateTimeOffset(sample.Local).ToUniversalTime(), new(0, 0), []), "core", "content"), true).Published,
                    "无法准备存档时间样本。");
                menu.Show(); await _input.Frames(2);
                Require(menu.GetNode<Label>("%RecentSave").Text.Split('\n')[^1] == sample.Text + "保存",
                    $"存档时间没有按本地时间显示为 {sample.Text}。");
            }
        }
        finally { await fixture.Cleanup(); UiTestInput.DeleteSaves([id]); }
    }

    [Test]
    public async Task Failed_startup_stops_progress_and_explains_why_start_is_disabled()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = new Fixture(TestScene.GetTree());
        var content = new ScriptedContentFactory
        {
            PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Failure(
                [new("invalid_json", ContentDiagnosticSeverity.Error, "loading", null, null, "/internal/path", null,
                    "Invalid GenerationProfile JSON at internal/file.json")])
        };
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new(new ScriptedCoreFactory(), content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "fixture", []), logger, sessionOptions: HostOptions());
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => !app.Host!.IsBusy);
            await _input.Frames(2);
            var menu = app.GetNode<MainMenu>("MainMenu");
            Require(menu.GetNode<Button>("%NewGame").Disabled && menu.GetNode<Button>("%Continue").Disabled &&
                !menu.GetNode<ProgressBar>("%LoadingProgress").IsVisibleInTree() &&
                menu.GetNode<Label>("%LoadingStatus").Text.Contains("未能加载"),
                "加载失败后不能继续显示加载中的进度，必须说明不能开始的原因。");
            Require(app.GetNode<GameUi>("GameUi").ErrorText == "部分内容无法加载，请检查已安装的内容包。",
                "主菜单加载失败不能显示内部诊断文本。");
            await MenuCapture.Capture(TestScene, "menu-load-failure");
        }
        finally { await fixture.Cleanup(); }
    }

    private static FixedContentView ContentView() => new(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
    {
        GenerationProfiles = [new(new("fixture:world"), new("测试世界", "小型测试世界。"))],
        Packages = [new(new(new("fixture"), "测试内容", "1.2", [], []), new("fixture"), PackageState.Available, [])]
    };
}
