using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Scenes.Contracts;

public interface IDesignPreview : IDisposable
{
    ContentGeneration Content { get; }
    SceneResult<ISceneFrame> Present();
}

public sealed record TemplatePlotPreview(int DraftIndex, ScenePose Pose, GridBounds Bounds);
public interface ITemplatePreview
{
    ContentGeneration Content { get; }
    EditRevision Revision { get; }
    ISceneFrame Frame { get; }
    ImmutableArray<TemplatePlotPreview> Plots { get; }
    ImmutableArray<SceneProblem> Diagnostics { get; }
    Lookup<int> PickPlot(SceneRay ray);
}
