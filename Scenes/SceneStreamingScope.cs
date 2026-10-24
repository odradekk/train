using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class SceneStreamingScope
{
    private sealed class Part(SceneStreamingAttemptId attempt)
    {
        internal SceneStreamingAttemptId Attempt = attempt;
        internal bool Needed, Queued, Cancelled, Admitted, Failed;
    }
    private readonly ISceneStreamingObserver observer;
    private readonly object gate = new();
    private readonly Dictionary<SceneStreamingPartId, Part> parts = [];
    private readonly Dictionary<SceneStreamingPartId, SceneStreamingAttemptId> admitted = [];
    private ImmutableArray<SceneStreamingAttemptId> frameParts = [];
    private bool closed;
    internal SceneStreamingSceneId Id { get; }

    internal SceneStreamingScope(ISceneStreamingObserver observer, SceneStreamingSceneId id,
        WorldLocation location, SceneStreamingRole role)
    {
        this.observer = observer;
        Id = id;
        observer.OnEvent(new SceneStreamingEvent.Opened(id, location, role));
    }

    internal SceneStreamingPartId PartId(SceneStreamingPartKind kind, string key) => new(Id, kind, key);

    private Part Get(SceneStreamingPartId id)
    {
        if (!parts.TryGetValue(id, out var part)) parts.Add(id, part = new(new(id, 1)));
        return part;
    }

    private void Renew(Part part)
    {
        part.Attempt = part.Attempt with { Value = part.Attempt.Value + 1 };
        part.Queued = part.Cancelled = part.Admitted = part.Failed = false;
        if (part.Needed) Emit(part.Attempt, SceneStreamingStage.Needed);
    }

    internal SceneStreamingAttemptId Need(SceneStreamingPartKind kind, string key)
    {
        lock (gate)
        {
            var part = Get(PartId(kind, key));
            if (part.Cancelled) Renew(part);
            if (!part.Needed) { part.Needed = true; Emit(part.Attempt, SceneStreamingStage.Needed); }
            return part.Attempt;
        }
    }

    internal void Demand(SceneStreamingPartKind kind, IEnumerable<string> keys)
    {
        lock (gate)
        {
            var wanted = keys.ToHashSet(StringComparer.Ordinal);
            foreach (var key in wanted) Need(kind, key);
            foreach (var (id, part) in parts)
                if (id.Kind == kind && part.Needed && !wanted.Contains(id.Key))
                { part.Needed = false; Emit(part.Attempt, SceneStreamingStage.DemandEnded); }
        }
    }

    internal SceneStreamingAttemptId Begin(SceneStreamingPartKind kind, string key)
    {
        lock (gate)
        {
            var part = Get(PartId(kind, key));
            if (part.Cancelled || part.Admitted) Renew(part);
            return part.Attempt;
        }
    }

    internal void Queue(SceneStreamingAttemptId attempt)
    {
        lock (gate)
        {
            var part = Get(attempt.Part);
            if (part.Attempt != attempt || part.Queued) return;
            part.Queued = true;
            Emit(attempt, SceneStreamingStage.Queued);
        }
    }

    internal T Build<T>(SceneStreamingAttemptId attempt, Func<T> build)
    {
        Emit(attempt, SceneStreamingStage.Started);
        try
        {
            var result = build();
            // A builder may ignore cancellation. Its late completion still belongs to this attempt.
            Emit(attempt, SceneStreamingStage.Built);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            lock (gate)
            {
                var part = Get(attempt.Part);
                if (part.Attempt == attempt) part.Failed = true;
                Emit(attempt, SceneStreamingStage.Failed);
            }
            throw;
        }
    }

    internal void CacheHit(SceneStreamingAttemptId attempt) => Emit(attempt, SceneStreamingStage.CacheHit);

    internal void Admit(SceneStreamingAttemptId attempt, ImmutableArray<SceneObjectId> objects)
    {
        lock (gate)
        {
            admitted[attempt.Part] = attempt;
            var part = Get(attempt.Part);
            if (part.Attempt == attempt) part.Admitted = true;
            observer.OnEvent(new SceneStreamingEvent.Changed(attempt, SceneStreamingStage.Admitted, objects));
        }
    }

    internal void Cancel(SceneStreamingAttemptId attempt)
    {
        lock (gate)
        {
            var part = Get(attempt.Part);
            if (part.Attempt != attempt || part.Cancelled || part.Failed) return;
            part.Cancelled = true;
            Emit(attempt, SceneStreamingStage.Cancelled);
        }
    }

    internal void Cancel(SceneStreamingPartKind kind, string key)
    {
        lock (gate)
            if (parts.TryGetValue(PartId(kind, key), out var part) && !part.Admitted) Cancel(part.Attempt);
    }

    internal void CancelPending()
    {
        lock (gate)
            foreach (var part in parts.Values)
                if (!part.Admitted && !part.Cancelled) Cancel(part.Attempt);
    }

    internal void Frame(SceneFrameStamp stamp, IEnumerable<SceneStreamingPartId> live, ISceneStreamingCoverage? coverage)
    {
        lock (gate)
        {
            var next = live.Distinct().Where(admitted.ContainsKey).Select(id => admitted[id]).ToImmutableArray();
            // Pose-only frames share membership storage in the retained measurement stream.
            if (!next.SequenceEqual(frameParts)) frameParts = next;
            observer.OnEvent(new SceneStreamingEvent.Frame(Id, stamp, frameParts, coverage));
        }
    }

    internal void Activate() => observer.OnEvent(new SceneStreamingEvent.Activated(Id));
    internal void Close()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            foreach (var part in parts.Values.Where(part => part.Needed))
            { part.Needed = false; Emit(part.Attempt, SceneStreamingStage.DemandEnded); }
            observer.OnEvent(new SceneStreamingEvent.Closed(Id));
        }
    }
    private void Emit(SceneStreamingAttemptId attempt, SceneStreamingStage stage) =>
        observer.OnEvent(new SceneStreamingEvent.Changed(attempt, stage, []));
}
