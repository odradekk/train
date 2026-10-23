using Train.Scenes.Contracts;

namespace Train.Rendering;

internal enum SceneDisplayStage { ResourcesPrepared, Committed, HostCancelled }

internal sealed record SceneDisplayEvent(long Candidate, SceneFrameStamp Frame,
    SceneObjectId? Object, SceneDisplayStage Stage);
