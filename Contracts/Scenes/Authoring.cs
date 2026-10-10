// 固定设计与聚居地模板的只读预览；不复制设计编辑会话。
using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Scenes.Contracts;

/// <summary>固定内容与设计的只读预览；复用体素网格生成，不暴露编辑操作、撤销或脏状态。</summary>
public interface IDesignPreview : IDisposable
{
    ContentGeneration Content { get; }
    /// <summary>生成时间为零的完整替换帧；缺失引用保留已知部分和诊断。关闭后调用抛 ObjectDisposedException。</summary>
    SceneResult<ISceneFrame> Present();
}

/// <summary>DraftIndex 对应本次固定草稿的 Plots 下标，不以可能重复的地块编号定位；Bounds 为地块自身格范围。</summary>
public sealed record TemplatePlotPreview(int DraftIndex, ScenePose Pose, GridBounds Bounds);
public interface ITemplatePreview
{
    ContentGeneration Content { get; }
    EditRevision Revision { get; }
    /// <summary>固定的完整替换帧；场景模型和可解析的预建建筑由体素生成，时间为零。</summary>
    ISceneFrame Frame { get; }
    ImmutableArray<TemplatePlotPreview> Plots { get; }
    ImmutableArray<SceneProblem> Diagnostics { get; }
    /// <summary>在 Frame 空间拾取地块占地，返回固定草稿数组下标；最近者优先，同距离按下标。未命中为 Missing。</summary>
    Lookup<int> PickPlot(SceneRay ray);
}
