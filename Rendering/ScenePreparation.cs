using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed class ScenePreparation<TVisual> where TVisual : IDisposable
{
    private readonly Dictionary<(SceneObjectId Object, int Index), TVisual> visuals = [];
    internal ISceneFrame? Frame { get; private set; }

    internal bool SameScope(ISceneFrame frame) => Frame is not { } previous ||
        previous.Update.Base == frame.Update.Base && previous.Update.Frame.Session == frame.Update.Frame.Session &&
        previous.Update.Frame.Request == frame.Update.Frame.Request && previous.Update.Frame.Space == frame.Update.Frame.Space;

    internal ISceneFrame Select(ISceneFrame latest)
    {
        if (!SameScope(latest)) Cancel();
        return Frame ??= latest;
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
