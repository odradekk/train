using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class VehicleServiceUiTests
{
    [Test]
    public async Task Vehicle_entry_opens_service_page_and_preselects_saved_draft()
    {
        var draft = new DesignDraft(new("入口草稿", ""), new DesignSpecification.Vehicle(SizeClass.Medium), new([], [], []));
        var saved = new SavedContentDraft(new(Guid.Parse("00000000-0000-0000-0000-00000000000a")), new(1),
            new DraftTarget.Instance(new InstanceId.Vehicle(new(10))), new DraftPayload.Design(draft));
        var setup = new HostSimulationTests.HostSetup(new ServiceView(), EconomyFixture.Content(drafts: [saved]));
        await Run(setup, async (ui, economy) =>
        {
            await _input.Activate(Find<Button>(ui, "Fleet"));
            await _input.ClickItem(Find<Tree>(Find<PanelContainer>(ui, "FleetList"), "Objects"), 0);
            await _input.ChooseTab(Find<TabContainer>(Find<PanelContainer>(ui, "Details"), "Pages"), 3);
            await _input.Activate(Find<Button>(Find<PanelContainer>(ui, "Details"), "VehicleServices"));
            var tabs = Find<TabContainer>(economy, "Pages");
            Require(tabs.GetTabTitle(tabs.CurrentTab) == "建造与工地", "车辆入口没有打开建造与工地页。");
            Require(Find<OptionButton>(economy, "ServiceTargetKind").Selected == 1, "没有预选已保存草稿。");
            var drafts = Find<OptionButton>(economy, "ServiceDraft");
            Require(drafts.Selected > 0 && drafts.GetItemText(drafts.Selected).Contains("入口草稿"), "已保存草稿没有预选。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Accepted_design_activation_keeps_reusable_inspectors_until_reload()
    {
        var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
            new(new("service-design"), new("service-composition"), new DesignKind.Vehicle(SizeClass.Small, 2),
                [], 10, 10, 1, new(100), [], [new(EconomyFixture.Food, 1)], 1, [], true, []), []);
        var draft = new DesignDraft(new("本局制造蓝图", ""), new DesignSpecification.Vehicle(SizeClass.Small), new([], [], []));
        var evaluation = new DesignEvaluation.Complete(design.Design.Design, design.Design.Composition, draft, design,
            new(null, new(new(0, 0, 0), new(1, 1, 1)), null, true, null, null, [], [], [], [], []), true, []);
        var blueprint = new BlueprintRecord(new(BlueprintLibrary.Local, new("service-blueprint")), draft, [], evaluation);
        var content = EconomyFixture.Content(vehicleBlueprint: blueprint);
        var activated = EconomyFixture.Content(generation: content.Generation.Value + 1, vehicleBlueprint: blueprint);
        var before = new ServiceView(expectedDesign: design, activeTrain: true);
        var setup = new HostSimulationTests.HostSetup(before, content);
        using var candidate = new ScriptedContentCandidate(activated, content, ContentChange.Persistent,
            edit: new PreparedContentEdit.ConstructionTarget(design));
        await Run(setup, async (ui, economy) =>
        {
            setup.ContentFactory!.PrepareEditCall = (_, _) => new ContentResult<IContentCandidate>.Success(candidate);
            await _input.Activate(Find<Button>(ui, "Fleet"));
            await _input.ClickItem(Find<Tree>(Find<PanelContainer>(ui, "FleetList"), "Objects"), 0);
            var shell = Find<GameShell>(ui, "*");
            var train = shell.InspectorContent!;
            var stop = Find<Button>(train, "Stop");
            Require(!stop.Disabled, "列车详情在制造前不可停车。");
            await _input.ChooseTab(Find<TabContainer>(train, "Pages"), 3);
            await _input.Activate(Find<Button>(train, "VehicleServices"));
            await _input.Choose(Find<OptionButton>(economy, "ServiceOperation"), 0);
            await _input.Choose(Find<OptionButton>(economy, "ServiceFacility"), 0);
            await _input.Choose(Find<OptionButton>(economy, "ServiceBlueprint"), 0);
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            var quote = before.LastQuote!;
            var payment = quote.TotalPayment ?? throw new InvalidOperationException("制造报价没有总额。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(quote.Request, payment), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
            await _input.Activate(Find<Button>(economy, "ConfirmService"));
            await _input.WaitUntil(() => candidate.Activations == 1);
            await _input.Frames(2);
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(ui.FindChild("FleetList", true, false) is not null && ui.FindChild("NodesList", true, false) is not null, "接受设计目标后关闭了仍打开的列表。");
            Require(!stop.Disabled && !Find<Label>(economy, "State").Text.Contains("已不存在"), "接受设计目标后其他面板或车辆服务页失效。");
            shell.Inspect(new WorldObjectId.Train(new(7)), train);
            Require(train.IsVisibleInTree() && !economy.IsVisibleInTree(), "恢复列车检查器没有收起服务页。");
            setup.Session.QueueSubmit(new CoreCommand.StopTrain(new(7)), CommandSource.UserInterface,
                Completed(2, before, before, new CommandDecision.Accepted([], [])), before);
            stop.GrabFocus();
            await _input.PressKey(Key.Space);
            Require(setup.Session.SubmitCount == 2, "设计激活后列车详情不能提交停车。");
            await _input.Frames(2);
            Require(ui.FindChild("FleetList", true, false) is not null && ui.FindChild("NodesList", true, false) is not null, "接受普通命令后关闭了仍打开的列表。");
            Require(!stop.Disabled && !Find<Label>(train, "Description").Text.Contains("已不存在"),
                "接受普通命令后列车详情失效。");
            setup.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(
                new Train.Core.ContractSupport.FixedCoreCapture(new(before.Position, "fixed", "fixed"),
                    Train.Core.ContractSupport.HostCoreScenario.References, "opaque", before.InputIdentity, before.Revision)));
            var reloaded = EconomyFixture.Content(generation: host.Content!.Generation.Value + 1, vehicleBlueprint: blueprint);
            using var reload = new ScriptedContentCandidate(reloaded, host.Content, ContentChange.Presentation);
            setup.ContentFactory.PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Reload
                ? new ContentResult<IContentCandidate>.Success(reload)
                : throw new InvalidOperationException("UNCONFIGURED: " + request.Purpose);
            Require(host.BeginReload(CommandSource.UserInterface).Problem is null, "内容重载被拒绝。");
            await _input.WaitUntil(() => !host.IsBusy && ReferenceEquals(host.Content, reloaded));
            await _input.Frames(2);
            Require(ui.FindChild("FleetList", true, false) is null && stop.Disabled, "内容重载后旧列表或列车详情仍然有效。");
            var submitted = setup.Session.SubmitCount;
            stop.GrabFocus();
            await _input.PressKey(Key.Space);
            Require(setup.Session.SubmitCount == submitted, "内容重载后旧列车详情仍提交了停车。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Blueprint_publish_refreshes_service_choices_and_keeps_train_actions()
    {
        BlueprintRecord Blueprint(string id, string name)
        {
            var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
                new(new("service-design"), new("service-composition"), new DesignKind.Vehicle(SizeClass.Small, 2),
                    [], 10, 10, 1, new(100), [], [new(EconomyFixture.Food, 1)], 1, [], true, []), []);
            var definition = new DesignDraft(new(name, ""), new DesignSpecification.Vehicle(SizeClass.Small), new([], [], []));
            var evaluation = new DesignEvaluation.Complete(design.Design.Design, design.Design.Composition, definition, design,
                new(null, new(new(0, 0, 0), new(1, 1, 1)), null, true, null, null, [], [], [], [], []), true, []);
            return new(new(BlueprintLibrary.Local, new(id)), definition, [], evaluation);
        }
        var existing = Blueprint("existing-blueprint", "原制造蓝图");
        var published = Blueprint("published-blueprint", "发布后的蓝图");
        var content = EconomyFixture.Content(vehicleBlueprint: existing);
        var activated = EconomyFixture.Content(generation: content.Generation.Value + 1,
            vehicleBlueprint: existing, otherBlueprint: published);
        var setup = new HostSimulationTests.HostSetup(new ServiceView(), content);
        using var candidate = new ScriptedContentCandidate(activated, content, ContentChange.Library | ContentChange.Persistent,
            edit: new PreparedContentEdit.BlueprintSaved(published.Address, ((DesignEvaluation.Complete)published.Evaluation).Design));
        await Run(setup, async (ui, economy) =>
        {
            setup.ContentFactory!.PrepareEditCall = (basis, edit) =>
            {
                Require(ReferenceEquals(basis, content) && edit is ContentEdit.SaveBlueprint, "保存蓝图没有按当前内容准备。");
                return new ContentResult<IContentCandidate>.Success(candidate);
            };
            await Services(economy);
            await _input.Activate(Find<Button>(ui, "Fleet"));
            await _input.ClickItem(Find<Tree>(Find<PanelContainer>(ui, "FleetList"), "Objects"), 0);
            var shell = Find<GameShell>(ui, "*");
            var train = shell.InspectorContent!;
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.BeginContentEdit(new ContentEdit.SaveBlueprint(
                new BlueprintSaveTarget.New(BlueprintLibrary.Local), published.Definition),
                _ => new Train.App.Saves.FileWriteResult(true)).Problem is null, "保存蓝图编辑被拒绝。");
            await _input.WaitUntil(() => !host.IsBusy && ReferenceEquals(host.Content, activated));
            await _input.Frames(2);
            Require(!Find<Label>(train, "Description").Text.Contains("已不存在"), "发布蓝图后列车详情失效。");
            await _input.ChooseTab(Find<TabContainer>(train, "Pages"), 3);
            await _input.Activate(Find<Button>(train, "VehicleServices"));
            await _input.Choose(Find<OptionButton>(economy, "ServiceOperation"), 0);
            Require(shell.InspectorContent == economy && !Find<Label>(economy, "State").Text.Contains("已不存在"),
                "发布蓝图后聚居地服务检查器失效。");
            var choices = Find<OptionButton>(economy, "ServiceBlueprint");
            Require(Enumerable.Range(0, choices.ItemCount).Any(index => choices.GetItemText(index).Contains("发布后的蓝图")),
                "发布蓝图后服务页设计下拉没有新蓝图。");
            shell.Inspect(new WorldObjectId.Train(new(7)), train);
            await _input.Activate(Find<Button>(ui, "EditPlan"));
            var plan = Find<TrainPlanWorkspace>(ui, "*");
            Require(plan.Visible && !ui.ErrorText.Contains("已不存在") && !ui.ErrorText.Contains("操作未提交"),
                $"发布蓝图后编辑计划没有打开或报了对象失效。visible={plan.Visible} error={ui.ErrorText}");
        });
        setup.Session.AssertComplete();
    }
}
