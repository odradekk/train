using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class OwnedSettlementInspectorUiTests
{
    [Test]
    public async Task Sites_use_plot_construction_and_share_building_name_size_progress_and_time_on_both_pages()
    {
        var setup = new HostSimulationTests.HostSetup(View(includeVehicleJob: true), Content());
        await Run(setup, async (_, panel) =>
        {
            var summary = Find<Label>(panel, "OwnedSites").Text;
            Require(summary.Contains("正在建造食品建筑") && summary.Contains("小地块") && summary.Contains("50%") && summary.Contains("10 秒") &&
                summary.Split('\n').Length == 1, "概况工地应只包含地块上正在建造的建筑，不包含车辆施工任务。");
            var tabs = Find<TabContainer>(panel, "Pages");
            await _input.ClickTab(tabs, 2);
            Require(panel.FindChildren("ConstructionJob_90", "", true, false).Count == 1,
                "建筑页应只显示一条工地任务。");
            var description = Find<Label>(Find<Control>(panel, "ConstructionJob_90"), "JobDescription").Text;
            Require(description.Contains("食品建筑") && description.Contains("50%") && description.Contains("10 秒"),
                "建筑页任务应显示建筑、进度和剩余时间。");
            Require(!panel.FindChildren("OtherPlots", "Label", true, false).Cast<Label>().Any(label => label.IsVisibleInTree() && label.Text.Length > 0),
                "建筑页不应再用工地汇总重复显示任务。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Site_with_missing_design_uses_the_same_readable_fallback_on_both_pages()
    {
        var setup = new HostSimulationTests.HostSetup(View(siteDesign: new("fixture-vehicle")), Content());
        await Run(setup, async (_, panel) =>
        {
            var summary = Find<Label>(panel, "OwnedSites").Text;
            Require(summary.Contains("正在建造建筑内容缺失") && !summary.Contains("fixture-vehicle"), "工地设计缺失时应说明内容缺失，不显示哈希。");
            await _input.ClickTab(Find<TabContainer>(panel, "Pages"), 2);
            var description = Find<Label>(Find<Control>(panel, "ConstructionJob_90"), "JobDescription").Text;
            Require(description.Contains("建筑内容缺失") && !description.Contains("fixture-vehicle"),
                "建筑页任务应显示可读的缺失设计原因。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Locked_building_explains_repair_refit_and_demolition_without_combat_or_internal_codes()
    {
        var setup = new HostSimulationTests.HostSetup(View(stoppedState: ProductionState.Locked), Content());
        await Run(setup, async (_, panel) =>
        {
            await _input.ClickTab(Find<TabContainer>(panel, "Pages"), 2);
            await _input.Activate(Find<Button>(panel, "SelectBuilding_2"));
            var building = Find<VBoxContainer>(panel, "Building_2");
            var state = Find<Label>(building, "State").Text;
            var reason = Find<Label>(building, "Blockers").Text;
            Require(state.Contains("改装") && state.Contains("修理") && state.Contains("拆除") && !state.Contains("交战"),
                "锁定状态应只说明核心实际锁住建筑的施工类型。");
            Require(reason.Contains("自动恢复") && reason.Contains("不会") && reason.Contains("生产") &&
                !reason.Contains("交战") && !reason.Contains("instance_locked") && !reason.Contains("Facility"),
                "处理办法应区分修理改装后的恢复与拆除后的结果，并使用中文原因。");
        });
        setup.Session.AssertComplete();
    }
}
