// 核心契约的会话、候选、捕获与录制入口。
// 核心不读文件、不订阅宿主回调，也不向调用方公开内部世界和计算阶段。
using System.Collections.Immutable;

namespace Train.Core.Contracts;

/// <summary>装配处取得的核心工厂；实现程序集的创建函数只返回此接口。</summary>
public interface ICoreFactory
{
    /// <summary>在独立状态中生成新局；候选引用列出全部聚居地副本所有者，失败不会改变另一活动会话。</summary>
    CoreResult<ICoreSessionCandidate> PrepareNew(WorldGenerationInput input,
        CoreContentProjection content, CoreSessionOptions options);
    /// <summary>解析当前格式并公开引用清单，不要求宿主解析核心 JSON。</summary>
    CoreResult<ICoreCapture> DecodeState(string canonicalPayload);
    /// <summary>按当前内容适配普通读档，保留无法解析的合法占位及冻结执行资料。</summary>
    CoreResult<ICoreSessionCandidate> PrepareRestore(ICoreCapture saved,
        CoreContentProjection content, CoreSessionOptions options);
    /// <summary>检查原构建、环境、配置和内容身份；严格重放不做普通读档适配。</summary>
    CoreResult<ICoreReplay> PrepareReplay(CoreReplayInput input, RuntimeIdentity environment,
        ImmutableArray<CoreContentProjection> contentHistory);
    /// <summary>比较同一位置的核心检查点，返回首个字段差异；不要求调用方解析规范文本。</summary>
    CoreResult<CheckpointComparison> CompareCheckpoints(CoreCheckpoint expected, CoreCheckpoint actual);
}

/// <summary>引擎无关的会话配置；保留起点数为正，具体值由测量确定。</summary>
public sealed record CoreSessionOptions(RuntimeIdentity Runtime, bool EnableDeveloperCommands,
    int RetainedRecordingStarts);
public sealed record RuntimeIdentity(string GameBuild, string SimulationVersion,
    string OperatingSystem, string Architecture, string DotNetRuntime);

/// <summary>准备好的独立会话；宿主完成固定缺失清单确认后才调用一次 Activate。</summary>
public interface ICoreSessionCandidate : IDisposable
{
    ICoreReadView View { get; }
    CoreReferenceManifest References { get; }
    ImmutableArray<CoreProblem> AdaptationReport { get; }
    ImmutableArray<MissingCoreReference> MissingReferences { get; }
    ICoreSession Activate();
}

/// <summary>一个核心运行会话；所有写入口在同一协调线程串行执行。</summary>
public interface ICoreSession : IDisposable
{
    /// <summary>固定当前完整边界；返回值可在其他线程查询并在会话关闭后保留。</summary>
    ICoreReadView Read();
    /// <summary>在协调线程同步裁决全部命令并完成记录；返回后才分发事件。内部候选不公开。</summary>
    SubmissionResult Submit(CoreCommand command, CommandSource source);
    /// <summary>逐步原子推进，首个内部故障即停；已完成步骤不回滚。steps 必须大于零。</summary>
    AdvanceResult Advance(uint steps);
    /// <summary>普通保存拒绝冻结会话；诊断捕获仍可用。捕获不推进模拟。</summary>
    CoreResult<ICoreCapture> Capture(CapturePurpose purpose);
    /// <summary>读取当前核心证据窗口；对应内容由宿主持有。</summary>
    CoreRecordingWindow ReadRecording();
    /// <summary>在当前完整边界建立待持久化起点，不立即裁剪旧证据。</summary>
    CoreResult<RecordingStart> CaptureRecordingStart();
    /// <summary>宿主确认起点及全部依赖发布成功或失败；成功才可能释放较早普通证据。</summary>
    RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome);
}

/// <summary>普通游戏推进的结果；Events 按位置及步内顺序排列。</summary>
public sealed record AdvanceResult(uint CompletedSteps, SimulationPosition Position,
    ImmutableArray<EventEnvelope> Events, CoreFault? Fault, CoreDiagnostic? Diagnostic);
/// <summary>稳定故障身份与字段位置，进入复现证据；不含异常文本或堆栈。</summary>
public sealed record CoreFault(string Code, SimulationPosition Position, string Operation,
    string Field, WorldObjectId? Subject);
/// <summary>本次新故障的诊断输出，不进入权威摘要；堆栈可能为空。</summary>
public sealed record CoreDiagnostic(string Message, string? StackTrace);

public enum CapturePurpose { Save, Diagnostic }
/// <summary>一致捕获的不可变核心载荷；可在后台编码并长期保留。</summary>
public interface ICoreCapture
{
    SimulationPosition Position { get; }
    CoreRevision Revision { get; }
    CoreInputId InputIdentity { get; }
    CoreReferenceManifest References { get; }
    CoreCheckpoint Checkpoint { get; }
    string Encode();
}

