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
    public async Task Owned_deconstruction_pause_and_resume_follow_current_owned_jobs()
    {
        var before = new ServiceView(owned: true);
        var paused = new ServiceView(2, owned: true, paused: true);
        var gone = new ServiceView(3, owned: true, completed: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (_, economy) =>
        {
            await Vehicles(economy);
            Require(Find<Label>(Find<Control>(economy, "ConstructionJob_4"), "JobDescription").Text.Contains("拆解") && Find<Label>(Find<Control>(economy, "ConstructionJob_5"), "JobDescription").Text.Contains("拆解"),
                "自有拆解任务没有从OwnedJobs展示。");
            Require(Find<Button>(economy, "PauseConstructionJob_5").IsVisibleInTree(), "第二个拆解任务没有独立暂停入口。");
            setup.Session.QueueSubmit(new CoreCommand.SetDeconstructionPaused(new(4), true), CommandSource.UserInterface,
                Completed(1, before, paused, new CommandDecision.Accepted([], [])), paused);
            await _input.Activate(Find<Button>(economy, "PauseConstructionJob_4"));
            Require(Find<Label>(Find<Control>(economy, "ConstructionJob_4"), "JobDescription").Text.Contains("已暂停") && !Find<Label>(Find<Control>(economy, "ConstructionJob_5"), "JobDescription").Text.Contains("已暂停"),
                "暂停没有只作用于所选任务。");
            setup.Session.QueueSubmit(new CoreCommand.SetDeconstructionPaused(new(4), false), CommandSource.UserInterface,
                Completed(2, paused, gone, new CommandDecision.Accepted([], [])), gone);
            await _input.Activate(Find<Button>(economy, "PauseConstructionJob_4"));
            await _input.Frames(2);
            Require(!economy.FindChildren("ConstructionJob_4", "", true, false).Any(), "任务离开OwnedJobs后仍被缓存展示。");
            Require(before.QuoteCount == 0 && paused.QuoteCount == 0 && gone.QuoteCount == 0, "任务操作隐式获取报价。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Building_dismantle_lists_target_reservation_and_pause_without_other_job_controls()
    {
        var before = new ServiceView(owned: true, buildingWork: true);
        var paused = new ServiceView(2, owned: true, buildingWork: true, buildingPaused: true);
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content());
        await Run(setup, async (_, economy) =>
        {
            await Buildings(economy);
            var dismantle = Find<Label>(Find<Control>(economy, "ConstructionJob_6"), "JobDescription").Text;
            Require(dismantle.Contains("拆解") && dismantle.Contains("普通建筑") && dismantle.Contains("聚居地库存") && dismantle.Contains("容量 40") &&
                Find<Label>(Find<Control>(economy, "ConstructionJob_7"), "JobDescription").Text.Contains("预留：无") &&
                Find<Label>(Find<Control>(economy, "ConstructionJob_9"), "JobDescription").Text.Contains("地块"),
                "任务行没有列出建筑、地块目标或匹配的库存预留。");
            Require(Find<Button>(economy, "PauseConstructionJob_6").Text == "暂停拆解" &&
                !economy.FindChildren("PauseConstructionJob_7", "Button", true, false).Any() &&
                !economy.FindChildren("PauseConstructionJob_8", "Button", true, false).Any() &&
                !economy.FindChildren("PauseConstructionJob_9", "Button", true, false).Any(),
                "修理、改装或建造任务出现了暂停入口。");
            setup.Session.QueueSubmit(new CoreCommand.SetDeconstructionPaused(new(6), true), CommandSource.UserInterface,
                Completed(1, before, paused, new CommandDecision.Accepted([], [])), paused);
            await _input.Activate(Find<Button>(economy, "PauseConstructionJob_6"));
            Require(setup.Session.SubmitCount == 1 && Find<Label>(Find<Control>(economy, "ConstructionJob_6"), "JobDescription").Text.Contains("已暂停") &&
                !Find<Label>(Find<Control>(economy, "ConstructionJob_7"), "JobDescription").Text.Contains("已暂停"), "建筑拆解暂停没有提交或作用到了其他任务。");
        });
        setup.Session.AssertComplete();
    }

}
