using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.Railway;

internal sealed record TrainCapabilities(double LengthMeters, double MassTons,
    double TractionKilonewtons, double MaximumSpeedMetersPerSecond, double Acceleration,
    bool HasControl)
{
    internal static TrainCapabilities Calculate(
        ImmutableArray<(DesignProjection Design, ImmutableArray<(PartId Part, long Current)> Durability)> vehicles,
        ImmutableArray<GoodsQuantity> inventory, ImmutableArray<GoodDefinitionProjection> goods)
    {
        var length = 0.0;
        var mass = 0.0;
        var traction = 0.0;
        double? maximumSpeed = null;
        var hasControl = false;
        foreach (var (design, durability) in vehicles)
        {
            length += ((DesignKind.Vehicle)design.Kind).LengthCells * 0.5;
            mass += design.MassTons;
            foreach (var part in design.Parts.OrderBy(p => p.Id.Value))
            {
                var current = durability.Single(d => d.Part == part.Id).Current;
                if (part.Missing || Math.Min(current, part.MaxDurability) <= 0)
                    continue;
                foreach (var capability in part.Capabilities)
                {
                    if (capability is PartCapability.Control)
                        hasControl = true;
                    if (capability is PartCapability.Propulsion propulsion)
                    {
                        traction += propulsion.TractionKilonewtons;
                        maximumSpeed = maximumSpeed is { } previous
                            ? Math.Min(previous, propulsion.RatedSpeedMetersPerSecond)
                            : propulsion.RatedSpeedMetersPerSecond;
                    }
                }
            }
        }
        foreach (var stack in inventory.OrderBy(s => GoodOrder(s.Good).Kind)
                     .ThenBy(s => GoodOrder(s.Good).Identity, StringComparer.Ordinal))
            mass += goods.Single(g => g.Good == stack.Good).MassTons * stack.Quantity;
        return new(length, mass, traction, maximumSpeed ?? 0, mass > 0 ? traction / mass : 0, hasControl);
    }

    internal CoreProblem? DepartureProblem(TrainId train, double minimumAcceleration)
    {
        var subject = new WorldObjectId.Train(train);
        if (!HasControl)
            return new("control_missing", "Train.Vehicles", subject, []);
        if (TractionKilonewtons == 0)
            return new("traction_missing", "Train.TractionKilonewtons", subject, []);
        return Acceleration < minimumAcceleration
            ? new("overloaded", "Train.Acceleration", subject, []) : null;
    }

    private static (int Kind, string Identity) GoodOrder(GoodId good) => good switch
    {
        GoodId.Voxel voxel => (0, voxel.Definition.Value),
        GoodId.Composite composite => (1, composite.Definition.Value),
        GoodId.Assembly assembly => (2, assembly.Composition.Value),
        _ => throw new InvalidOperationException("货物种类无效。")
    };
}
