using System.Runtime.InteropServices;
using Train.Scenes.Contracts;

namespace Train.App;

internal sealed class LinuxAllocator(Action<Exception> reportFailure, Action? trim = null)
{
    private static int _running;
    private (SceneSessionId, SceneRequestId)? _trimmedOpening;

    [DllImport("libc.so.6", EntryPoint = "malloc_trim", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Trim(nuint padding);

    internal Task? TryTrim(SceneFrameStamp frame, SceneReadiness readiness, bool packingDone)
    {
        if ((trim is null && !OperatingSystem.IsLinux()) || readiness != SceneReadiness.Ready || !packingDone)
            return null;
        var opening = (frame.Session, frame.Request);
        if (_trimmedOpening == opening || Interlocked.CompareExchange(ref _running, 1, 0) != 0) return null;
        _trimmedOpening = opening;
        return Task.Run(() =>
        {
            try
            {
                if (trim is not null) trim();
                else Trim(0);
            }
            catch (Exception error) { reportFailure(error); }
            finally { Volatile.Write(ref _running, 0); }
        });
    }
}
