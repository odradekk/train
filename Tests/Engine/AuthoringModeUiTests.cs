using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class AuthoringModeUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput input = new(testScene);

    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Main_menu_authoring_is_full_screen_without_creating_a_world_and_returns_to_its_opener()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var view = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        {
            GenerationProfiles = [new(new("fixture:world"), new("测试世界", "菜单测试"))],
            Voxels = [], Blueprints = [], LocalDrafts = []
        };
        var content = new ScriptedContentFactory
        {
            PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view))
        };
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new(new ScriptedCoreFactory(), content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "fixture", []), logger,
                sessionOptions: new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
            await fixture.AddToRoot(app);
            await input.WaitUntil(() => !app.Host!.IsBusy);
            var menu = app.GetNode<MainMenu>("MainMenu");
            Require(menu.IsVisibleInTree() && app.Host!.GetStatus() is null, "测试必须从无世界的生产主菜单开始。");
            Require(menu.HasNode("%OpenAuthoring"), "主菜单必须提供创作模式入口。");
            var open = menu.GetNode<Button>("%OpenAuthoring");
            Require(open.Visible == OS.IsDebugBuild() && !open.Disabled, "主菜单创作入口必须在开发构建中可用。");
            var root = app.GetNode<Control>("GameUi/Root");
            var popup = Find<MenuButton>(root, "GameMenu").GetPopup();
            Require(Enumerable.Range(0, popup.ItemCount).Any(index => popup.GetItemText(index) == "创作模式") == OS.IsDebugBuild(),
                "游戏菜单必须在开发构建中提供创作模式条目。");

            await input.Activate(open);
            await input.Frames(2);
            var panel = Find<AuthoringPanel>(root, "Authoring");
            var mode = Find<Control>(panel, "AuthoringMode");
            Require(panel.IsVisibleInTree() && mode.IsVisibleInTree() && !menu.IsVisibleInTree()
                && !root.GetNode<Control>("World").IsVisibleInTree(), "进入创作必须隐藏主菜单和游戏外壳。");
            var expected = root.GetGlobalRect();
            var actual = mode.GetGlobalRect();
            Require(expected.Size.X > 0 && expected.Size.Y > 0 && actual.Position.IsEqualApprox(expected.Position)
                && actual.Size.IsEqualApprox(expected.Size), "创作界面必须覆盖整个 Root，不能保留浮动小窗布局。");
            Require(app.Host!.GetStatus() is null, "从主菜单进入创作不能隐式创建世界。");
            Require(Find<Button>(panel, "ExitAuthoring").HasFocus(), "进入创作必须把键盘焦点放到退出入口。");

            var window = TestScene.GetTree().Root;
            var size = window.Size;
            var scale = window.ContentScaleFactor;
            try
            {
                foreach (var (resolution, factor) in new[] { (new Vector2I(1920, 1080), 1f), (new Vector2I(1280, 720), 1f), (new Vector2I(1920, 1080), 2f) })
                {
                    window.Size = resolution; window.ContentScaleFactor = factor;
                    await input.Frames(3);
                    var tree = Find<Tree>(panel, "DefinitionTree");
                    var drafts = tree.GetParent().GetChildren().OfType<ScrollContainer>().Single();
                    var viewport = panel.GetViewport().GetVisibleRect();
                    Require(mode.GetGlobalRect().End.Y <= viewport.End.Y + 1
                        && tree.GetGlobalRect().End.Y <= viewport.End.Y + 1
                        && drafts.GetGlobalRect().End.Y <= viewport.End.Y + 1,
                        $"{resolution}、缩放 {factor} 时，定义树和旧草稿列表必须完整处于屏幕内。");
                }
            }
            finally
            {
                window.ContentScaleFactor = scale; window.Size = size;
                await input.Frames(3);
            }

            await input.Activate(Find<Button>(panel, "ExitAuthoring"));
            await input.WaitUntil(() => menu.IsVisibleInTree() && !panel.IsVisibleInTree());
            Require(!root.GetNode<Control>("World").IsVisibleInTree() && app.Host.GetStatus() is null,
                "退出菜单创作后必须回到主菜单，不能进入游戏外壳。");
            Require(open.HasFocus(), "退出创作必须恢复主菜单创作入口的焦点。");
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task In_game_menu_authoring_hides_world_and_restores_pause_speed_and_focus_on_exit()
    {
        var fixture = new Fixture(TestScene.GetTree());
        try
        {
            var app = await fixture.LoadAndAddScene<Train.App.App>("res://App/App.tscn");
            await FramesUntil(TestScene, () => !app.Host!.IsBusy, timeout: ContentPreparationTimeout);
            var host = app.Host!;
            Require(host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!).Problem is null,
                "真实新局请求被拒绝。");
            await FramesUntil(TestScene, () => !host.IsBusy && host.GetStatus() is not null, timeout: ContentPreparationTimeout);
            app.Saves!.SetAutoSaveInterval(null); host.AutoPauseOnFocusLoss = false;
            var ui = app.GetNode<GameUi>("GameUi");
            var menu = app.GetNode<MainMenu>("MainMenu");
            var world = ui.GetNode<Control>("Root/World");
            var gameMenu = Find<MenuButton>(world, "GameMenu");
            var panel = Find<AuthoringPanel>(ui, "Authoring");
            foreach (var playerPaused in new[] { false, true })
            {
                Require(host.SetSpeed(SimulationSpeed.Four) is null && host.SetPaused(playerPaused) is null,
                    "设置真实对局的玩家暂停与倍速失败。");
                menu.FocusBehaviorRecursive = playerPaused ? Control.FocusBehaviorRecursiveEnum.Inherited : Control.FocusBehaviorRecursiveEnum.Enabled;
                world.FocusBehaviorRecursive = playerPaused ? Control.FocusBehaviorRecursiveEnum.Enabled : Control.FocusBehaviorRecursiveEnum.Inherited;
                var menuFocus = menu.FocusBehaviorRecursive;
                var worldFocus = world.FocusBehaviorRecursive;
                await input.Frames(2);
                Require(world.IsVisibleInTree() && !host.GetStatus()!.OverlayPaused, "测试必须从可交互、没有覆盖层暂停的真实对局开始。");

                await input.ChooseMenu(gameMenu, "创作模式");
                await input.WaitUntil(() => panel.IsVisibleInTree() && !world.IsVisibleInTree());
                var entered = host.GetStatus()!;
                Require(entered.OverlayPaused && entered.PlayerPaused == playerPaused && entered.Speed == SimulationSpeed.Four,
                    "创作模式必须独立暂停世界，保留玩家原来的暂停和倍速设置。");
                Require(menu.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Disabled
                    && world.FocusBehaviorRecursive == Control.FocusBehaviorRecursiveEnum.Disabled,
                    "创作期间必须禁止菜单与世界外壳接收键盘焦点。");
                Require(Find<Button>(panel, "ExitAuthoring").HasFocus(), "从游戏菜单进入创作后必须有默认键盘焦点。");
                host.ProcessFrame(1);
                Require(host.GetStatus()!.View.Position == entered.View.Position, "覆盖层暂停期间真实核心不能推进。");

                await input.PressKey(Key.Space);
                await input.WaitUntil(() => !panel.IsVisibleInTree() && world.IsVisibleInTree());
                var exited = host.GetStatus()!;
                Require(!menu.IsVisibleInTree() && !exited.OverlayPaused && exited.PlayerPaused == playerPaused
                    && exited.Speed == SimulationSpeed.Four && exited.Binding == entered.Binding,
                    "退出创作必须回到原对局，解除覆盖层暂停并保留玩家暂停和倍速。");
                Require(menu.FocusBehaviorRecursive == menuFocus && world.FocusBehaviorRecursive == worldFocus && gameMenu.HasFocus(),
                    "退出创作必须精确恢复菜单、世界的焦点设置，并把焦点还给游戏菜单。");
            }
            host.SetPaused(true);
        }
        finally { await fixture.Cleanup(); }
    }

    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Publish_requires_confirmation_and_cancel_keeps_the_package_unchanged()
    {
        using var fixture = new RealAuthoringFixture(TestScene);
        Require(fixture.Host.BeginReload(CommandSource.AutomationTest).Problem is null, "菜单初始加载请求被拒绝。");
        await fixture.Pump(() => !fixture.Host.IsBusy, ContentPreparationTimeout);
        Require(fixture.LastOperation?.Succeeded == true, "真实初始内容加载失败。");
        await fixture.Open();
        fixture.Service.OpenDefinition(new(DefinitionKind.Voxel, "engine-food"), grid: false);
        var panel = new AuthoringPanel { Name = "Authoring" };
        panel.Initialize(fixture.Service, fixture.Editor);
        TestScene.AddChild(panel);
        panel.Open();
        try
        {
            await input.Frames(2);
            var form = Find<AuthoringDefinitionForm>(panel, "DefinitionForm");
            var name = form.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>().Single(field => field.TooltipText == "名称");
            await input.Enter(name, "Confirmed draft");
            await TabTo(Find<Button>(panel, "SaveDraft"), panel);
            await input.PressKey(Key.Space);
            await fixture.Pump(() => !fixture.Service.IsBusy);
            Require(fixture.Service.Drafts().Length == 1, "确认写包前必须通过真实界面保存草稿。");
            var savedDraft = fixture.Service.Drafts().Single();
            var draftFile = Path.Combine(AuthoringPaths.PackageDraftsDirectory(fixture.UserData, fixture.PackageId), savedDraft.Id.Value.ToString("D") + ".json");
            File.SetLastWriteTimeUtc(draftFile, new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            var savedAt = File.GetLastWriteTimeUtc(draftFile);
            var oldDrafts = Find<Tree>(panel, "DefinitionTree").GetParent().GetChildren().OfType<ScrollContainer>().Single();
            Require(!oldDrafts.FindChildren("*", "Button", true, false).OfType<Button>()
                .Any(button => button.Name == "OpenDraft" || button.Name == "OpenDraftOnCurrentSource"),
                "本次已暂存的草稿不能重复出现在以前保存的草稿列表。");
            var definition = Path.Combine(fixture.ModDirectory, "resources", "voxels", "engine-food.voxel.json");
            var originalBytes = File.ReadAllBytes(definition);
            var originalContent = fixture.Host.Content;

            var publish = Find<Button>(panel, "Publish");
            await TabTo(publish, panel);
            await input.PressKey(Key.Space);
            var confirm = Find<Control>(panel, "ConfirmPublish");
            Require(confirm.IsVisibleInTree() && !fixture.Service.IsBusy && fixture.Service.LastWrite is null
                && ReferenceEquals(originalContent, fixture.Host.Content)
                && originalBytes.AsSpan().SequenceEqual(File.ReadAllBytes(definition)),
                "点击写包必须先显示确认；确认前不得开始写入或切换内容。");
            Require(Find<Button>(confirm, "CancelWrite").HasFocus(), "写入确认必须默认聚焦取消按钮。");
            await AssertTabStaysIn(confirm);
            await TabTo(Find<Button>(confirm, "CancelWrite"), confirm);
            await input.PressKey(Key.Space);
            await input.Frames(2);
            Require(!confirm.IsVisibleInTree() && !fixture.Service.IsBusy && fixture.Service.LastWrite is null
                && ReferenceEquals(originalContent, fixture.Host.Content)
                && originalBytes.AsSpan().SequenceEqual(File.ReadAllBytes(definition))
                && fixture.Service.Drafts().Length == 1, "取消确认必须保留原文件、活动内容和已保存草稿。");
            Require(publish.HasFocus(), "取消写入确认必须恢复写入入口的焦点。");

            await input.PressKey(Key.Space);
            Require(Find<Button>(confirm, "CancelWrite").HasFocus(), "再次打开写入确认仍必须默认聚焦取消。");
            await TabTo(Find<Button>(confirm, "ConfirmWrite"), confirm);
            await input.PressKey(Key.Space);
            await fixture.Pump(() => !fixture.Service.IsBusy && !fixture.Host.IsBusy, ContentPreparationTimeout);
            Require(fixture.Service.LastWrite?.Success == true && fixture.LastOperation?.Succeeded == true,
                "明确确认后必须完成真实写包和普通重载：" + fixture.Service.Message);
            Require(fixture.Host.Content!.FindVoxelDisplay(new(fixture.PackageId.Value + ":engine-food"))
                is ContentLookup<VoxelDisplayProjection>.Found { Value.Display.Name: "Confirmed draft" },
                "确认写入后必须能从真实重载内容读出表单修改。");
            Require(fixture.Service.Package is not null && fixture.Service.Drafts().Single().Id == savedDraft.Id,
                "成功写入后必须自动重开源包，并重新显示保留的旧草稿。");
            var oldDraftButton = oldDrafts.FindChildren("*", "Button", true, false).OfType<Button>()
                .Single(button => button.Name == "OpenDraft" || button.Name == "OpenDraftOnCurrentSource");
            var row = oldDraftButton.GetParent();
            var draftName = Find<Label>(row, "SavedDraftName");
            var draftTime = Find<Label>(row, "SavedDraftTime");
            Require(draftName.IsVisibleInTree() && draftName.Text.Contains("engine-food", StringComparison.Ordinal)
                && !draftName.ClipText && draftName.TextOverrunBehavior == TextServer.OverrunBehavior.NoTrimming
                && draftName.AutowrapMode != TextServer.AutowrapMode.Off && !oldDraftButton.ClipText
                && oldDrafts.HorizontalScrollMode == ScrollContainer.ScrollMode.Disabled,
                "以前保存的草稿必须显示完整名称，标签换行、操作按钮不裁切，列表不能依赖横向滚动。");
            Require(draftTime.IsVisibleInTree() && draftTime.Text == savedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                && draftTime.TooltipText == savedAt.ToString("O"),
                "每项旧草稿必须显示真实文件保存时间，并在提示里提供完整 UTC 时间。");
        }
        finally { panel.QueueFree(); await input.Frames(3); }
    }
}
