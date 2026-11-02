using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Engine.Tests;

internal static class TrainFormationTestScenario
{
    internal static (TrainId? Train, ImmutableArray<VehicleId> OrderedVehicles,
        CoreResult<TrainFormationPreview> Result)[] Vehicle1 =>
    [
        (null, [], Empty),
        (null, [new(1)], Single)
    ];

    internal static (TrainId? Train, ImmutableArray<VehicleId> OrderedVehicles,
        CoreResult<TrainFormationPreview> Result)[] Vehicle9 =>
    [
        (null, [], Empty),
        (null, [new(9)], Single)
    ];

    internal static (TrainId? Train, ImmutableArray<VehicleId> OrderedVehicles,
        CoreResult<TrainFormationPreview> Result)[] Editing =>
    [
        (null, [], Empty),
        (null, [new(1)], Single),
        (null, [new(2)], Single),
        (null, [new(1), new(2)], Pair),
        (null, [new(2), new(1)], Pair)
    ];

    internal static CoreResult<TrainFormationPreview> CompletedVehicle9(
        ImmutableArray<VehicleId> orderedVehicles, TrainId? train)
    {
        foreach (var preview in Vehicle9)
            if (preview.Train == train && preview.OrderedVehicles.SequenceEqual(orderedVehicles))
                return preview.Result;
        throw new InvalidOperationException("UNCONFIGURED: PreviewTrainFormation");
    }

    private static CoreResult<TrainFormationPreview> Empty =>
        new CoreResult<TrainFormationPreview>.Failure([new("composition_empty", "OrderedVehicles", null, [])]);
    private static CoreResult<TrainFormationPreview> Single =>
        new CoreResult<TrainFormationPreview>.Success(new(5, 10, 20, 30, [], null));
    private static CoreResult<TrainFormationPreview> Pair =>
        new CoreResult<TrainFormationPreview>.Success(new(10, 20, 40, 60, [], null));
}

public partial class VehicleServiceUiTests
{
    private sealed partial class ServiceView
    {
        public CoreResult<TrainFormationPreview> PreviewTrainFormation(ImmutableArray<VehicleId> orderedVehicles, TrainId? train)
            => completed ? TrainFormationTestScenario.CompletedVehicle9(orderedVehicles, train)
                : throw new InvalidOperationException("UNCONFIGURED: PreviewTrainFormation");
    }
}
