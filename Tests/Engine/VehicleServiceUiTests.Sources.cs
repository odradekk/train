using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class VehicleServiceUiTests
{
    [Test]
    public async Task Only_platform_eligible_train_sources_are_selected_by_default()
    {
        foreach (var atPlatform in new[] { false, true })
        {
            var view = new ServiceView(trainAtPlatform: atPlatform);
            var setup = new HostSimulationTests.HostSetup(view, EconomyFixture.Content());
            await Run(setup, async (ui, _) =>
            {
                var work = await OpenRepair(ui, new InstanceId.Vehicle(new(9)));
                var train = Find<CheckBox>(work, "Source_12");
                Require(train.ButtonPressed == atPlatform && (atPlatform || train.Text.Contains("未停靠站台")),
                    "只有停靠站台且正在装卸的列车才能默认勾选，其他列车应解释未停靠站台。");
                Require(view.LastQuote!.Request is JobRequest.Repair repair && repair.Sources.SequenceEqual(atPlatform
                    ? new InventoryId[] { new(12), new(11) } : new InventoryId[] { new(11) }),
                    "默认报价包含了未停靠站台的列车。");
            });
            setup.Session.AssertComplete();
        }
    }
}
