using System.Collections.Immutable;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal enum SceneDisplayStage { ResourcesPrepared, Committed, HostCancelled, FramePrepared, FrameCommitted, FramePreparationStarted }

internal sealed record SceneDisplayEvent(long Candidate, SceneFrameStamp Frame,
    SceneObjectId? Object, SceneDisplayStage Stage, ImmutableArray<SceneObjectId>? ResourceObjects = null);
