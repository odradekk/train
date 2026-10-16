using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.ContractSamples;

public static class ConsumerExamples
{
    public static SubmissionResult SetLoadingRoute(ICoreSession session, TrainId train,
        NodeId waypoint, NodeId station, ulong dwellSteps, InventoryId localInventory,
        GoodId good, long targetQuantity)
        => session.Submit(new CoreCommand.ReplaceRunPlan(train, new RunPlan(false,
            ImmutableArray.Create<PlanStop>(new PlanStop.Waypoint(waypoint),
                new PlanStop.Station(station, dwellSteps, ImmutableArray.Create(
                    new AutomaticGoodsRule(new AutomaticGoodsAction.Load(), good,
                        localInventory, targetQuantity)))))), CommandSource.UserInterface);

    public static ImmutableArray<GoodsQuantity> ReadBatchOutputs(ProcessProjection process)
        => process.Details switch
        {
            ProcessDetails.Production production => production.Outputs,
            ProcessDetails.Extraction extraction => ImmutableArray.Create(extraction.Extracts),
            ProcessDetails.Recovery recovery => recovery.Outputs,
            _ => throw new InvalidOperationException("未知的流程类别。")
        };

    public static (WorldSummary Map, Lookup<LocationView> Scene, Lookup<TrainView> Panel)
        ReadSameBoundary(ICoreSession session, TrainId train, WorldLocation location)
    {
        var view = session.Read();
        return (view.World, view.FindLocation(location), view.FindTrain(train));
    }

    public static ulong? ReadPlatformQueueEnteredStep(TrainView train)
        => train.PlatformQueueEnteredStep;

    public static SubmissionResult BuyGoods(ICoreSession session, TradeQuoteRequest request, Money maximum)
        => session.Submit(new CoreCommand.BuyGoods(request.Train, request.Settlement,
            request.LocalInventory, request.Good, request.Quantity, maximum), CommandSource.UserInterface);

    public static SubmissionResult ApplyAppearanceChange(ICoreSession session,
        InstanceId instance, DesignChangeProjection change)
        => session.Submit(new CoreCommand.ApplyInstanceDesign(instance, change), CommandSource.UserInterface);

    public static CoreResult<string> CaptureSave(ICoreSession session)
        => session.Capture(CapturePurpose.Save) switch
        {
            CoreResult<ICoreCapture>.Success result => new CoreResult<string>.Success(result.Value.Encode()),
            CoreResult<ICoreCapture>.Failure failure => new CoreResult<string>.Failure(failure.Problems),
            CoreResult<ICoreCapture>.Faulted fault => new CoreResult<string>.Faulted(fault.Fault, fault.Diagnostic),
            _ => throw new InvalidOperationException("未知的核心结果。")
        };

    public static ImmutableArray<EventEnvelope> RunHeadless(ICoreSession session, uint steps)
        => session.Advance(steps).Events;

    public static ReplayAdvanceResult InspectRecordedCommand(ICoreReplay replay, SimulationPosition target)
        => replay.Advance(1, target);

    public static CoreResult<ICoreSessionCandidate> ForkReplay(ICoreFactory factory, ICoreReplay replay,
        CoreContentProjection currentContent, CoreSessionOptions options)
        => factory.PrepareRestore(replay.CaptureBoundary(), currentContent, options);

    public static RecordingMaintenance ReportRecordingWrite(ICoreSession session, RecordingStart point,
        bool publishedWithDependencies)
        => session.ConfirmRecordingStart(point.Id, publishedWithDependencies
            ? RecordingWriteOutcome.PublishedWithDependencies : RecordingWriteOutcome.Failed);
}