/// <summary>引用包括当前对象和已冻结批次、任务、人口统计期所需的历史身份；Templates 按实例所有者保留副本。</summary>
public sealed record CoreReferenceManifest(ImmutableArray<DesignHash> Designs,
    ImmutableArray<OwnedSettlementTemplateReference> Templates,
    ImmutableArray<RequiredDefinition> Definitions, ImmutableArray<CoreInputId> Inputs);
/// <summary>聚居地或建城任务预留的最终实例身份拥有模板副本；来源身份仅用于追溯。</summary>
public sealed record OwnedSettlementTemplateReference(SettlementId Owner,
    SettlementTemplateId SourceTemplate, ImmutableArray<TemplatePlotReference> Plots);
/// <summary>供内容验证实例业务地块与内嵌布局配对；不携带位置或朝向。</summary>
public sealed record TemplatePlotReference(int Number, PlotSize Size);
public sealed record RequiredDefinition(CoreDefinitionReference Definition,
    SemanticRevision? FrozenRevision, string Field, bool WasMissing);
/// <summary>规范文本及摘要由核心生成；调用方比较或导出，不解析为业务状态。</summary>
public sealed record CoreCheckpoint(SimulationPosition Position, string CanonicalText, string Sha256);

public sealed record RecordedCommand(ulong Number, SimulationPosition Before,
    CommandSource Source, string CanonicalPayload, CommandReceipt Result,
    CoreInputId InputBefore, CoreInputId InputAfter);
public sealed record RecordingStart(RecordingPointId Id, ICoreCapture State);
public sealed record RecordingGap(SimulationPosition From, SimulationPosition? Until, string Code);
/// <summary>故障区间独立固定；普通窗口轮换不得删除。</summary>
public sealed record FaultRecordingSegment(RecordingStart Start,
    ImmutableArray<RecordedCommand> Commands, ImmutableArray<CoreCheckpoint> Checkpoints,
    CoreFault Fault, CoreReferenceManifest RequiredReferences);
public sealed record CoreRecordingWindow(SimulationPosition EndPosition, ImmutableArray<RecordingStart> Starts,
    ImmutableArray<RecordedCommand> Commands, ImmutableArray<CoreCheckpoint> Checkpoints,
    ImmutableArray<FaultRecordingSegment> FaultSegments, ImmutableArray<RecordingGap> Gaps,
    CoreReferenceManifest RequiredReferences);
public enum RecordingWriteOutcome { PublishedWithDependencies, Failed }
/// <summary>只释放不再被任何普通或故障片段引用的输入；宿主还需考虑自身其他使用者。</summary>
public sealed record RecordingMaintenance(ImmutableArray<CoreInputId> ReleasedInputs,
    ImmutableArray<RecordingGap> Gaps);

/// <summary>已由宿主从复现包读出的核心部分；格式版本由核心检查。</summary>
public sealed record CoreReplayInput(int FormatVersion, RuntimeIdentity OriginalRuntime,
    string InitialStatePayload, ImmutableArray<RecordedCommand> Commands,
    ImmutableArray<CoreCheckpoint> Checkpoints, SimulationPosition EndPosition, CoreFault? ExpectedFault);
/// <summary>不提供 Submit，严格重放只执行包内输入。</summary>
public interface ICoreReplay : IDisposable
{
    ICoreReadView Read();
    /// <summary>按记录顺序推进；可在某条命令后的精确位置停止，不跨过 stopAt。</summary>
    ReplayAdvanceResult Advance(uint maximumSteps, SimulationPosition? stopAt = null);
    /// <summary>只导出当前完整边界，作为新普通会话的起点；不修改原重放。</summary>
    ICoreCapture CaptureBoundary();
}
public enum ReplayStop { BudgetReached, RequestedPosition, EndOfRecording, FirstDifference, RecordedFault, UnexpectedFault }
public sealed record CheckpointDifference(SimulationPosition Position, string Field,
    string Expected, string Actual);
/// <summary>相同检查点或首个权威字段差异；不同位置通过 CoreResult.Failure 报告不可比较。</summary>
public abstract record CheckpointComparison
{
    private CheckpointComparison() { }
    public sealed record Equal : CheckpointComparison;
    public sealed record Different(CheckpointDifference Difference) : CheckpointComparison;
}
public sealed record ReplayAdvanceResult(ReplayStop Stop, SimulationPosition Position,
    ImmutableArray<EventEnvelope> Events, CheckpointDifference? Difference, CoreFault? Fault);
