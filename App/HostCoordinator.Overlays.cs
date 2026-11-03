namespace Train.App;

[Flags]
internal enum OverlayPauseSource { Saves = 1, Settings = 2, Authoring = 4 }

internal sealed partial class HostCoordinator
{
    private OverlayPauseSource _openOverlays;

    internal void SetOverlayPaused(OverlayPauseSource source, bool paused)
    {
        EnsureOwner();
        _openOverlays = paused ? _openOverlays | source : _openOverlays & ~source;
        _active?.Driver.SetOverlayPaused(_openOverlays != 0);
        _skipNextFrame = true;
    }
}
