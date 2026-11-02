using System.Collections.Immutable;
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

public partial class TrainFormationWorkspaceTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Checkboxes_and_order_submit_the_draft_and_request_plan_editing()
    {
        var submitted = new List<CoreCommand>();
        await WithWorkspace(submitted, async (panel, binding, view, content) =>
        {
            panel.Open(new(binding, content.Generation, new WorldObjectId.Node(new(1))), new NodeId(1), view, content);
            await UiCapture.Capture(TestScene, "formation-nothing-selected");
            var input = new UiTestInput(TestScene);
            await input.Activate(Find<CheckBox>(panel, "Vehicle1"));
            await input.Activate(Find<CheckBox>(panel, "Vehicle2"));
            var ordered = Find<VBoxContainer>(panel, "OrderedVehicles");
            await input.Activate(Find<Button>(ordered.GetChild(1), "MoveUp"));
            Require(ordered.GetChild(0).Name == "Vehicle2", "前移没有更新编组顺序。");
            await input.Activate(Find<Button>(ordered.GetChild(0), "Remove"));
            Require(!Find<CheckBox>(panel, "Vehicle2").ButtonPressed, "移出没有同步左侧勾选。");
            await input.Activate(Find<CheckBox>(panel, "Vehicle2"));
            await input.Activate(Find<Button>(ordered.GetChild(1), "MoveUp"));
            TrainId? saved = null; var editPlan = false;
            panel.FormationSaved += (train, edit) => { saved = train; editPlan = edit; };
            await UiCapture.Capture(TestScene, "formation-workspace");
            await input.Activate(Find<Button>(panel, "Form"));
            Require(submitted.Single() is CoreCommand.FormTrain form && form.OrderedVehicles.SequenceEqual(new VehicleId[] { new(2), new(1) }) &&
                saved == new TrainId(8) && editPlan && !panel.Visible,
                "编组没有提交勾选顺序或请求编辑新列车计划。");
        });
    }

    private async Task WithWorkspace(List<CoreCommand> submitted,
        Func<TrainFormationWorkspace, HostBinding, FixedCoreReadView, IContentView, Task> run,
        FixedCoreReadView? view = null, Func<CoreCommand, SubmissionResult>? decide = null, bool shell = false,
        IContentView? contentOverride = null)
    {
        var fixture = new Fixture(TestScene.GetTree());
        view ??= World();
        var content = contentOverride ?? Content();
        var setup = new HostSimulationTests.HostSetup(view, content);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await FramesUntil(TestScene, () => app.Host!.GetStatus() is not null);
            var panel = shell ? Find<TrainFormationWorkspace>(app, "TrainFormationWorkspace") : new TrainFormationWorkspace();
            if (!shell) panel.Initialize(app.Host!, (_, command) =>
            {
                submitted.Add(command);
                return decide?.Invoke(command) ?? new SubmissionResult.Completed(new(1, view.Position, view.Position,
                    new CommandDecision.Accepted([new WorldObjectId.Train(new(8))], [])), [], null);
            });
            if (!shell)
            {
                var ui = app.GetNode<GameUi>("GameUi"); ui.AddChild(panel);
                panel.Opened += () => ((GameShell)ui.World).ShowWorkspace(panel);
            }
            await new UiTestInput(TestScene).Frames(2);
            await run(panel, app.Host!.GetStatus()!.Binding, view, content);
        }
        finally { await fixture.Cleanup(); }
    }

    private static FixedContentView Content() => new(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
    {
        Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
        { [new("vehicle-design")] = new ContentLookup<DesignEvaluation>.NotFound() },
        DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
        { [new CoreDefinitionReference.Terrain(new("fixture:plain"))] = new ContentLookup<ContentDisplay>.NotFound() }
    };

    private static FixedCoreReadView World()
    {
        var view = UiWorldFixture.Formation(0, false);
        view.FormationPreviews = new[] { Array.Empty<ulong>(), new ulong[] { 1 }, [2], [1, 2], [2, 1] }
            .Select(ids => ((TrainId?)null, ids.Select(id => new VehicleId(id)).ToImmutableArray(),
                ids.Length == 0 ? (CoreResult<TrainFormationPreview>)new CoreResult<TrainFormationPreview>.Failure(
                    [new("composition_empty", "OrderedVehicles", null, [])])
                    : new CoreResult<TrainFormationPreview>.Success(new(12, 34, 56, 78, [], null))));
        return view;
    }
}
