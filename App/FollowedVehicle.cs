using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App;

// 跟随列车时的焦点车辆：列车参考中心所在的那辆（用户决定 https://github.com/odradekk/train/issues/368#issuecomment-6344217504）。
// 核心按各车 LengthCells 相加得到编组总长（不计车间距），线路位置是总长的中点。
internal static class FollowedVehicle
{
    internal static VehicleId Of(TrainView train, ICoreReadView core, IContentView content) =>
        AtCenter([.. train.Vehicles.Select(vehicle => (vehicle, LengthCells(vehicle, core, content)))]);

    // 从首端按顺序累加车长，返回跨过总长一半的那辆；中心正好落在交界时取靠首端的那辆。有车长未知时退回首车。
    internal static VehicleId AtCenter(IReadOnlyList<(VehicleId Vehicle, int? LengthCells)> members)
    {
        if (members.Any(member => member.LengthCells is null)) return members[0].Vehicle;
        var half = members.Sum(member => (long)member.LengthCells!.Value) / 2.0;
        var end = 0L;
        foreach (var (vehicle, length) in members)
            if ((end += length!.Value) >= half) return vehicle;
        return members[^1].Vehicle;
    }

    private static int? LengthCells(VehicleId vehicle, ICoreReadView core, IContentView content) =>
        core.FindInstance(new InstanceId.Vehicle(vehicle)) is Lookup<InstanceView>.Found instance &&
        content.FindDesign(instance.Value.Design) is ContentLookup<DesignEvaluation>.Found design
            ? design.Value switch
            {
                DesignEvaluation.Complete { Core.Design.Kind: DesignKind.Vehicle kind } => kind.LengthCells,
                DesignEvaluation.Partial { Metrics.VehicleLengthCells: ComputedValue<int>.Known known } => known.Value,
                _ => null
            }
            : null;
}
