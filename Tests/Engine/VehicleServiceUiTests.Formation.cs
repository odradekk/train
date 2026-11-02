using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Engine.Tests;

public partial class VehicleServiceUiTests
{
    private sealed partial class ServiceView
    {
        public CoreResult<TrainFormationPreview> PreviewTrainFormation(ImmutableArray<VehicleId> orderedVehicles, TrainId? train)
            => completed ? TrainFormationTestScenario.CompletedVehicle9(orderedVehicles, train)
                : throw new InvalidOperationException("UNCONFIGURED: PreviewTrainFormation");
    }
}
