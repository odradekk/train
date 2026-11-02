using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class AuthoringModeUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput input = new(testScene);

    [Test]
    [Chickensoft.GoDotTest.Timeout(120000)]
    public async Task Main_menu_authoring_replaces_the_world_shell_and_returns_to_the_menu()
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

            await input.Activate(Find<Button>(panel, "ExitAuthoring"));
            await input.WaitUntil(() => menu.IsVisibleInTree() && !panel.IsVisibleInTree());
            Require(!root.GetNode<Control>("World").IsVisibleInTree() && app.Host.GetStatus() is null,
                "退出菜单创作后必须回到主菜单，不能进入游戏外壳。");
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
            await input.Activate(Find<Button>(panel, "SaveDraft"));
            await fixture.Pump(() => !fixture.Service.IsBusy);
            Require(fixture.Service.Drafts().Length == 1, "确认写包前必须通过真实界面保存草稿。");
            var definition = Path.Combine(fixture.ModDirectory, "resources", "voxels", "engine-food.voxel.json");
            var originalBytes = File.ReadAllBytes(definition);
            var originalContent = fixture.Host.Content;

            await input.Activate(Find<Button>(panel, "Publish"));
            var confirm = Find<Control>(panel, "ConfirmPublish");
            Require(confirm.IsVisibleInTree() && !fixture.Service.IsBusy && fixture.Service.LastWrite is null
                && ReferenceEquals(originalContent, fixture.Host.Content)
                && originalBytes.AsSpan().SequenceEqual(File.ReadAllBytes(definition)),
                "点击写包必须先显示确认；确认前不得开始写入或切换内容。");
            await input.Activate(Find<Button>(confirm, "CancelWrite"));
            await input.Frames(2);
            Require(!confirm.IsVisibleInTree() && !fixture.Service.IsBusy && fixture.Service.LastWrite is null
                && ReferenceEquals(originalContent, fixture.Host.Content)
                && originalBytes.AsSpan().SequenceEqual(File.ReadAllBytes(definition))
                && fixture.Service.Drafts().Length == 1, "取消确认必须保留原文件、活动内容和已保存草稿。");

            await input.Activate(Find<Button>(panel, "Publish"));
            await input.Activate(Find<Button>(confirm, "ConfirmWrite"));
            await fixture.Pump(() => !fixture.Service.IsBusy && !fixture.Host.IsBusy, ContentPreparationTimeout);
            Require(fixture.Service.LastWrite?.Success == true && fixture.LastOperation?.Succeeded == true,
                "明确确认后必须完成真实写包和普通重载：" + fixture.Service.Message);
            Require(fixture.Host.Content!.FindVoxelDisplay(new(fixture.PackageId.Value + ":engine-food"))
                is ContentLookup<VoxelDisplayProjection>.Found { Value.Display.Name: "Confirmed draft" },
                "确认写入后必须能从真实重载内容读出表单修改。");
        }
        finally { panel.QueueFree(); await input.Frames(3); }
    }
}
