// 世界场景由体素会话管理后台工作；宿主只交付固定输入及接入已就绪帧。
using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Scenes.Contracts;

public interface IScenesFactory
{
    /// <summary>轻量创建，不等待完整场景；绑定更换后创建新会话，旧会话不再接收输入。</summary>
    /// <exception cref="ArgumentException">初始核心视图与内容业务输入身份不匹配。</exception>
    SceneResult<IWorldSceneSession> OpenWorld(SceneBinding binding, SceneWorldSnapshot initial);
    /// <summary>宿主保证 initial 由 content 评估而来；Complete 或 Partial 可以编辑，Failed 明确拒绝。</summary>
    SceneResult<IDesignEditSession> OpenEditor(IContentView content, DesignEvaluation initial);
    /// <summary>固定设计的只读显示入口；initial 来自 content，Complete 或 Partial 可预览，Failed 拒绝；不建立编辑、撤销或保存状态。</summary>
    SceneResult<IDesignPreview> PreviewDesign(IContentView content, DesignEvaluation initial);
    /// <summary>开发工具的固定模板预览，不修改表单草稿；缺失引用返回已知部分和诊断。</summary>
    SceneResult<ITemplatePreview> PreviewTemplate(IContentView content,
        SettlementTemplateDraft draft, EditRevision revision);
}
/// <summary>同一完整边界的核心视图与对应内容；InputIdentity 必须与 Content.Core.Identity 相同。</summary>
public sealed record SceneWorldSnapshot(ICoreReadView Core, IContentView Content);
public abstract record WorldSceneTarget
{
    private WorldSceneTarget() { }
    public sealed record Location(WorldLocation Value) : WorldSceneTarget;
    public sealed record Follow(TrainId Train, VehicleId Vehicle) : WorldSceneTarget;
    public sealed record Combat(CombatId Id) : WorldSceneTarget;
}
/// <summary>来自当前实际显示帧的相机焦点，单位格；体素负责跨浮动原点换算和沿线路定位。</summary>
public sealed record SceneCameraSample(ISceneFrame Frame, Vector3 Focus);
/// <summary>Interpolation 为 [0,1]，在相邻已完成模拟步之间插值；暂停时宿主固定该值。</summary>
public sealed record SceneFrameRequest(double Interpolation, SceneCameraSample? Camera,
    SceneDebugLayer DebugLayers);
public interface IWorldSceneSession : IAsyncDisposable
{
    SceneSessionId Id { get; }
    SceneBinding Binding { get; }
    /// <summary>显式切换观察目标；相同目标再次请求也得到新请求身份，取消先前目标的后台接入资格。</summary>
    SceneResult<SceneRequestId> RequestView(WorldSceneTarget target);
    /// <summary>串行交入非递减的完整边界及本次新事件，不执行场景构建。</summary>
    /// <exception cref="ArgumentException">输入不匹配绑定、世代或业务身份，位置倒退，事件乱序、重复或晚于输入边界。</exception>
    SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events);
    /// <summary>
    /// 接入后台完成结果、补齐当前状态并生成帧；首次和分段构建不阻塞本调用。
    /// displayedFrame 是 Godot 已完整接入的最后一帧，null 表示请求完整替换；不交入仅准备但尚未显示的帧。
    /// </summary>
    SceneResult<ISceneFrame> Present(SceneFrameRequest request, ISceneFrame? displayedFrame);
    /// <summary>核对帧仍属当前观察请求及目标仍有效；旧设计部件、已删除对象和过期请求返回明确失败。</summary>
    SceneResult<PickSubject> Resolve(SceneHit hit);
    /// <summary>立即撤销输入和接入资格，随后等待内部任务退出；不释放调用方仍持有的不可变帧。</summary>
    new ValueTask DisposeAsync();
}
