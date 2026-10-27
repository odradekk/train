using System.Collections.Immutable;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal enum SceneDisplayStage { ResourcesPrepared, HostCancelled, FramePrepared, FrameCommitted, FramePreparationStarted }

internal enum SceneDisplayWork { Prepare, PrepareCandidate, Commit }

internal sealed record SceneDisplayEvent(long Candidate, SceneFrameStamp Frame,
    SceneObjectId? Object, SceneDisplayStage Stage, ImmutableArray<SceneObjectId>? ResourceObjects = null);
