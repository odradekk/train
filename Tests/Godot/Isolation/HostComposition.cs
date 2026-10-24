using Train.App.Assets;
using Train.App.Logging;
using Train.App.Notifications;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.App.Composition;

internal static class HostComposition
{
    internal static HostCoordinator CreateHost(TrainLogger logger, string userData,
        GodotMainThreadDispatcher dispatcher, NotificationPolicy notifications) => new(new ScriptedCoreFactory(),
            new ScriptedContentFactory(), new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
            new(new("builtin"), new("mods"), null, "0.0.56", []), logger, notifications.Decide);

    internal static IScenesFactory CreateScenes() => new UnconfiguredScenesFactory();

    internal static IScenesFactory CreateScenes(ISceneStreamingObserver? observer) => observer is null ? CreateScenes() :
        throw new InvalidOperationException("UNCONFIGURED: 流式记录场景未配置。");

    private sealed class UnconfiguredScenesFactory : IScenesFactory
    {
        public SceneResult<IWorldSceneSession> OpenWorld(SceneBinding binding, SceneWorldSnapshot initial) =>
            throw new InvalidOperationException("UNCONFIGURED: 世界场景未配置。");
        public SceneResult<IDesignEditSession> OpenEditor(IContentView content, DesignEvaluation initial) =>
            throw new InvalidOperationException("UNCONFIGURED: 编辑场景未配置。");
        public SceneResult<IDesignPreview> PreviewDesign(IContentView content, DesignEvaluation initial) =>
            throw new InvalidOperationException("UNCONFIGURED: 设计预览未配置。");
        public SceneResult<ITemplatePreview> PreviewTemplate(IContentView content, SettlementTemplateDraft draft, EditRevision revision) =>
            throw new InvalidOperationException("UNCONFIGURED: 模板预览未配置。");
    }
}
