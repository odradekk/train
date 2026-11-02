using Chickensoft.GoDotTest;
using Godot;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Long_good_names_remain_inside_their_market_rows()
    {
        var name = string.Concat(Enumerable.Repeat("长名称货物", 12));
        var setup = new HostSimulationTests.HostSetup(EconomyFixture.Create(),
            EconomyFixture.Content(mapPresentation: true, foodName: name));
        await Run(setup, async (_, panel) =>
        {
            await _input.ChooseTab(Find<TabContainer>(panel, "Pages"), 1);
            await _input.Frames(3);
            var row = panel.FindChildren("SelectGood", "Button", true, false).Cast<Button>().First();
            var label = row.FindChildren("GoodName", "Label", true, false).Cast<Label>().Single();
            Require(label.Text == name && label.GetLineCount() > 1, "长名称情景必须使用实际的多行货物名称。");
            Require(row.GetGlobalRect().Encloses(label.GetGlobalRect()) &&
                label.Size.Y >= label.GetLineCount() * label.GetLineHeight(), "货物名称被裁切或越过本行边界。");
        });
        setup.Session.AssertComplete();
    }
}
