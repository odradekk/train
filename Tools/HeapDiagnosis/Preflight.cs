namespace Train.HeapDiagnosis;

internal static class Preflight
{
    internal static RecordingService[] Root = [];

    internal static void Run(string ready, string release)
    {
        var binding = new HostBinding(Guid.NewGuid());
        var host = new HostCoordinator(new ActiveGroup(binding));
        Root = [new(host, null),
            new(host, new PendingWrite(new RecordingSnapshot(binding, new ContentView()), Task.CompletedTask)),
            new(host, new PendingWrite(new RecordingSnapshot(new HostBinding(Guid.NewGuid()), new ContentView()),
                new TaskCompletionSource().Task))];
        using var stop = new ManualResetEventSlim();
        var background = new Thread(() => stop.Wait(TimeSpan.FromSeconds(120))) { IsBackground = true };
        background.Start();
        File.WriteAllText(ready + ".partial", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(ready + ".partial", ready);
        var until = Environment.TickCount64 + 120_000;
        while (!File.Exists(release) && Environment.TickCount64 < until) Thread.Sleep(50);
        stop.Set();
        background.Join();
        GC.KeepAlive(Root);
    }
}

internal readonly record struct HostBinding(Guid Session);
internal sealed record ActiveGroup(HostBinding Binding);
internal sealed class HostCoordinator(ActiveGroup active)
{
    internal readonly ActiveGroup _active = active;
}
internal sealed record RecordingSnapshot(HostBinding Binding, ContentView Content);
internal sealed record PendingWrite(RecordingSnapshot Snapshot, Task Work);
internal sealed class RecordingService(HostCoordinator host, PendingWrite? pending)
{
    internal readonly HostCoordinator _host = host;
    internal readonly PendingWrite? _pending = pending;
}
internal sealed class ContentView
{
    internal const int ChainLength = 10_000;
    internal readonly byte[] Bytes = new byte[1_100_000];
    internal readonly char[] Chars = new char[600_000];
    internal readonly LinkedList<int> Chain = new(Enumerable.Range(0, ChainLength));
}
