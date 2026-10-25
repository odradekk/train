using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Railway;

internal sealed record TrainCapabilities(double LengthMeters, double MassTons,
    double TractionKilonewtons, double MaximumSpeedMetersPerSecond, double Acceleration,
    bool HasControl, ImmutableArray<GoodId> MissingGoods)
{
    internal static TrainCapabilities Calculate(WorldState world, CoreContentProjection content, TrainState train)
    {
        var length = 0.0;
        var mass = 0.0;
        var traction = 0.0;
        double? maximumSpeed = null;
        var hasControl = false;
        foreach (var vehicle in train.Vehicles)
        {
            var id = new InstanceId.Vehicle(vehicle);
            var instance = world.Instances.Single(i => i.Id == id);
            var design = world.Designs.Single(d => d.Design == instance.Design);
            var currentDesign = content.Designs.SingleOrDefault(d => d.Design == design.Design);
            length += ((DesignKind.Vehicle)design.Kind).LengthCells * 0.5;
            mass += design.MassTons;
            for (var index = 0; index < design.Parts.Length; index++)
            {
                var part = design.Parts[index];
                var current = instance.Parts[index].Current;
                if (InstanceCapabilities.DefinitionMissing(content, currentDesign, part)
                    || Math.Min(current, part.MaxDurability) <= 0)
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
        var inventory = world.Inventories.Single(i => i.Id == train.Inventory).Goods;
        ImmutableArray<GoodId> missingGoods = [];
        if (!inventory.IsEmpty)
        {
            var missing = ImmutableArray.CreateBuilder<GoodId>();
            foreach (var stack in inventory.OrderBy(s => GoodOrder(s.Good).Kind)
                         .ThenBy(s => GoodOrder(s.Good).Identity, StringComparer.Ordinal))
            {
                var definition = content.Goods.SingleOrDefault(g => g.Good == stack.Good)
                    ?? (stack.Good is GoodId.Assembly
                        ? world.DesignGoods.SingleOrDefault(g => g.Good == stack.Good) : null);
                if (definition is null) missing.Add(stack.Good);
                else mass += definition.MassTons * stack.Quantity;
            }
            missingGoods = missing.Order(WorldReferences.GoodComparer).ToImmutableArray();
        }
        return new(length, mass, traction, maximumSpeed ?? 0, mass > 0 ? traction / mass : 0,
            hasControl, missingGoods);
    }

    internal CoreProblem? DepartureProblem(TrainId train, double minimumAcceleration)
    {
        var subject = new WorldObjectId.Train(train);
        if (!HasControl)
            return new("control_missing", "Train.Vehicles", subject, []);
        if (TractionKilonewtons == 0)
            return new("traction_missing", "Train.TractionKilonewtons", subject, []);
        if (MissingProblem(train) is { } missing) return missing;
        return Acceleration < minimumAcceleration
            ? new("overloaded", "Train.Acceleration", subject, []) : null;
    }

    internal CoreProblem? MissingProblem(TrainId train) => MissingGoods.IsEmpty ? null
        : new("content_missing", "Train.Inventory", new WorldObjectId.Train(train),
            MissingGoods.Select(WorldReferences.GoodKey).ToImmutableArray());

    private static (int Kind, string Identity) GoodOrder(GoodId good) => good switch
    {
        GoodId.Voxel voxel => (0, voxel.Definition.Value),
        GoodId.Composite composite => (1, composite.Definition.Value),
        GoodId.Assembly assembly => (2, assembly.Composition.Value),
        _ => throw new InvalidOperationException("货物种类无效。")
    };
}
