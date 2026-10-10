// 核心契约的可编译消费样例；只依赖契约，不提供游戏实现或模拟算法替身。
// 参数由装配方提供；真正的共享行为用例和四模块隔离由契约原型任务交付。
using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.ContractSamples;

public static class ConsumerExamples
{
    /// <summary>地图、地点与面板使用同一边界；随后推进不改变这些已取得的结果。</summary>
    public static (WorldSummary Map, Lookup<LocationView> Scene, Lookup<TrainView> Panel)
        ReadSameBoundary(ICoreSession session, TrainId train, WorldLocation location)
    {
        var view = session.Read();
        return (view.World, view.FindLocation(location), view.FindTrain(train));
    }

    /// <summary>调用方直接给出报价请求和可接受总额，核心再次裁决而非凭报价成交。</summary>
    public static SubmissionResult BuyGoods(ICoreSession session, TradeQuoteRequest request, Money maximum)
        => session.Submit(new CoreCommand.BuyGoods(request.Train, request.Settlement,
            request.LocalInventory, request.Good, request.Quantity, maximum), CommandSource.UserInterface);

    /// <summary>协调线程同步提交固定设计差额；宿主按回执裁决切换对应内容。</summary>
    public static SubmissionResult ApplyAppearanceChange(ICoreSession session,
        InstanceId instance, DesignChangeProjection change)
        => session.Submit(new CoreCommand.ApplyInstanceDesign(instance, change), CommandSource.UserInterface);

    /// <summary>编码固定捕获值；后台不能再从活动会话取状态拼接同一存档。</summary>
    public static CoreResult<string> CaptureSave(ICoreSession session)
        => session.Capture(CapturePurpose.Save) switch
        {
            CoreResult<ICoreCapture>.Success result => new CoreResult<string>.Success(result.Value.Encode()),
            CoreResult<ICoreCapture>.Failure failure => new CoreResult<string>.Failure(failure.Problems),
            CoreResult<ICoreCapture>.Faulted fault => new CoreResult<string>.Faulted(fault.Fault, fault.Diagnostic),
            _ => throw new InvalidOperationException("未知的核心结果。")
        };

    /// <summary>无引擎地推进同一接口；返回事件供宿主一次分发，不从新视图反推过去事件。</summary>
    public static ImmutableArray<EventEnvelope> RunHeadless(ICoreSession session, uint steps)
        => session.Advance(steps).Events;

    /// <summary>重放可停在同一步内某条命令后的边界，便于检查拒绝或内容发布。</summary>
    public static ReplayAdvanceResult InspectRecordedCommand(ICoreReplay replay, SimulationPosition target)
        => replay.Advance(1, target);

    /// <summary>严格重放无外部命令入口；到达停止点后可导出当前边界作为普通读档候选。</summary>
    public static CoreResult<ICoreSessionCandidate> ForkReplay(ICoreFactory factory, ICoreReplay replay,
        CoreContentProjection currentContent, CoreSessionOptions options)
        => factory.PrepareRestore(replay.CaptureBoundary(), currentContent, options);

    /// <summary>只有已发布起点与全部依赖才传成功；普通保存成功不自动等于录制起点成功。</summary>
    public static RecordingMaintenance ReportRecordingWrite(ICoreSession session, RecordingStart point,
        bool publishedWithDependencies)
        => session.ConfirmRecordingStart(point.Id, publishedWithDependencies
            ? RecordingWriteOutcome.PublishedWithDependencies : RecordingWriteOutcome.Failed);
}
