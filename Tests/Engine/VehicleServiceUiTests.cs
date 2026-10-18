using System.Collections.Immutable;
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

public class VehicleServiceUiTests(Node testScene) : TestClass(testScene)
{
    private readonly UiTestInput _input = new(testScene);

    [Test]
    public async Task Repair_uses_ordered_core_quote_and_explicit_price_without_retrying_rejection()
    {
        var before = new ServiceView();
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            Require(before.QuoteCount == 0, "隐藏服务页不应获取报价。");
            await Services(economy);
            var operations = Find<OptionButton>(economy, "ServiceOperation");
            Require(Enumerable.Range(0, operations.ItemCount).All(index => operations.GetItemText(index) != "拆解车辆"),
                "非自有聚居地提供了拆解车辆。");
            await SelectRepair(economy);
            Require(Find<OptionButton>(economy, "ServiceFacility").ItemCount == 1, "非施工能力建筑被列为执行设施。");
            await _input.Choose(Find<OptionButton>(economy, "ServiceSource"), 1);
            await _input.Activate(Find<Button>(economy, "AddServiceSource"));
            await _input.Choose(Find<OptionButton>(economy, "ServiceSource"), 2);
            await _input.Activate(Find<Button>(economy, "AddServiceSource"));
            await _input.ClickItem(Find<ItemList>(economy, "ServiceSourceOrder"), 1);
            await _input.Activate(Find<Button>(economy, "MoveServiceSourceUp"));
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            var quote = before.LastQuote!;
            Require(quote.Request is JobRequest.Repair repair && repair.Sources.SequenceEqual(new InventoryId[] { new(11), new(12) }), "材料来源顺序没有传给核心。");
            var text = Find<Label>(economy, "ServiceQuote").Text;
            Require(text.Contains("NPC 补购") && text.Contains("总额：999") && text.Contains("× 3"), "未显示核心补购和精确付款。");
            var previousCount = before.QuoteCount;
            await _input.Activate(Find<Button>(economy, "MoveServiceSourceDown"));
            Require(Find<Button>(economy, "ConfirmService").Disabled && before.QuoteCount == previousCount,
                "材料来源次序变化未使报价失效或自动获取新报价。");
            await _input.ClickItem(Find<ItemList>(economy, "ServiceSourceOrder"), 1);
            await _input.Activate(Find<Button>(economy, "MoveServiceSourceUp"));
            Require(before.QuoteCount == previousCount, "恢复输入仍不应自动报价。");
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            quote = before.LastQuote!;
            var count = before.QuoteCount;
            await _input.Frames(4);
            Require(before.QuoteCount == count, "同一读视图逐帧重复获取报价。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(quote.Request, new(999)), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Rejected([new("price_changed", "MaximumPayment", null, ["fixture:price-rise"])])), before);
            await _input.Activate(Find<Button>(economy, "ConfirmService"));
            await _input.Frames(3);
            Require(setup.Session.SubmitCount == 1 && before.QuoteCount == count && Find<Button>(economy, "ConfirmService").Disabled,
                "旧价被拒绝后自动重报、重试或仍可确认。");
            Require(ui.ErrorText.Contains("price_changed"), "拒绝没有显示核心原因。");
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            var confirm = Find<Button>(economy, "ConfirmService");
            Require(!confirm.Disabled, "有效报价应可确认。");
            var submitted = setup.Session.SubmitCount;
            economy.Refresh(new HostBinding(Guid.NewGuid()), before, ui.GetParent<Train.App.App>().Host!.Content!);
            confirm.EmitSignal(BaseButton.SignalName.Pressed);
            Require(confirm.Disabled && setup.Session.SubmitCount == submitted, "陈旧绑定仍可确认或提交了服务。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Blockers_null_payment_and_new_boundary_update_queue_without_replacing_controls()
    {
        var before = new ServiceView(blocked: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            await Services(economy);
            await SelectRepair(economy);
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            var description = Find<Label>(economy, "ServiceQuote");
            Require(description.Text.Contains("fixture:missing") && description.Text.Contains("fixture:capacity") &&
                Find<Button>(economy, "ConfirmService").Disabled, "报价没有完整显示所有阻塞或允许提交。");
            var row = Find<Label>(Find<VBoxContainer>(economy, "ServiceJobs"), "ServiceJob_4");
            Require(row.Text.Contains("fixture:missing") && row.Text.Contains("劳动力"), "队列缺料和工作限制未显示。");
            var next = new ServiceView(2, nullPayment: true);
            var host = ui.GetParent<Train.App.App>().Host!;
            setup.Session.QueueAdvance(new(1, next.Position, [], null, null), next);
            Require(host.Step(1).Problem is null, "无法发布新边界。");
            economy.Refresh(host.GetStatus()!.Binding, next, host.Content!);
            Require(ReferenceEquals(description, Find<Label>(economy, "ServiceQuote")) &&
                ReferenceEquals(row, Find<Label>(Find<VBoxContainer>(economy, "ServiceJobs"), "ServiceJob_4")) &&
                row.Text.Contains("60/100") && row.Text.Contains("电力"), "边界刷新没有更新进度和限制或替换了控件。");
            Require(next.QuoteCount == 0 && Find<Button>(economy, "ConfirmService").Disabled, "新读视图不应自动报价。");
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            Require(next.QuoteCount == 1 && Find<Button>(economy, "ConfirmService").Disabled && description.Text.Contains("不能确认"), "NPC未提供总付款仍可确认。");
            var gone = new ServiceView(3, missingFacility: true);
            setup.Session.QueueAdvance(new(1, gone.Position, [], null, null), gone);
            Require(host.Step(1).Problem is null, "无法发布对象移除边界。");
            economy.Refresh(host.GetStatus()!.Binding, gone, host.Content!);
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            Require(description.Text.Contains("已不存在") && Find<Button>(economy, "ConfirmService").Disabled && gone.QuoteCount == 0,
                "设施消失后仍向核心报价或未说明原因。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Core_completion_removes_queue_row_and_opens_existing_formation()
    {
        var before = new ServiceView();
        var after = new ServiceView(2, completed: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (ui, economy) =>
        {
            await Services(economy);
            var completion = new CoreEvent.ConstructionCompleted(new(4), ConstructionKind.Construction,
                new ConstructionTarget.NewVehicle(new("fixture-vehicle")), new InstanceId.Vehicle(new(9)), []);
            setup.Session.QueueAdvance(new(1, after.Position, [new(after.Position, 0, completion)], null, null), after);
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.Step(1).Problem is null, "不能发布真实宿主完工事件。");
            await _input.WaitUntil(() => Find<Label>(economy, "ServiceResult").Text.Contains("可编组"));
            Require(Find<VBoxContainer>(economy, "ServiceJobs").GetChildCount() == 0,
                "已不在CommissionJobs的任务仍保留在队列。");
            await _input.Activate(Find<Button>(economy, "FormServiceVehicle"));
            var formation = ui.GetNode<TrainFormationPanel>("Root/World/TrainFormationPanel");
            var available = Find<ItemList>(formation, "AvailableVehicles");
            Require(available.IsVisibleInTree() && available.ItemCount == 1 && available.GetItemText(0).Contains("车辆 9"),
                "完工后的原车辆没有进入现有编组入口。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Build_from_local_blueprint_submits_content_candidate_design_and_activates_only_after_acceptance()
    {
        var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
            new(new("service-design"), new("service-composition"), new DesignKind.Vehicle(SizeClass.Small, 2),
                [], 10, 10, 1, new(100), [], [new(EconomyFixture.Food, 1)], 1, [], true, []), []);
        var draft = new DesignDraft(new("本局制造蓝图", ""), new DesignSpecification.Vehicle(SizeClass.Small), new([], [], []));
        var evaluation = new DesignEvaluation.Complete(design.Design.Design, design.Design.Composition, draft, design,
            new(null, new(new(0, 0, 0), new(1, 1, 1)), null, true, null, null, [], [], [], [], []), true, []);
        var blueprint = new BlueprintRecord(new(BlueprintLibrary.Local, new("service-blueprint")), draft, [], evaluation);
        var content = EconomyFixture.Content(vehicleBlueprint: blueprint);
        var target = EconomyFixture.Content(vehicleBlueprint: blueprint);
        var before = new ServiceView(expectedDesign: design);
        var setup = new HostSimulationTests.HostSetup(before, content);
        using var candidate = new ScriptedContentCandidate(target, content, ContentChange.Persistent,
            edit: new PreparedContentEdit.ConstructionTarget(design));
        var preparations = 0;
        await Run(setup, async (ui, economy) =>
        {
            setup.ContentFactory!.PrepareEditCall = (basis, edit) =>
            {
                Require(ReferenceEquals(basis, content) && edit is ContentEdit.ConstructionTarget requested &&
                    ReferenceEquals(requested.Target, draft), "制造没有把所选本局蓝图交给内容准备目标。");
                preparations++;
                return new ContentResult<IContentCandidate>.Success(candidate);
            };
            await Services(economy);
            await _input.Choose(Find<OptionButton>(economy, "ServiceFacility"), 0);
            var choices = Find<OptionButton>(economy, "ServiceBlueprint");
            Require(choices.ItemCount == 1 && choices.GetItemText(0).Contains("本局库"), "本局库蓝图未展示。");
            await _input.Choose(choices, 0);
            Require(preparations == 0 && before.QuoteCount == 0, "选择输入不应准备或自动报价。");
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            Require(preparations == 1 && candidate.Activations == 0 && ReferenceEquals(ui.GetParent<Train.App.App>().Host!.Content, content),
                "报价前错误激活内容或没有准备候选。");
            var quote = before.LastQuote!;
            Require(quote.Request is JobRequest.BuildVehicle build && ReferenceEquals(build.Design, design),
                "UI未使用内容候选提供的DesignInput。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(quote.Request, new(999)), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
            await _input.Activate(Find<Button>(economy, "ConfirmService"));
            await _input.WaitUntil(() => candidate.Activations == 1);
            Require(setup.Session.SubmitCount == 1 && ReferenceEquals(ui.GetParent<Train.App.App>().Host!.Content, target),
                "制造接受后没有激活同一候选。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Persistent_content_change_invalidates_quote_and_reprepares_same_blueprint_on_explicit_refresh()
    {
        var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
            new(new("basis-design"), new("basis-composition"), new DesignKind.Vehicle(SizeClass.Small, 2),
                [], 10, 1, 1, new(1), [], [], 0, [], true, []), []);
        var draft = new DesignDraft(new("保留选择的蓝图", ""), new DesignSpecification.Vehicle(SizeClass.Small), new([], [], []));
        var evaluation = new DesignEvaluation.Complete(design.Design.Design, design.Design.Composition, draft, design,
            new(null, null, null, true, null, null, [], [], [], [], []), true, []);
        var blueprint = new BlueprintRecord(new(BlueprintLibrary.Local, new("basis-blueprint")), draft, [], evaluation);
        var saved = new SavedContentDraft(new(Guid.Parse("00000000-0000-0000-0000-000000000003")), new(1),
            new DraftTarget.LocalBlueprint(null), new DraftPayload.Design(draft));
        var content = EconomyFixture.Content(vehicleBlueprint: blueprint);
        var changed = EconomyFixture.Content(vehicleBlueprint: blueprint, drafts: [saved]);
        var target = EconomyFixture.Content(vehicleBlueprint: blueprint, drafts: [saved]);
        var before = new ServiceView(expectedDesign: design);
        var setup = new HostSimulationTests.HostSetup(before, content);
        using var oldCandidate = new ScriptedContentCandidate(content, content, ContentChange.Persistent,
            edit: new PreparedContentEdit.ConstructionTarget(design));
        using var savedCandidate = new ScriptedContentCandidate(changed, content, ContentChange.Persistent,
            edit: new PreparedContentEdit.DraftSaved(saved.Id, saved.Revision));
        using var newCandidate = new ScriptedContentCandidate(target, changed, ContentChange.Persistent,
            edit: new PreparedContentEdit.ConstructionTarget(design));
        var preparations = 0;
        await Run(setup, async (ui, economy) =>
        {
            setup.ContentFactory!.PrepareEditCall = (basis, edit) =>
            {
                if (edit is ContentEdit.SaveLocalDraft save)
                {
                    Require(ReferenceEquals(basis, content) && save.Draft == saved, "持久草稿变更使用了错误基准。");
                    return new ContentResult<IContentCandidate>.Success(savedCandidate);
                }
                Require(edit is ContentEdit.ConstructionTarget request && ReferenceEquals(request.Target, draft), "重新报价没有保留所选蓝图。");
                preparations++;
                Require(ReferenceEquals(basis, preparations == 1 ? content : changed), "重新报价没有用当前内容基准准备目标。");
                return new ContentResult<IContentCandidate>.Success(preparations == 1 ? oldCandidate : newCandidate);
            };
            await Services(economy);
            await _input.Choose(Find<OptionButton>(economy, "ServiceFacility"), 0);
            var choices = Find<OptionButton>(economy, "ServiceBlueprint");
            await _input.Choose(choices, 0);
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            Require(preparations == 1 && before.QuoteCount == 1 && !Find<Button>(economy, "ConfirmService").Disabled, "初次报价未完成。");
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.BeginContentEdit(new ContentEdit.SaveLocalDraft(saved)).Problem is null, "无法开始持久内容变更。");
            await _input.WaitUntil(() => !host.IsBusy && ReferenceEquals(host.Content, changed));
            await _input.Frames(2);
            Require(changed.Generation == content.Generation && preparations == 1 && before.QuoteCount == 1 &&
                Find<Button>(economy, "ConfirmService").Disabled && Find<Label>(economy, "ServiceQuote").Text.Contains("重新"),
                "同Generation内容变更未使旧报价失效，或隐式重新准备及报价。");
            Require(ReferenceEquals(choices, Find<OptionButton>(economy, "ServiceBlueprint")) && choices.Selected == 0, "内容变更丢失原蓝图选择。");
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            Require(preparations == 2 && before.QuoteCount == 2 && !Find<Button>(economy, "ConfirmService").Disabled, "显式刷新没有恢复当前基准报价。");
            var quote = before.LastQuote!;
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(quote.Request, new(999)), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
            await _input.Activate(Find<Button>(economy, "ConfirmService"));
            Require(setup.Session.SubmitCount == 1 && newCandidate.Activations == 1 && oldCandidate.Activations == 0 &&
                ReferenceEquals(host.Content, target), "重报价后未提交当前基准候选。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Owned_repair_submits_owned_job_without_npc_payment()
    {
        var before = new ServiceView(nullPayment: true, owned: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (_, economy) =>
        {
            await Services(economy);
            await SelectRepair(economy);
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            var quote = before.LastQuote!;
            Require(quote.Funding == JobFunding.OwnInventory && quote.TotalPayment is null &&
                !Find<Button>(economy, "ConfirmService").Disabled, "自有修理错误依赖NPC付款。");
            setup.Session.QueueSubmit(new CoreCommand.StartOwnedJob(quote.Request), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
            await _input.Activate(Find<Button>(economy, "ConfirmService"));
            Require(setup.Session.SubmitCount == 1, "自有修理未通过StartOwnedJob提交。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Owned_dismantle_uses_core_quote_and_starts_owned_job_with_default_priority()
    {
        var before = new ServiceView(owned: true, completed: true);
        var after = new ServiceView(2, owned: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (_, economy) =>
        {
            await Services(economy);
            var ownedOperations = Find<OptionButton>(economy, "ServiceOperation");
            Require(Enumerable.Range(0, ownedOperations.ItemCount).Any(index => ownedOperations.GetItemText(index) == "拆解车辆"),
                "自有聚居地没有拆解车辆。");
            await _input.Choose(ownedOperations, 3);
            await _input.Choose(Find<OptionButton>(economy, "ServiceFacility"), 0);
            await _input.Choose(Find<OptionButton>(economy, "ServiceVehicle"), 1);
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            var quote = before.LastQuote!;
            Require(quote.Funding == JobFunding.OwnInventory && quote.Request is JobRequest.Dismantle
                { Instance: InstanceId.Vehicle { Id.Value: 9 }, Priority: 0 }, "拆解没有采用自有报价、原实例或固定默认优先级。");
            Require(Find<Label>(economy, "ServiceDifference").Text.Contains("完工时损伤"), "拆解未说明核心完工结算返还。");
            setup.Session.QueueSubmit(new CoreCommand.StartOwnedJob(quote.Request), CommandSource.UserInterface,
                Completed(1, before, after, new CommandDecision.Accepted([], [])), after);
            await _input.Activate(Find<Button>(economy, "ConfirmService"));
            Require(setup.Session.SubmitCount == 1 && Find<Button>(economy, "PauseServiceJob_4").IsVisibleInTree(),
                "拆解受理后没有读取OwnedJobs和暂停入口。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Owned_deconstruction_pause_and_resume_follow_current_owned_jobs()
    {
        var before = new ServiceView(owned: true);
        var paused = new ServiceView(2, owned: true, paused: true);
        var gone = new ServiceView(3, owned: true, completed: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (_, economy) =>
        {
            await Services(economy);
            Require(Find<Label>(economy, "ServiceJob_4").Text.Contains("拆解") && Find<Label>(economy, "ServiceJob_5").Text.Contains("拆解"),
                "自有拆解任务没有从OwnedJobs展示。");
            Require(Find<Button>(economy, "PauseServiceJob_5").IsVisibleInTree(), "第二个拆解任务没有独立暂停入口。");
            setup.Session.QueueSubmit(new CoreCommand.SetDeconstructionPaused(new(4), true), CommandSource.UserInterface,
                Completed(1, before, paused, new CommandDecision.Accepted([], [])), paused);
            await _input.Activate(Find<Button>(economy, "PauseServiceJob_4"));
            Require(Find<Label>(economy, "ServiceJob_4").Text.Contains("已暂停") && !Find<Label>(economy, "ServiceJob_5").Text.Contains("已暂停"),
                "暂停没有只作用于所选任务。");
            setup.Session.QueueSubmit(new CoreCommand.SetDeconstructionPaused(new(4), false), CommandSource.UserInterface,
                Completed(2, paused, gone, new CommandDecision.Accepted([], [])), gone);
            await _input.Activate(Find<Button>(economy, "PauseServiceJob_4"));
            await _input.Frames(2);
            Require(!economy.FindChildren("ServiceJob_4", "Label", true, false).Any(), "任务离开OwnedJobs后仍被缓存展示。");
            var serviceButtons = economy.GetNode<ScrollContainer>("Settlement/Layout/Content/Body/Pages/车辆服务")
                .FindChildren("*", "Button", true, false).OfType<Button>().Select(button => button.Text).ToArray();
            Require(!serviceButtons.Any(text => text.Contains("取消") || text.Contains("退款") || text.Contains("优先级")),
                "车辆服务按钮集合含取消、退款或优先级：" + string.Join("、", serviceButtons));
            Require(before.QuoteCount == 0 && paused.QuoteCount == 0 && gone.QuoteCount == 0, "任务操作隐式获取报价。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Refit_saved_instance_draft_uses_content_difference_for_commission_and_owned_work()
    {
        foreach (var (owned, immediate, resemblesImmediate) in new[] { (false, false, false), (true, false, false), (true, true, false), (true, false, true) })
        {
            var sameComposition = immediate || resemblesImmediate;
            var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
                new(new("refit-target"), new(sameComposition ? "fixture-composition" : "refit-composition"), new DesignKind.Vehicle(SizeClass.Medium, 2),
                    [], 100, 1, 1, new(1), [], [], 0, [], true, []), []);
            var draft = new DesignDraft(new("已保存改装", ""), new DesignSpecification.Vehicle(SizeClass.Medium), new([], [], []));
            var saved = new SavedContentDraft(new(Guid.Parse("00000000-0000-0000-0000-000000000001")), new(1), new DraftTarget.Instance(new InstanceId.Vehicle(new(9))), new DraftPayload.Design(draft));
            var other = saved with { Id = new(Guid.Parse("00000000-0000-0000-0000-000000000002")), Target = new DraftTarget.Instance(new InstanceId.Vehicle(new(10))) };
            var content = EconomyFixture.Content(drafts: [saved, other]);
            var target = EconomyFixture.Content(generation: 2, drafts: [saved, other]);
            var change = new DesignChangeProjection(new("fixture-vehicle"), new("fixture-composition"), design, [],
                sameComposition ? [] : [new(EconomyFixture.Food, 5)], sameComposition ? [] : [new(EconomyFixture.Equipment, 7, [], [], [])], sameComposition ? 0 : 3);
            var before = new ServiceView(owned: owned, expectedDesign: design);
            var setup = new HostSimulationTests.HostSetup(before, content);
            using var candidate = new ScriptedContentCandidate(target, content, ContentChange.Persistent,
                edit: new PreparedContentEdit.InstanceTarget(new(change, [], immediate, [])));
            await Run(setup, async (_, economy) =>
            {
                setup.ContentFactory!.PrepareEditCall = (basis, edit) =>
                {
                    Require(ReferenceEquals(basis, content) && edit is ContentEdit.InstanceTarget request &&
                        request.Current == new DesignHash("fixture-vehicle") && ReferenceEquals(request.Target, draft), "改装未准备所选实例保存草稿。");
                    return new ContentResult<IContentCandidate>.Success(candidate);
                };
                await Services(economy);
                await _input.Choose(Find<OptionButton>(economy, "ServiceOperation"), 2);
                await _input.Choose(Find<OptionButton>(economy, "ServiceFacility"), 0);
                await _input.Choose(Find<OptionButton>(economy, "ServiceVehicle"), 1);
                await _input.Choose(Find<OptionButton>(economy, "ServiceTargetKind"), 1);
                var drafts = Find<OptionButton>(economy, "ServiceDraft");
                Require(drafts.ItemCount == 2, "草稿候选应只含占位和当前实例保存草稿。");
                await _input.Choose(drafts, 1);
                Require(before.QuoteCount == 0 && candidate.Activations == 0, "草稿选择隐式提交或报价。");
                await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
                var quote = before.LastQuote!;
                if (!immediate) Require(quote.Request is JobRequest.Refit refit && ReferenceEquals(refit.Change, change) && refit.Instance == new InstanceId.Vehicle(new(9)), "改装未使用内容差量。");
                else Require(before.QuoteCount == 0, "立即应用不应获取施工报价。");
                var differenceText = Find<Label>(economy, "ServiceDifference").Text;
                Require(sameComposition
                    ? differenceText.Contains("改动单位：0") && !differenceText.Contains("× 5")
                    : differenceText.Contains("× 5") && differenceText.Contains("改动单位：3") && !differenceText.Contains("× 7"),
                    "差量没有展示内容新增货物和改动单位，或把拆下项当作返还数量显示。");
                Require(Find<Button>(economy, "ApplyServiceDesign").Disabled != immediate, "立即应用资格未使用内容评估。");
                if (immediate) Require(Find<Button>(economy, "ConfirmService").Disabled, "立即应用构成不应创建正式任务。");
                CoreCommand command = immediate ? new CoreCommand.ApplyInstanceDesign(new InstanceId.Vehicle(new(9)), change)
                    : owned ? new CoreCommand.StartOwnedJob(quote.Request) : new CoreCommand.CommissionVehicleWork(quote.Request, new(999));
                setup.Session.QueueSubmit(command, CommandSource.UserInterface, Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
                await _input.Activate(Find<Button>(economy, immediate ? "ApplyServiceDesign" : "ConfirmService"));
                Require(setup.Session.SubmitCount == 1 && candidate.Activations == 1, "改装接受后未提交或激活候选。");
                Require(!Find<Button>(economy, "RefreshServiceQuote").Disabled && Find<Button>(economy, "EditServiceVehicle").IsVisibleInTree(),
                    "本面板接受新世代目标后没有继续保留可用服务入口。");
            });
            setup.Session.AssertComplete();
        }
    }

    [Test]
    public async Task Changed_vehicle_design_disables_confirm_until_requote()
    {
        var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
            new(new("refit-target"), new("refit-composition"), new DesignKind.Vehicle(SizeClass.Medium, 2),
                [], 100, 1, 1, new(1), [], [], 0, [], true, []), []);
        var draft = new DesignDraft(new("已保存改装", ""), new DesignSpecification.Vehicle(SizeClass.Medium), new([], [], []));
        var saved = new SavedContentDraft(new(Guid.Parse("00000000-0000-0000-0000-000000000001")), new(1),
            new DraftTarget.Instance(new InstanceId.Vehicle(new(9))), new DraftPayload.Design(draft));
        var change = new DesignChangeProjection(new("fixture-vehicle"), new("fixture-composition"), design, [],
            [new(EconomyFixture.Food, 5)], [new(EconomyFixture.Equipment, 7, [], [], [])], 3);
        var before = new ServiceView(expectedDesign: design);
        var content = EconomyFixture.Content(drafts: [saved]);
        var setup = new HostSimulationTests.HostSetup(before, content);
        using var candidate = new ScriptedContentCandidate(EconomyFixture.Content(generation: 2, drafts: [saved]), content,
            ContentChange.Persistent, edit: new PreparedContentEdit.InstanceTarget(new(change, [], false, [])));
        await Run(setup, async (ui, economy) =>
        {
            setup.ContentFactory!.PrepareEditCall = (_, _) => new ContentResult<IContentCandidate>.Success(candidate);
            await Services(economy);
            await _input.Choose(Find<OptionButton>(economy, "ServiceOperation"), 2);
            await _input.Choose(Find<OptionButton>(economy, "ServiceFacility"), 0);
            await _input.Choose(Find<OptionButton>(economy, "ServiceVehicle"), 1);
            await _input.Choose(Find<OptionButton>(economy, "ServiceTargetKind"), 1);
            await _input.Choose(Find<OptionButton>(economy, "ServiceDraft"), 1);
            await _input.Activate(Find<Button>(economy, "RefreshServiceQuote"));
            Require(!Find<Button>(economy, "ConfirmService").Disabled, "设计未变化时应可确认改装。");
            var host = ui.GetParent<Train.App.App>().Host!;
            economy.Refresh(host.GetStatus()!.Binding, new ServiceView(expectedDesign: design, changedDesign: true), host.Content!);
            Require(Find<Label>(economy, "ServiceQuote").Text.Contains("车辆设计已变化") && Find<Button>(economy, "ConfirmService").Disabled,
                "车辆设计变化后仍可确认或未说明原因。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Vehicle_entry_opens_service_page_and_preselects_saved_draft()
    {
        var draft = new DesignDraft(new("入口草稿", ""), new DesignSpecification.Vehicle(SizeClass.Medium), new([], [], []));
        var saved = new SavedContentDraft(new(Guid.Parse("00000000-0000-0000-0000-00000000000a")), new(1),
            new DraftTarget.Instance(new InstanceId.Vehicle(new(10))), new DraftPayload.Design(draft));
        var setup = new HostSimulationTests.HostSetup(new ServiceView(), EconomyFixture.Content(drafts: [saved]));
        await Run(setup, async (ui, economy) =>
        {
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Fleet"));
            await _input.ClickItem(ui.GetNode<ItemList>("Root/World/WorldPanels/FleetList/Layout/Content/Body/Objects"), 0);
            await _input.Activate(panels.FindChildren("VehicleServices", "Button", true, false).OfType<Button>().Single(button => button.Text.Contains("车辆 10")));
            var tabs = Find<TabContainer>(economy, "Pages");
            Require(tabs.GetTabTitle(tabs.CurrentTab) == "车辆服务", "车辆入口没有打开车辆服务页。");
            Require(Find<OptionButton>(economy, "ServiceTargetKind").Selected == 1, "没有预选已保存草稿。");
            var drafts = Find<OptionButton>(economy, "ServiceDraft");
            Require(drafts.Selected > 0 && drafts.GetItemText(drafts.Selected).Contains("入口草稿"), "已保存草稿没有预选。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Accepted_design_activation_keeps_other_panels_until_reload()
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
        var before = new ServiceView(expectedDesign: design);
        var setup = new HostSimulationTests.HostSetup(before, content);
        using var candidate = new ScriptedContentCandidate(activated, content, ContentChange.Persistent,
            edit: new PreparedContentEdit.ConstructionTarget(design));
        await Run(setup, async (ui, economy) =>
        {
            setup.ContentFactory!.PrepareEditCall = (_, _) => new ContentResult<IContentCandidate>.Success(candidate);
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Fleet"));
            await _input.ClickItem(ui.GetNode<ItemList>("Root/World/WorldPanels/FleetList/Layout/Content/Body/Objects"), 0);
            var stop = panels.FindChildren("Stop", "Button", true, false).OfType<Button>().Single();
            Require(!stop.Disabled, "列车详情在制造前不可停车。");
            await Services(economy);
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
            Require(panels.HasNode("FleetList") && panels.HasNode("NodesList"), "接受设计目标后关闭了仍打开的列表。");
            Require(!stop.Disabled && !Find<Label>(economy, "State").Text.Contains("已不存在"), "接受设计目标后其他面板或车辆服务页失效。");
            setup.Session.QueueSubmit(new CoreCommand.StopTrain(new(7)), CommandSource.UserInterface,
                Completed(2, before, before, new CommandDecision.Accepted([], [])), before);
            stop.GrabFocus();
            await _input.PressKey(Key.Space);
            Require(setup.Session.SubmitCount == 2, "设计激活后列车详情不能提交停车。");
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
            Require(!panels.HasNode("FleetList") && stop.Disabled, "内容重载后旧列表或列车详情仍然有效。");
            var submitted = setup.Session.SubmitCount;
            stop.GrabFocus();
            await _input.PressKey(Key.Space);
            Require(setup.Session.SubmitCount == submitted, "内容重载后旧列车详情仍提交了停车。");
        });
        setup.Session.AssertComplete();
    }

    private async Task SelectRepair(EconomyPanels economy)
    {
        await _input.Choose(Find<OptionButton>(economy, "ServiceOperation"), 1);
        await _input.Choose(Find<OptionButton>(economy, "ServiceFacility"), 0);
        await _input.Choose(Find<OptionButton>(economy, "ServiceVehicle"), 1);
    }

    private async Task Services(EconomyPanels economy)
    {
        var tabs = Find<TabContainer>(economy, "Pages");
        var index = Enumerable.Range(0, tabs.GetTabCount()).Single(i => tabs.GetTabTitle(i) == "车辆服务");
        var bar = tabs.GetTabBar();
        await _input.Frames(2);
        await _input.Click(bar.GlobalPosition + bar.GetTabRect(index).GetCenter());
        Require(tabs.CurrentTab == index, "真实点击未打开车辆服务。");
        await _input.Frames();
    }

    private async Task Run(HostSimulationTests.HostSetup setup, Func<GameUi, EconomyPanels, Task> body)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.Activate(ui.GetNode<Button>("Root/World/Bar/TopBar/Nodes"));
            await _input.ClickItem(ui.GetNode<ItemList>("Root/World/WorldPanels/NodesList/Layout/Content/Body/Objects"), 0);
            await _input.Activate(Find<Button>(ui.GetNode<WorldPanels>("Root/World/WorldPanels"), "Economy"));
            await body(ui, ui.GetNode<EconomyPanels>("Root/World/EconomyPanels"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private sealed class ServiceView(ulong revision = 1, bool blocked = false, bool nullPayment = false, bool completed = false, bool missingFacility = false, DesignInput? expectedDesign = null, bool owned = false, bool paused = false, bool changedDesign = false) : ICoreReadView
    {
        private readonly ICoreReadView basis = EconomyFixture.Create(revision, vehicle: EconomyFixture.VehicleState.Owned);
        private static readonly FacilityKey Facility = new(new(20), CapabilityKind.Construction);
        private static readonly ImmutableArray<CoreProblem> Missing = [new("insufficient_goods", "materials", null, ["fixture:missing"])];
        internal int QuoteCount { get; private set; }
        internal JobQuote? LastQuote { get; private set; }
        public Lookup<SettlementView> FindSettlement(SettlementId id)
        {
            if (basis.FindSettlement(id) is not Lookup<SettlementView>.Found found) return basis.FindSettlement(id);
            return new Lookup<SettlementView>.Found(found.Value with { Relation = owned ? SettlementRelation.PlayerOwned : found.Value.Relation, Plots = missingFacility ? [] : [new(new(1), 1, PlotSize.Small, [], new PlotOccupant.Building(new(20))), new(new(2), 2, PlotSize.Small, [], new PlotOccupant.Building(new(21)))], CommissionJobs = completed || owned ? [] : [new(4)], OwnedJobs = owned && !completed ? [new(4), new(5)] : [] });
        }
        public Lookup<JobView> FindJob(JobId id) => id.Value is 4 or 5 && (id.Value == 4 || owned)
            ? new Lookup<JobView>.Found(new JobView.Construction(new(id, owned ? ConstructionKind.Deconstruction : ConstructionKind.Repair, new(1), Facility,
                new ConstructionTarget.Existing(new InstanceId.Vehicle(new(9)), new("fixture-vehicle")), id.Value == 4 && paused ? ConstructionState.Paused : ConstructionState.ResourceBlocked,
                0, 100, revision == 1 ? 20 : 60, revision == 1 ? .2 : .6, 3,
                revision == 1 ? [WorkLimit.Labor] : [WorkLimit.Power], [], Missing)))
            : throw new InvalidOperationException("UNCONFIGURED: FindJob");
        public CoreResult<JobQuote> GetJobQuote(JobRequest request, JobFunding funding)
        {
            var sources = request switch
            {
                JobRequest.Repair repair when repair.Facility == Facility && repair.Instance is InstanceId.Vehicle { Id.Value: 9 or 10 } => repair.Sources,
                JobRequest.Dismantle dismantle when owned && dismantle.Facility == Facility && dismantle.Instance is InstanceId.Vehicle { Id.Value: 9 } => [],
                JobRequest.Refit refit when refit.Facility == Facility && expectedDesign is not null && ReferenceEquals(refit.Change.Target, expectedDesign) => refit.Sources,
                JobRequest.BuildVehicle build when build.Facility == Facility && expectedDesign is not null && ReferenceEquals(build.Design, expectedDesign) => build.Sources,
                _ => throw new InvalidOperationException("UNCONFIGURED: GetJobQuote request")
            };
            if (funding != (owned ? JobFunding.OwnInventory : JobFunding.NpcCommission)) throw new InvalidOperationException("UNCONFIGURED: GetJobQuote funding");
            QuoteCount++;
            LastQuote = new(request, funding, Revision, 100, [new(EconomyFixture.Food, 7)],
                [.. sources.Select(id => new JobMaterialSourceView(id, [new(EconomyFixture.Food, 2)]))],
                [new(EconomyFixture.Food, 3)], new(500), new(231), nullPayment ? null : new(999),
                blocked ? [.. Missing, new("insufficient_capacity", "queue", null, ["fixture:capacity"])] : []);
            return new CoreResult<JobQuote>.Success(LastQuote);
        }
        public SimulationPosition Position => basis.Position;
        public CoreRevision Revision => basis.Revision;
        public CoreInputId InputIdentity => basis.InputIdentity;
        public CoreFault? Fault => basis.Fault;
        public WorldSummary World => basis.World;
        public EconomyView Economy => basis.Economy;
        public Lookup<NodeView> FindNode(NodeId id) => basis.FindNode(id);
        public Lookup<EdgeView> FindEdge(EdgeId id) => basis.FindEdge(id);
        public Lookup<LocationView> FindLocation(WorldLocation location) => basis.FindLocation(location);
        public Lookup<TrainView> FindTrain(TrainId id) => basis.FindTrain(id);
        public Lookup<InstanceView> FindInstance(InstanceId id)
        {
            if (changedDesign && id is InstanceId.Vehicle { Id.Value: 9 } && basis.FindInstance(id) is Lookup<InstanceView>.Found vehicle)
                return new Lookup<InstanceView>.Found(vehicle.Value with { Design = new("changed-vehicle") });
            if (id is InstanceId.Building { Id.Value: 21 })
                return new Lookup<InstanceView>.Found(new(id, new("fixture-plain-building"), new("fixture-plain-composition"),
                    new WorldLocation.Node(new(1)), SettlementRelation.PlayerOwned, new InstanceDetails.Building(new(2), PlotSize.Small),
                    InstanceLock.None, new(100, 100), [], []));
            if (id is InstanceId.Building { Id.Value: 20 })
                return new Lookup<InstanceView>.Found(new(id, new("fixture-service-building"), new("fixture-building-composition"),
                    new WorldLocation.Node(new(1)), owned ? SettlementRelation.PlayerOwned : SettlementRelation.NonHostile,
                    new InstanceDetails.Building(new(1), PlotSize.Small), InstanceLock.None, new(100, 100), [], []));
            return basis.FindInstance(id);
        }
        public Lookup<InventoryView> FindInventory(InventoryId id) => basis.FindInventory(id);
        public Lookup<LeaseView> FindLease(LeaseId id) => basis.FindLease(id);
        public Lookup<FacilityView> FindFacility(FacilityKey key) => basis.FindFacility(key);
        public Lookup<CombatView> FindCombat(CombatId id) => basis.FindCombat(id);
        public Lookup<CreatureGroupView> FindCreatureGroup(CreatureGroupId id) => basis.FindCreatureGroup(id);
        public Lookup<CreatureView> FindCreature(CreatureId id) => basis.FindCreature(id);
        public Lookup<LairView> FindLair(LairId id) => basis.FindLair(id);
        public CoreResult<TradeQuote> QuoteTrade(TradeQuoteRequest request) => basis.QuoteTrade(request);
        public CoreResult<VehicleTradeQuote> QuoteVehicle(VehicleTradeQuoteRequest request) => basis.QuoteVehicle(request);
        public CoreResult<LeaseQuote> QuoteLease(LeaseRequest request) => basis.QuoteLease(request);
        public CoreResult<SettlementFoundingPreview> PreviewSettlement(NodeId node) => basis.PreviewSettlement(node);
    }
}
