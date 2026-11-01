using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
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
    internal static string ServiceRemainingTime(ConstructionJobView job) => job.ActualWorkPerSecond > 0
        ? $"预计还需 {Math.Ceiling(Math.Max(0, job.FrozenTotalWork - job.ProgressWork) / job.ActualWorkPerSecond):0} 秒"
        : "当前未施工";
    internal static string ServiceLimit(WorkLimit value) => value switch
    { WorkLimit.Capability => "设施能力", WorkLimit.Power => "电力", WorkLimit.Labor => "劳动力", _ => value.ToString() };
}
