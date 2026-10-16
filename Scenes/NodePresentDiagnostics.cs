#if DEBUG
using System.Diagnostics;

namespace Train.Scenes;

internal static class NodePresentDiagnostics
{
    internal static long Calls;
    internal static long Ticks;
    internal static long LastTicks;
    internal static ulong LastNode;

    internal readonly struct Measurement(ulong node) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        public void Dispose()
        {
            LastTicks = Stopwatch.GetTimestamp() - started;
            Ticks += LastTicks;
            Calls++;
            LastNode = node;
        }
    }
}
#endif
