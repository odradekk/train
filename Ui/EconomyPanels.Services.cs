using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    internal static bool IsConstructionFacility(ICoreReadView view, IContentView content, BuildingId building) =>
        view.FindInstance(new InstanceId.Building(building)) is Lookup<InstanceView>.Found instance &&
        content.FindDesign(instance.Value.Design) is ContentLookup<DesignEvaluation>.Found
            { Value: DesignEvaluation.Complete { Core: { } input } } &&
        input.Design.Parts.Any(part => part.Capabilities.Any(capability => capability is PartCapability.Construction));

    internal static string? TrainVehicleName(ICoreReadView view, VehicleId vehicle) =>
        view.FindInstance(new InstanceId.Vehicle(vehicle)) is Lookup<InstanceView>.Found
            { Value.Details: InstanceDetails.Vehicle { Train: { } trainId } } &&
        view.FindTrain(trainId) is Lookup<TrainView>.Found train
            ? $"列车 {trainId.Value} 的第 {train.Value.Vehicles.IndexOf(vehicle) + 1} 辆车" : null;

    internal static string DescribeVehicleJob(ICoreReadView view, IContentView content, ConstructionJobView job)
    {
        string DesignName(DesignHash design) =>
            content.FindDesign(design) is ContentLookup<DesignEvaluation>.Found { Value: DesignEvaluation.Complete complete }
                ? complete.Definition.Display.Name : "设计内容缺失";

        string VehicleName(VehicleId id)
        {
            if (view.FindInstance(new InstanceId.Vehicle(id)) is not Lookup<InstanceView>.Found found) return "车辆已不存在";
            if (TrainVehicleName(view, id) is { } name) return name;
            return $"停放车辆（{DesignName(found.Value.Design)}）";
        }

        var name = job.Target switch
        {
            ConstructionTarget.NewVehicle target => DesignName(target.Design),
            ConstructionTarget.Existing { Instance: InstanceId.Vehicle vehicle } => VehicleName(vehicle.Id),
            _ => "施工对象不可用"
        };
        return $"正在{ServiceKind(job.Kind)}{name} · 进度 {job.Progress:P0} · {ServiceRemainingTime(job, view)}" +
            (job.State == ConstructionState.Working ? "" : $" · {ServiceState(job.State)}");
    }

    internal static string PlotSizeName(PlotSize size) => size switch
    {
        PlotSize.Small => "小", PlotSize.Medium => "中", PlotSize.Large => "大", PlotSize.Giant => "巨型",
        PlotSize.SmallStation => "小站点", PlotSize.LargeStation => "大站点", _ => size.ToString()
    };

    internal static string ServiceLibrary(BlueprintLibrary value) => value switch
    { BlueprintLibrary.Packages => "内容包", BlueprintLibrary.Local => "本局库", BlueprintLibrary.User => "用户库", _ => value.ToString() };
    internal static string ServiceKind(ConstructionKind value) => value switch
    { ConstructionKind.Construction => "制造", ConstructionKind.Repair => "修理", ConstructionKind.Refit => "改装", ConstructionKind.Deconstruction => "拆解", _ => value.ToString() };
    internal static string ServiceState(ConstructionState value) => value switch
    { ConstructionState.Queued => "排队中", ConstructionState.Working => "进行中", ConstructionState.ResourceBlocked => "资源受阻", ConstructionState.Paused => "已暂停", ConstructionState.Failed => "已失败", ConstructionState.Completed => "已完成", _ => value.ToString() };
    internal static string ServiceDuration(ICoreReadView view, double seconds) => seconds < 60
        ? $"{Math.Ceiling(seconds):0} 秒"
        : UiInput.FormatTime(view.Position.CompletedSteps + (ulong)Math.Ceiling(seconds * SimulationTime.StepsPerSecond));

    internal static string ServiceRemainingTime(ConstructionJobView job, ICoreReadView view)
    {
        if (job.ActualWorkPerSecond <= 0) return "当前未施工";
        var seconds = Math.Max(0, job.FrozenTotalWork - job.ProgressWork) / job.ActualWorkPerSecond;
        return (seconds < 60 ? "预计还需 " : "预计完成于 ") + ServiceDuration(view, seconds);
    }
}
