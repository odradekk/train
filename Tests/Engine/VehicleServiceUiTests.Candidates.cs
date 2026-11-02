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

public partial class VehicleServiceUiTests
{
    [Test]
    public async Task Closing_confirmable_manufacture_releases_candidate_without_activation_or_submission()
    {
        foreach (var closeButton in new[] { true, false })
        {
            var design = new DesignInput(Train.Core.ContractSupport.HostCoreScenario.Input,
                new(new("close-design"), new("close-composition"), new DesignKind.Vehicle(SizeClass.Small, 2),
                    [], 10, 1, 1, new(1), [], [], 0, [], true, []), []);
            var draft = new DesignDraft(new("可制造蓝图", ""), new DesignSpecification.Vehicle(SizeClass.Small), new([], [], []));
            var evaluation = new DesignEvaluation.Complete(design.Design.Design, design.Design.Composition, draft, design,
                new(null, null, null, true, null, null, [], [], [], [], []), true, []);
            var blueprint = new BlueprintRecord(new(BlueprintLibrary.Local, new("close-blueprint")), draft, [], evaluation);
            var content = EconomyFixture.Content(vehicleBlueprint: blueprint);
            var view = new ServiceView(expectedDesign: design);
            var setup = new HostSimulationTests.HostSetup(view, content);
            using var candidate = new ScriptedContentCandidate(content, content, ContentChange.Persistent,
                edit: new PreparedContentEdit.ConstructionTarget(design));
            await Run(setup, async (ui, panel) =>
            {
                setup.ContentFactory!.PrepareEditCall = (_, _) => new ContentResult<IContentCandidate>.Success(candidate);
                var work = await OpenManufacture(ui, panel);
                await _input.Activate(Find<Button>(work, "Blueprint_0"));
                Require(view.QuoteCount > 0 && !Find<Button>(work, "ConfirmWork").Disabled, "关闭测试必须先有可确认报价。");
                if (closeButton) await _input.Activate(Find<Button>(work, "CloseWork"));
                else work.Hide();
                await _input.Frames(2);
                Require(!work.IsVisibleInTree() && candidate.Disposals == 1 && candidate.Activations == 0 && setup.Session.SubmitCount == 0,
                    "关闭或隐藏可确认工作区应释放候选，不得提交或激活。");
            });
            setup.Session.AssertComplete();
        }
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
            var work = await OpenManufacture(ui, economy);
            Require(preparations == 0 && before.QuoteCount == 0, "未选择蓝图应保持只读。");
            await _input.Activate(Find<Button>(work, "Blueprint_0"));
            Require(preparations == 1 && candidate.Activations == 0 && ReferenceEquals(ui.GetParent<Train.App.App>().Host!.Content, content),
                "选择蓝图应准备报价但不得激活内容候选。");
            var quote = before.LastQuote!;
            Require(quote.Request is JobRequest.BuildVehicle build && ReferenceEquals(build.Design, design),
                "UI未使用内容候选提供的DesignInput。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(quote.Request, new(999)), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
            await _input.Activate(Find<Button>(work, "ConfirmWork"));
            await _input.WaitUntil(() => candidate.Activations == 1);
            Require(setup.Session.SubmitCount == 1 && ReferenceEquals(ui.GetParent<Train.App.App>().Host!.Content, target),
                "制造接受后没有激活同一候选。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Rejected_build_keeps_the_candidate_inactive_and_does_not_retry()
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
            var work = await OpenManufacture(ui, economy);
            Require(preparations == 0 && before.QuoteCount == 0, "未选择蓝图应保持只读。");
            await _input.Activate(Find<Button>(work, "Blueprint_0"));
            Require(preparations == 1 && candidate.Activations == 0 && ReferenceEquals(ui.GetParent<Train.App.App>().Host!.Content, content),
                "选择蓝图应准备报价但不得激活内容候选。");
            var quote = before.LastQuote!;
            Require(quote.Request is JobRequest.BuildVehicle build && ReferenceEquals(build.Design, design),
                "UI未使用内容候选提供的DesignInput。");
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(quote.Request, new(999)), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Rejected([new("price_changed", "MaximumPayment", null, [])])), before);
            var quotes = before.QuoteCount;
            await _input.Activate(Find<Button>(work, "ConfirmWork"));
            await _input.Frames(3);
            Require(setup.Session.SubmitCount == 1 && candidate.Activations == 0 && before.QuoteCount == quotes &&
                ReferenceEquals(ui.GetParent<Train.App.App>().Host!.Content, content) && Find<Button>(work, "ConfirmWork").Disabled,
                "拒绝制造后激活候选、自动重试或仍允许重复提交。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Persistent_content_change_invalidates_quote_and_reprepares_same_blueprint_automatically()
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
            var work = await OpenManufacture(ui, economy);
            await _input.Activate(Find<Button>(work, "Blueprint_0"));
            Require(preparations == 1 && before.QuoteCount == 1 && !Find<Button>(work, "ConfirmWork").Disabled, "初次报价未完成。");
            var host = ui.GetParent<Train.App.App>().Host!;
            Require(host.BeginContentEdit(new ContentEdit.SaveLocalDraft(saved)).Problem is null, "无法开始持久内容变更。");
            await _input.WaitUntil(() => !host.IsBusy && ReferenceEquals(host.Content, changed));
            await _input.Frames(2);
            Require(changed.Generation == content.Generation && preparations == 2 && before.QuoteCount == 2 &&
                !Find<Button>(work, "ConfirmWork").Disabled, "同Generation内容变更未自动按新基准准备报价。");
            Require(oldCandidate.Activations == 0, "旧候选被提前激活。");
            var quote = before.LastQuote!;
            setup.Session.QueueSubmit(new CoreCommand.CommissionVehicleWork(quote.Request, new(999)), CommandSource.UserInterface,
                Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
            await _input.Activate(Find<Button>(work, "ConfirmWork"));
            Require(setup.Session.SubmitCount == 1 && newCandidate.Activations == 1 && oldCandidate.Activations == 0 &&
                ReferenceEquals(host.Content, target), "重报价后未提交当前基准候选。");
        });
        setup.Session.AssertComplete();
    }

}
