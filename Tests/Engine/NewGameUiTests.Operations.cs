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

public partial class NewGameUiTests
{
    [Test]
    public async Task Generating_world_shows_progress_and_cancel_discards_late_result()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = new Fixture(TestScene.GetTree());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var candidate = new ScriptedCoreCandidate(UiWorldFixture.Session(new FixedCoreReadView(default, HostCoreScenario.Input)),
            HostCoreScenario.References);
        var core = new ScriptedCoreFactory
        {
            PrepareNewCall = (_, _, _) =>
            {
                entered.Set();
                Require(release.Wait(TimeSpan.FromSeconds(20)), "测试没有释放生成步骤。");
                return new CoreResult<ICoreSessionCandidate>.Success(candidate);
            }
        };
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new(core, OperationContent(), new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "fixture", []), logger,
                sessionOptions: new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => !app.Host!.IsBusy && app.Host.Content is not null);
            await _input.Activate(app.GetNode<MainMenu>("MainMenu").GetNode<Button>("%NewGame"));
            var panel = app.GetNode<GameUi>("GameUi").GetNode<Control>("Root/NewGame");
            await _input.Activate(Find<Button>(panel, "Create"));
            await _input.WaitUntil(() => entered.IsSet);
            var progress = Find<ProgressBar>(panel, "GenerationProgress");
            Require(progress.IsVisibleInTree() && progress.Indeterminate && Find<Button>(panel, "Create").Disabled &&
                !Find<LineEdit>(panel, "Seed").Editable && !Find<LineEdit>(panel, "SaveName").Editable &&
                Find<OptionButton>(panel, "Profile").Disabled && Find<Button>(panel, "RandomSeed").Disabled,
                "生成时必须显示不确定进度并锁定创建参数，防止重复开始。");
            await MenuCapture.Capture(TestScene, "newgame-generating");
            await _input.Activate(Find<Button>(panel, "Cancel"));
            Require(!panel.IsVisibleInTree() && !app.Host!.IsBusy && app.Host.GetStatus() is null,
                "取消生成没有立刻返回菜单。");
            release.Set();
            await _input.WaitUntil(() => candidate.DisposeCount == 1);
            Require(candidate.ActivationCount == 0 && app.Host!.GetStatus() is null,
                "取消后迟到的生成结果不得激活。");
        }
        finally { release.Set(); await fixture.Cleanup(); }
    }

    [Test]
    public async Task Content_failure_keeps_inputs_and_hides_technical_diagnostics()
    {
        UiCapture.ConfigureWindowIfNeeded(TestScene.GetTree().Root);
        var fixture = new Fixture(TestScene.GetTree());
        var content = OperationContent();
        var loaded = content.PrepareLoadCall;
        var loads = 0;
        content.PrepareLoadCall = (request, files, assets) => ++loads == 1 ? loaded!(request, files, assets)
            : new ContentResult<IContentCandidate>.Failure([new("invalid_json", ContentDiagnosticSeverity.Error,
                "loading", null, null, "/generation/profile", null, "Invalid JSON: GenerationProfile enum at internal/file.json")]);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new(new ScriptedCoreFactory(), content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "fixture", []), logger,
                sessionOptions: new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => !app.Host!.IsBusy && app.Host.Content is not null);
            await _input.Activate(app.GetNode<MainMenu>("MainMenu").GetNode<Button>("%NewGame"));
            var ui = app.GetNode<GameUi>("GameUi");
            var panel = ui.GetNode<Control>("Root/NewGame");
            Find<LineEdit>(panel, "SaveName").Text = "保留的名称";
            Find<LineEdit>(panel, "Seed").Text = "18446744073709551615";
            await _input.Activate(Find<Button>(panel, "Create"));
            await _input.WaitUntil(() => loads == 2 && !app.Host!.IsBusy);
            Require(panel.IsVisibleInTree() && Find<LineEdit>(panel, "SaveName").Text == "保留的名称" &&
                Find<LineEdit>(panel, "Seed").Text == "18446744073709551615" && !Find<Button>(panel, "Create").Disabled,
                "准备失败必须保留输入并允许重试。");
            Require(ui.ErrorText == "部分内容无法加载，请检查已安装的内容包。" &&
                Find<Label>(panel, "Problem").Text == ui.ErrorText,
                "新游戏的失败界面不能泄露诊断代码、字段路径或引擎英文文本。");
            await MenuCapture.Capture(TestScene, "newgame-content-failure");
        }
        finally { await fixture.Cleanup(); }
    }

    private static ScriptedContentFactory OperationContent()
    {
        var view = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        { GenerationProfiles = [new(new("fixture:world"), new("测试世界", "用于检查生成和取消。"))] };
        return new()
        {
            PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view)),
            CreateWorldInputCall = (_, _, _, _) => new ContentResult<WorldGenerationInput>.Success(HostCoreScenario.World)
        };
    }
}
