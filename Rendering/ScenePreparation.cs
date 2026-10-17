using System.Diagnostics;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed class ScenePreparation<TVisual>(Func<TVisual, SceneVisual, bool> canUpdate) where TVisual : IDisposable
{
    private static readonly TimeSpan MergeDuration = TimeSpan.FromMilliseconds(500);
    private readonly Dictionary<(SceneObjectId Object, int Index), TVisual> visuals = [];
    private long started;
    internal ISceneFrame? Frame { get; private set; }

    internal bool SameScope(ISceneFrame frame) => Frame is not { } previous ||
        previous.Update.Base == frame.Update.Base && previous.Update.Frame.Session == frame.Update.Frame.Session &&
        previous.Update.Frame.Request == frame.Update.Frame.Request && previous.Update.Frame.Space == frame.Update.Frame.Space;

    internal ISceneFrame Select(ISceneFrame latest, long timestamp)
    {
        if (!SameScope(latest)) Cancel();
        if (Frame is null) { Frame = latest; started = timestamp; }
        // Lock a finite target after the merge window so continuous arrivals cannot starve commits.
        else if (Stopwatch.GetElapsedTime(started, timestamp) < MergeDuration &&
            latest.Update.Frame.Revision >= Frame.Update.Frame.Revision) Frame = latest;
        return Frame;
    }

    internal void Retain(IReadOnlyDictionary<SceneObjectId, SceneObject> sources,
        Func<SceneObjectId, int, SceneVisual, bool> alreadyDisplayed)
    {
        foreach (var pair in visuals.ToArray())
        {
            var (id, index) = pair.Key;
            if (sources.TryGetValue(id, out var source) && index < source.Visuals.Length &&
                !alreadyDisplayed(id, index, source.Visuals[index]) && canUpdate(pair.Value, source.Visuals[index])) continue;
            visuals.Remove(pair.Key);
            pair.Value.Dispose();
        }
    }

    internal bool TryGet((SceneObjectId Object, int Index) key, out TVisual visual) => visuals.TryGetValue(key, out visual!);

    internal TVisual GetOrCreate((SceneObjectId Object, int Index) key, Func<TVisual> prepare)
    {
        if (visuals.TryGetValue(key, out var existing)) return existing;
        try
        {
            var visual = prepare();
            visuals.Add(key, visual);
            return visual;
        }
        catch { Cancel(); throw; }
    }

    internal List<TVisual> Take()
    {
        var result = visuals.Values.ToList();
        visuals.Clear();
        Frame = null;
        return result;
    }

    internal void Cancel()
    {
        foreach (var visual in Take()) visual.Dispose();
    }
}
