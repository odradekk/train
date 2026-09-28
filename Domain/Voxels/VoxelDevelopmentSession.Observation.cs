using Train.Domain.Composites;
using Train.Domain.Rails;

namespace Train.Domain.Voxels;

public sealed partial class VoxelDevelopmentSession
{
    /// <summary>
    /// Whether a start of this assembly would be accepted now, without submitting one: <see cref="TrainMotionStatus.Started"/>
    /// or the refusal the start would report. Reads only; consumes no transaction.
    /// </summary>
    public TrainMotionStatus QueryStartEligibility(EntityId assemblyId)
    {
        if (Fault is not null)
            return TrainMotionStatus.SimulationFaulted;
        return CanStartNow(assemblyId, out var status) ? TrainMotionStatus.Started : status;
    }
}
