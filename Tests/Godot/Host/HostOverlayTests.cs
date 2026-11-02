using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class HostOverlayTests
{
    [TestMethod]
    public void An_open_overlay_still_pauses_a_newly_bound_session()
    {
        using var fixture = new HostFixture();
        fixture.Host.SetOverlayPaused(OverlayPauseSource.Settings, true);
        fixture.ReadyHost();
        Assert.IsTrue(fixture.Host.GetStatus()!.OverlayPaused);
        fixture.Host.SetOverlayPaused(OverlayPauseSource.Settings, false);
        Assert.IsFalse(fixture.Host.GetStatus()!.OverlayPaused);
        fixture.AssertNoUnexpectedFailures();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Closing_one_overlay_keeps_the_other_and_existing_pause_reasons(bool settingsFirst)
    {
        var first = settingsFirst ? OverlayPauseSource.Settings : OverlayPauseSource.Saves;
        var second = settingsFirst ? OverlayPauseSource.Saves : OverlayPauseSource.Settings;
        using var fixture = new HostFixture();
        fixture.ReadyHost();
        var host = fixture.Host;
        host.AutoPauseOnFocusLoss = true;
        host.SetFocused(false);
        host.SetOverlayPaused(first, true);
        host.SetOverlayPaused(second, true);
        host.SetOverlayPaused(second, true);
        host.SetOverlayPaused(first, false);
        Assert.IsTrue(host.GetStatus()!.OverlayPaused);
        host.SetOverlayPaused(first, false);
        Assert.IsTrue(host.GetStatus()!.OverlayPaused);
        host.SetOverlayPaused(second, false);
        Assert.IsFalse(host.GetStatus()!.OverlayPaused);
        Assert.IsTrue(host.GetStatus()!.PlayerPaused);
        Assert.IsTrue(host.GetStatus()!.FocusPaused);
        fixture.AssertNoUnexpectedFailures();
    }
}
