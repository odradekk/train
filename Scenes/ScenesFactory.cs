using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

public static class ScenesFactory
{
    public static IScenesFactory Create() => new Factory();

    private sealed class Factory : IScenesFactory
    {
        public SceneResult<IWorldSceneSession> OpenWorld(SceneBinding binding, SceneWorldSnapshot initial)
        {
            WorldSceneSession.ValidateSnapshot(initial);
            return new SceneResult<IWorldSceneSession>.Success(new WorldSceneSession(binding, initial, WorldSceneSession.Generate));
        }
        public SceneResult<IDesignEditSession> OpenEditor(IContentView content, DesignEvaluation initial) =>
            throw new NotImplementedException("Design editing is implemented by Issue #333.");
        public SceneResult<ITemplatePreview> PreviewTemplate(IContentView content, SettlementTemplateDraft draft, EditRevision revision) =>
            throw new NotImplementedException("Template previews are implemented by Issue #389.");
        public SceneResult<IDesignPreview> PreviewDesign(IContentView content, DesignEvaluation initial)
        {
            ArgumentNullException.ThrowIfNull(content);
            ArgumentNullException.ThrowIfNull(initial);
            if (initial is DesignEvaluation.Failed failed)
                return new SceneResult<IDesignPreview>.Failure(failed.Diagnostics.Select(problem =>
                    new SceneProblem(problem.Code, SceneStage.Input, null, problem.Subject, problem.Source?.Package, problem.Element, problem.Message)).ToImmutableArray());
            return new SceneResult<IDesignPreview>.Success(new DesignPreview(content, initial));
        }
    }
}

internal sealed class DesignPreview : IDesignPreview
{
    private readonly RenderedDesign geometry;
    private readonly SceneSessionId session = new(Guid.NewGuid());
    private ulong revision;
    private bool disposed;
    public ContentGeneration Content { get; }

    internal DesignPreview(IContentView content, DesignEvaluation evaluation)
    {
        Content = content.Generation;
        ulong nextId = 0;
        geometry = DesignGeometry.Build(content, evaluation, null, new(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity), _ => new(++nextId));
    }
    public SceneResult<ISceneFrame> Present()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return new SceneResult<ISceneFrame>.Success(new SceneFrame(new(session, new(1), new(1), ++revision),
            new(0, 0, 0), 0, SceneReadiness.Ready, geometry.Objects, geometry.Picking, new([], []), geometry.Diagnostics));
    }
    public void Dispose() => disposed = true;
}
