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
            await Run(setup, async (ui, economy) =>
            {
                setup.ContentFactory!.PrepareEditCall = (basis, edit) =>
                {
                    Require(ReferenceEquals(basis, content) && edit is ContentEdit.InstanceTarget request &&
                        request.Current == new DesignHash("fixture-vehicle") && ReferenceEquals(request.Target, draft), "改装未准备所选实例保存草稿。");
                    return new ContentResult<IContentCandidate>.Success(candidate);
                };
                Find<EconomyPanels>(ui, "EconomyPanels").OpenVehicleServices(new InstanceId.Vehicle(new(9)));
                await _input.Frames(2);
                var work = Find<Control>(ui, "ConstructionWorkspace");
                var drafts = Find<VBoxContainer>(work, "WorkChoices").FindChildren("Draft_*", "Button", true, false);
                Require(drafts.Count == 1 && ((Button)drafts[0]).Text.Contains("已保存改装"), "只应展示当前实例草稿。");
                Require(candidate.Activations == 0, "报价提前激活候选。");
                var quote = before.LastQuote!;
                if (!immediate) Require(quote.Request is JobRequest.Refit refit && ReferenceEquals(refit.Change, change) && refit.Instance == new InstanceId.Vehicle(new(9)), "改装未使用内容差量。");
                else Require(before.QuoteCount == 0, "立即应用不应获取施工报价。");
                Require(!Find<Button>(work, "ConfirmWork").Disabled, "有效草稿未提供统一确认入口。");
                CoreCommand command = immediate ? new CoreCommand.ApplyInstanceDesign(new InstanceId.Vehicle(new(9)), change)
                    : owned ? new CoreCommand.StartOwnedJob(quote.Request) : new CoreCommand.CommissionVehicleWork(quote.Request, new(999));
                setup.Session.QueueSubmit(command, CommandSource.UserInterface, Completed(1, before, before, new CommandDecision.Accepted([], [])), before);
                await _input.Activate(Find<Button>(work, "ConfirmWork"));
                Require(setup.Session.SubmitCount == 1 && candidate.Activations == 1, "改装接受后未提交或激活候选。");
                Require(Find<Button>(work, "EditWorkTarget").IsVisibleInTree(), "接受后没有保留继续编辑入口。");
            });
            setup.Session.AssertComplete();
        }
    }

    [Test]
    public async Task Changed_vehicle_design_reprepares_difference_from_current_design()
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
        var currentDesign = design with { Design = design.Design with { Design = new("changed-vehicle"), Composition = new("fixture-composition") } };
        var currentEvaluation = new DesignEvaluation.Complete(currentDesign.Design.Design, currentDesign.Design.Composition, draft, currentDesign,
            new(null, null, null, true, null, null, [], [], [], [], []), true, []);
        var content = EconomyFixture.Content(drafts: [saved], additionalDesigns:
            new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [currentDesign.Design.Design] = new ContentLookup<DesignEvaluation>.Found(currentEvaluation) });
        var setup = new HostSimulationTests.HostSetup(before, content);
        using var candidate = new ScriptedContentCandidate(EconomyFixture.Content(generation: 2, drafts: [saved]), content,
            ContentChange.Persistent, edit: new PreparedContentEdit.InstanceTarget(new(change, [], false, [])));
        var nextChange = change with { Before = new("changed-vehicle") };
        using var nextCandidate = new ScriptedContentCandidate(EconomyFixture.Content(generation: 2, drafts: [saved]), content,
            ContentChange.Persistent, edit: new PreparedContentEdit.InstanceTarget(new(nextChange, [], false, [])));
        var preparations = 0;
        await Run(setup, async (ui, economy) =>
        {
            setup.ContentFactory!.PrepareEditCall = (_, edit) =>
            {
                Require(edit is ContentEdit.InstanceTarget, "改装应从当前实例设计准备差量。");
                var request = (ContentEdit.InstanceTarget)edit;
                preparations++;
                Require(request.Current == new DesignHash(preparations == 1 ? "fixture-vehicle" : "changed-vehicle"),
                    "实例设计变化后仍按旧设计准备改装差量。");
                return new ContentResult<IContentCandidate>.Success(preparations == 1 ? candidate : nextCandidate);
            };
            Find<EconomyPanels>(ui, "EconomyPanels").OpenVehicleServices(new InstanceId.Vehicle(new(9)));
            await _input.Frames(2);
            var work = Find<Control>(ui, "ConstructionWorkspace");
            Require(!Find<Button>(work, "ConfirmWork").Disabled, "设计未变化时应可确认改装。");
            var host = ui.GetParent<Train.App.App>().Host!;
            var next = new ServiceView(2, expectedDesign: design, changedDesign: true);
            setup.Session.QueueAdvance(new(1, next.Position, [], null, null), next);
            Require(host.Step(1).Problem is null, "无法发布实例设计变化。");
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(host.GetStatus()!.Binding, next, host.Content!);
            Require(preparations == 2 && next.LastQuote?.Request is JobRequest.Refit refit &&
                ReferenceEquals(refit.Change, nextChange) && !Find<Button>(work, "ConfirmWork").Disabled &&
                candidate.Disposals == 1 && candidate.Activations == 0 && nextCandidate.Activations == 0,
                "实例设计变化后应释放旧候选，并按当前设计重新准备可确认报价。");
        });
        setup.Session.AssertComplete();
    }

}
