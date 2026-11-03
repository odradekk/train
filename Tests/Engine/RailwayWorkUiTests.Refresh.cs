using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class RailwayWorkUiTests
{
    [Test]
    public async Task Existing_automatic_repair_cannot_be_submitted_as_manual_repair()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.Create(), RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var repair = RepairButtons(panel).Single();
            Require(repair.Disabled, "已有未开工维修时仍能重复手动修复。");
            repair.EmitSignal(BaseButton.SignalName.Pressed);
            Require(setup.Session.SubmitCount == 0, "检查既有维修提交了命令。");
            var description = repair.GetParent().GetNode<Label>("Description").Text;
            Require(!repair.Visible && description.Contains("正在修复") && !description.Contains("当前操作尚未结束")
                && !description.Contains("需要：") && !description.Contains("来源：")
                && !description.Contains("库存") && !description.Contains("工期"),
                "已有修复任务的断点应明确说明正在修复，隐藏重复开工信息和按钮：" + description);
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Cancelling_automatic_repair_before_choosing_a_source_submits_nothing()
    {
        var initial = RailwayWorkUiFixture.WithStock(0, automaticRepair: false);
        var setup = new HostSimulationTests.HostSetup(initial, RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var toggle = Find<CheckButton>(panel, "AutomaticRepair");
            await _input.Activate(toggle);
            Require(Find<OptionButton>(panel, "AutomaticRepairSource").Visible, "打开自动修复没有显示来源选择。");
            await _input.Activate(toggle);
            Require(!toggle.ButtonPressed && !Find<OptionButton>(panel, "AutomaticRepairSource").Visible,
                "取消待启用的自动修复没有隐藏来源选择。");
            Require(setup.Session.SubmitCount == 0 && Find<Label>(panel, "Feedback").Text.Length == 0,
                "没有选来源就关闭开关，仍提交了命令或显示受理反馈。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Building_a_line_submits_its_source_and_shows_a_chinese_rejection()
    {
        var initial = RailwayWorkUiFixture.Create();
        var setup = new HostSimulationTests.HostSetup(initial, RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 0);
            var row = Find<VBoxContainer>(panel, "AddTrackRow");
            var description = row.GetNode<Label>("Description").Text;
            Require(description.Contains("需要：夹具钢轨 × 17") && description.Contains("来源：自有镇")
                && description.Contains("来源库存：夹具钢轨 × 18") && description.Contains("工期：25 分")
                && !description.Contains("90000") && !description.Contains("tick"),
                "建设说明没有显示预览材料、来源库存和可读工期：" + description);
            setup.Session.QueueSubmit(new CoreCommand.ExpandRail(new(RailwayWorkUiFixture.ZeroTrackEdge), new(1)),
                CommandSource.UserInterface, Completed(1, initial, initial,
                    new CommandDecision.Rejected([RailwayWorkUiFixture.Blocker()])), initial);
            await _input.Activate(Find<Button>(panel, "AddTrack"));
            await _input.Frames(2);
            var feedback = Find<Label>(panel, "Feedback").Text;
            Require(setup.Session.SubmitCount == 1 && feedback.Contains("货物不足") && feedback.Contains("夹具钢轨")
                && !feedback.Contains("insufficient_goods") && !feedback.Contains("voxel:") && !feedback.Contains("fixture:"),
                "建设拒绝没有显示中文材料反馈：" + feedback);
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Missing_rules_disable_work_without_inventing_materials_or_duration()
    {
        var setup = new HostSimulationTests.HostSetup(RailwayWorkUiFixture.MissingRules(), RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var descriptions = Find<VBoxContainer>(panel, "Breaks").FindChildren("Description", "Label", true, false)
                .Cast<Label>().Append(Find<VBoxContainer>(panel, "AddTrackRow").GetNode<Label>("Description")).ToArray();
            Require(descriptions.Length == 2 && descriptions.All(label => label.Text.Contains("材料需求不可用")
                && !label.Text.Contains("工期") && !label.Text.Contains("content_missing")),
                "缺少铁路规则时应说明材料不可用，不能显示虚构工期。");
            Require(RepairButtons(panel).Single().Disabled && Find<Button>(panel, "AddTrack").Disabled,
                "缺少铁路规则时仍能提交施工。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Ordinary_refresh_retains_source_scroll_and_keyboard_focus()
    {
        var initial = RailwayWorkUiFixture.WithManyBreaks();
        var next = RailwayWorkUiFixture.WithManyBreaks(2);
        var setup = new HostSimulationTests.HostSetup(initial, RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var source = Find<OptionButton>(panel, "MaterialSource");
            var scroll = panel.FindChildren("*", "ScrollContainer", true, false).Cast<ScrollContainer>().First();
            var focused = RepairButtons(panel)[^1];
            focused.GrabFocus();
            scroll.EnsureControlVisible(focused);
            await _input.Frames(2);
            var previousScroll = scroll.ScrollVertical;
            var host = ui.GetParent<Train.App.App>().Host!;
            setup.Session.QueueAdvance(new(1, next.Position, [], null, null), next);
            Require(host.Step(1).Problem is null, "无法发布普通修订。");
            Find<WorldPanels>(ui, "WorldPanels").Refresh(host.GetStatus()!.Binding, next, host.Content!);
            await _input.Frames(2);
            Require(source.Selected == 0 && focused.HasFocus() && previousScroll > 0 && scroll.ScrollVertical == previousScroll,
                "普通修订丢失了来源、键盘焦点或滚动位置。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Replacing_the_game_makes_an_old_repair_callback_harmless()
    {
        var initial = RailwayWorkUiFixture.ReadyToRepair();
        var setup = new HostSimulationTests.HostSetup(initial, RailwayWorkUiFixture.Content());
        var replacement = UiWorldFixture.Session(RailwayWorkUiFixture.ReadyToRepair());
        setup.ReplacementCandidate = new(replacement, HostCoreScenario.References);
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var oldRepair = RepairButtons(panel).Single();
            var host = ui.GetParent<Train.App.App>().Host!;
            var binding = host.GetStatus()!.Binding;
            Require(host.BeginRestore("opaque core", "opaque content", host.SessionOptions!).Problem is null, "替换对局请求失败。");
            await _input.WaitUntil(() => !host.IsBusy && host.GetStatus()!.Binding != binding);
            if (GodotObject.IsInstanceValid(oldRepair)) oldRepair.EmitSignal(BaseButton.SignalName.Pressed);
            await _input.Frames(2);
            Require(setup.Session.SubmitCount == 0 && replacement.SubmitCount == 0, "旧绑定的修复回调提交到了旧局或新局。");
        });
        setup.Session.AssertComplete();
        replacement.AssertComplete();
    }
    [Test]
    public async Task Content_reload_invalidates_the_old_repair_callback()
    {
        var initial = RailwayWorkUiFixture.ReadyToRepair();
        var setup = new HostSimulationTests.HostSetup(initial, RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            var oldRepair = RepairButtons(panel).Single();
            var host = ui.GetParent<Train.App.App>().Host!;
            setup.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(
                new FixedCoreCapture(new(initial.Position, "fixed", "fixed"), HostCoreScenario.References,
                    "opaque", initial.InputIdentity, initial.Revision)));
            var reloaded = RailwayWorkUiFixture.Content(generation: 2);
            using var candidate = new ScriptedContentCandidate(reloaded, host.Content!, ContentChange.Presentation);
            setup.ContentFactory!.PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Reload
                ? new ContentResult<IContentCandidate>.Success(candidate)
                : throw new InvalidOperationException("UNCONFIGURED: " + request.Purpose);
            Require(host.BeginReload(CommandSource.UserInterface).Problem is null, "内容重载请求失败。");
            await _input.WaitUntil(() => !host.IsBusy && ReferenceEquals(host.Content, reloaded));
            if (GodotObject.IsInstanceValid(oldRepair)) oldRepair.EmitSignal(BaseButton.SignalName.Pressed);
            await _input.Frames(2);
            Require(setup.Session.SubmitCount == 0, "旧内容代次的修复回调仍提交命令。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Losing_the_automatic_repair_source_before_selection_does_not_submit()
    {
        var initial = RailwayWorkUiFixture.WithStock(0, automaticRepair: false);
        var withoutSource = RailwayWorkUiFixture.WithoutSource();
        var setup = new HostSimulationTests.HostSetup(initial, RailwayWorkUiFixture.Content());
        await Run(setup, async ui =>
        {
            var panel = await OpenFromList(ui, 2);
            await _input.Activate(Find<CheckButton>(panel, "AutomaticRepair"));
            var source = Find<OptionButton>(panel, "AutomaticRepairSource");
            Require(source.IsVisibleInTree() && source.ItemCount == 2,
                "自动修复必须有占位项和一个来源，才能复现来源失效时的旧选择。");
            var host = ui.GetParent<Train.App.App>().Host!;
            setup.Session.QueueAdvance(new(1, withoutSource.Position, [], null, null), withoutSource);
            Require(host.Step(1).Problem is null, "无法发布材料来源失效的修订。");
            // 不推进 Godot 帧，模拟界面刷新前到达的旧下拉选项事件。
            source.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
            await _input.Frames(2);
            Require(setup.Session.SubmitCount == 0, "来源失效后的旧选择提交了自动修复命令。");
            Require(!Find<CheckButton>(panel, "AutomaticRepair").ButtonPressed &&
                Find<Label>(panel, "Feedback").Text.Contains("来源已不存在"), "来源失效后没有退出待启用状态并解释原因。");
        });
        setup.Session.AssertComplete();
    }

}
