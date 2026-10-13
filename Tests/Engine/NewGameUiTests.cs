using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class NewGameUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    // GoDotTest 2.0.47 根据异步状态机标记判断是否等待，直接返回 Task 会被当作同步测试。
    [Test]
    public async Task Menu_inputs_create_a_paused_world() => await CreateThroughInput(false);

    [Test]
    public async Task New_game_rejection_keeps_the_menu_and_shows_the_problem() => await CreateThroughInput(true);

    [Test]
    public async Task Missing_new_game_references_are_visible_and_can_be_cancelled() => await CreateThroughInput(false, true);

    [Test]
    public async Task Missing_new_game_confirmation_activates_the_candidate_and_opens_a_paused_map()
        => await CreateThroughInput(false, true, confirm: true);

    [Test]
    public async Task New_game_without_session_options_is_disabled()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var core = new ScriptedCoreFactory();
        var view = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        { GenerationProfiles = [new(new("fixture:world"), new("固定世界", ""))] };
        var content = new ScriptedContentFactory
        { PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view)) };
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new HostCoordinator(core, content, new ScriptedContentFiles(),
                new UnconfiguredAssetInspector(), new(new("builtin"), new("mods"), null, "fixture", []), logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.Content is not null && !app.Host.IsBusy);
            await _input.Frames(2);
            var button = app.GetNode<MainMenu>("MainMenu").GetNode<Button>("%NewGame");
            Require(button.Visible && button.Disabled, "缺少宿主会话选项时，新游戏入口仍可操作。");
            await _input.Click(button.GetGlobalRect().GetCenter());
            Require(core.PrepareNewCount == 0 && app.GetNodeOrNull<Control>("GameUi/Root/NewGame") is null,
                "停用的新游戏入口仍准备了候选或打开了表单。");
        }
        finally { await fixture.Cleanup(); }
    }

    private async Task CreateThroughInput(bool reject, bool missing = false, bool confirm = false)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        var projection = HostContentScenario.CreateProjection(HostCoreScenario.Input);
        var contentView = new FixedContentView(projection, new(1))
        {
            GenerationProfiles = [new(new("fixture:first"), new("首个配置", "")),
                new(new("fixture:second"), new("第二配置", ""))]
        };
        var view = new FixedCoreReadView(default, HostCoreScenario.Input)
        { World = new(42, new("fixture:railway"), 100, 100, [], [], [], [], []) };
        var session = new ScriptedCoreSession(view);
        var candidate = new ScriptedCoreCandidate(session, HostCoreScenario.References,
            missing ? [new(new CoreDefinitionReference.Voxel(new("fixture:missing")), "vehicles[0]")] : []);
        var loads = 0;
        var worldInputs = 0;
        var sessionOptions = new CoreSessionOptions(new("fixture-game", "fixture-core", "fixture-os", "fixture-arch", "fixture-runtime"), true, 1);
        var core = new ScriptedCoreFactory
        {
            PrepareNewCall = (world, content, options) =>
            {
                Require(ReferenceEquals(world, HostCoreScenario.World) && ReferenceEquals(content, projection) &&
                    ReferenceEquals(options, sessionOptions), "新游戏未传递情景世界输入、内容投影和宿主会话选项。");
                return reject ? new CoreResult<ICoreSessionCandidate>.Failure(
                    [new("fixture_generation_rejected", "world", null, [])])
                    : new CoreResult<ICoreSessionCandidate>.Success(candidate);
            }
        };
        var content = new ScriptedContentFactory
        {
            PrepareLoadCall = (request, _, _) =>
            {
                Require(request.Purpose is ContentLoadPurpose.Initial && ++loads <= 2,
                    "有限情景只接受启动和新游戏的两次初始加载。");
                return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(contentView));
            },
            CreateWorldInputCall = (basis, profile, seed, generator) =>
            {
                Require(ReferenceEquals(basis, contentView) && profile == new GenerationProfileId("fixture:second") &&
                    seed == 42 && generator == "stage1-world-v1" && ++worldInputs == 1,
                    "实际输入未传入选中的配置、种子或生成器版本。");
                return new ContentResult<WorldGenerationInput>.Success(HostCoreScenario.World);
            }
        };
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new HostCoordinator(core, content, new ScriptedContentFiles(),
                new UnconfiguredAssetInspector(), new(new("builtin"), new("mods"), null, "fixture", []), logger, sessionOptions: sessionOptions);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.Content is not null && !app.Host.IsBusy);
            tree.Root.ContentScaleFactor = 1;
            await _input.Frames();
            var menu = app.GetNode<MainMenu>("MainMenu");
            var ui = app.GetNode<GameUi>("GameUi");
            menu.GetNode<Button>("%NewGame").GrabFocus();
            await _input.PressKey(Key.Space);
            var panel = ui.GetNode<Control>("Root/NewGame");
            Require(panel.IsVisibleInTree(), "菜单实际按键未打开新游戏面板。");
            var seedInput = panel.GetNode<LineEdit>("Layout/Body/Seed");
            seedInput.GrabFocus();
            await _input.Frames();
            await _input.PressKey(Key.A, control: true);
            await _input.PressKey(Key.Key4, unicode: '4');
            await _input.PressKey(Key.Key2, unicode: '2');
            Require(seedInput.Text == "42", "真实键盘输入未替换世界种子。");
            var profileInput = panel.GetNode<OptionButton>("Layout/Body/Profile");
            profileInput.GrabFocus();
            await _input.PressKey(Key.Space);
            await _input.PressKey(Key.Down);
            await _input.PressKey(Key.Enter);
            Require(profileInput.Selected == 1, "真实键盘输入未选择第二个生成配置。");
            panel.GetNode<Button>("Layout/Body/Create").GrabFocus();
            await _input.PressKey(Key.Space);
            await _input.WaitUntil(() => missing
                ? app.Host!.MissingConfirmation is not null && ui.ErrorText.Contains("fixture:missing")
                : !app.Host!.IsBusy && (reject ? ui.ErrorText.Length > 0 : app.Host.GetStatus() is not null));
            Require(loads == 2 && worldInputs == 1 && core.PrepareNewCount == 1,
                "新游戏未完成规定的有限准备序列。");
            if (missing)
            {
                Require(app.Host!.IsBusy && app.Host.GetStatus() is null && menu.IsVisibleInTree() &&
                    panel.IsVisibleInTree() && ui.ErrorText.Contains("vehicles[0]") &&
                    ui.GetNode<Control>("Root/ErrorPanel").IsVisibleInTree(),
                    "缺失引用未显示具体引用、字段和等待确认状态，或提前替换了活动局。");
                var action = panel.GetNode<Button>(confirm ? "Layout/Body/ConfirmMissing" : "Layout/Body/Cancel");
                Require(action.IsVisibleInTree() && !action.Disabled, "缺失确认的操作入口不可用。");
                Require(candidate.ActivationCount == 0, "缺失确认前已激活候选。");
                action.GrabFocus();
                await _input.PressKey(Key.Space);
                await _input.WaitUntil(() => !app.Host.IsBusy);
                Require(app.Host.MissingConfirmation is null && candidate.DisposeCount == 1 && !panel.IsVisibleInTree(),
                    "真实确认或取消输入未清除缺失确认，或候选重复释放、表单仍显示。");
                if (confirm)
                {
                    var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
                    await _input.WaitUntil(() => ReferenceEquals(map.View, view));
                    Require(candidate.ActivationCount == 1 && app.Host.GetStatus()!.PlayerPaused &&
                        map.IsVisibleInTree() && !menu.IsVisibleInTree() &&
                        !ui.GetNode<Control>("Root/ErrorPanel").IsVisibleInTree(),
                        "真实确认输入没有激活候选并进入暂停地图。");
                    session.AssertComplete();
                }
                else
                    Require(app.Host.GetStatus() is null && candidate.ActivationCount == 0,
                        "真实取消输入激活了缺失候选。");
            }
            else if (reject)
            {
                Require(app.Host!.GetStatus() is null && menu.IsVisibleInTree() && panel.IsVisibleInTree() &&
                    ui.ErrorText.Contains("fixture_generation_rejected") &&
                    ui.GetNode<Control>("Root/ErrorPanel").IsVisibleInTree(),
                    "创建失败未保留菜单与输入面板，或未显示具体拒绝原因。");
            }
            else
            {
                var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
                await _input.WaitUntil(() => ReferenceEquals(map.View, view));
                Require(app.Host!.GetStatus()!.PlayerPaused && map.IsVisibleInTree() &&
                    !menu.IsVisibleInTree() && !panel.IsVisibleInTree() && candidate.ActivationCount == 1,
                    "创建成功后未进入暂停地图，或候选重复激活。");
                session.AssertComplete();
            }
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

}
