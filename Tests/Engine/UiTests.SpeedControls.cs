using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class UiTests
{
    [Test]
    public async Task Speed_buttons_preserve_focus_pause_and_select_only_the_current_control()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var setup = new HostSimulationTests.HostSetup(UiWorldFixture.Map());
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            var host = app.Host!;
            host.AutoPauseOnFocusLoss = true;
            host.SetFocused(false);
            var bar = app.GetNode<GameUi>("GameUi").GetNode<Control>("Root/World/Bar/TopBar");
            var controls = new[] { "Pause", "Speed1", "Speed2", "Speed4", "Speed8" }
                .Select(name => bar.GetNode<Button>(name)).ToArray();
            controls[2].GrabFocus();
            await _input.PressKey(Key.Space);
            var status = host.GetStatus()!;
            Require(status.Speed == SimulationSpeed.Two && !status.PlayerPaused && status.FocusPaused &&
                controls[2].ButtonPressed && controls.Count(button => button.ButtonPressed) == 1,
                "真实倍速输入未解除玩家暂停、保留失焦暂停并单选当前速度。");
            var clock = bar.GetNode<Label>("Clock");
            await _input.WaitUntil(() => clock.Text.Contains("已暂停（失焦）") && long.TryParse(Find<Label>(bar, "Wallet").Text.Split(' ')[^1], out var wallet) && wallet == status.View.Economy.Wallet.Value);
            controls[0].GrabFocus();
            await _input.PressKey(Key.Space);
            status = host.GetStatus()!;
            Require(status.PlayerPaused && status.FocusPaused && controls[0].ButtonPressed &&
                controls.Count(button => button.ButtonPressed) == 1 && setup.Session.AdvanceCount == 0,
                "真实暂停输入未保留失焦暂停，或保护期间仍推进模拟。");
            setup.Session.AssertComplete();
        }
        finally { await fixture.Cleanup(); }
    }

}
