using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;

namespace Train.Scenes.ContractSupport;

public sealed class RecordingStreamingObserver : ISceneStreamingObserver
{
    private readonly ConcurrentQueue<SceneStreamingEvent> events = new();
    public void OnEvent(SceneStreamingEvent value) => events.Enqueue(value);
    public ImmutableArray<SceneStreamingEvent> Snapshot() => [.. events];
}

public static class StreamingScenario
{
    public static SceneStreamingEvent.Frame AssertFrameMatches(
        ImmutableArray<SceneStreamingEvent> events, ISceneFrame frame)
    {
        var matches = events.OfType<SceneStreamingEvent.Frame>()
            .Where(value => value.Stamp == frame.Update.Frame).ToArray();
        Assert.AreEqual(1, matches.Length);
        var streamed = matches[0];
        Assert.AreEqual(frame.Update.Frame.Session, streamed.Scene.Session);
        Assert.IsFalse(streamed.Parts.IsDefault);
        Assert.AreEqual(streamed.Parts.Length, streamed.Parts.Distinct().Count());
        foreach (var attempt in streamed.Parts)
            Assert.AreEqual(streamed.Scene, attempt.Part.Scene);
        return streamed;
    }
}
