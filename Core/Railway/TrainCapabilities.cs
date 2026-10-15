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
            var instance = world.Instances.Single(i => i.Id == new InstanceId.Vehicle(vehicle));
            var design = world.Designs.Single(d => d.Design == instance.Design);
            length += ((DesignKind.Vehicle)design.Kind).LengthCells * 0.5;
            mass += design.MassTons;
            foreach (var part in design.Parts.OrderBy(p => p.Id.Value))
            {
                var current = instance.Parts.Single(d => d.Id == part.Id).Current;
                if (InstanceCapabilities.DefinitionMissing(world, content, design.Design, part)
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
        var goods = content.Goods.AddRange(world.DesignGoods.Where(g => g.Good is GoodId.Assembly));
        var missing = ImmutableArray.CreateBuilder<GoodId>();
        foreach (var stack in inventory.OrderBy(s => GoodOrder(s.Good).Kind)
                     .ThenBy(s => GoodOrder(s.Good).Identity, StringComparer.Ordinal))
        {
            var definition = goods.SingleOrDefault(g => g.Good == stack.Good);
            if (definition is null) missing.Add(stack.Good);
            else mass += definition.MassTons * stack.Quantity;
        }
        return new(length, mass, traction, maximumSpeed ?? 0, mass > 0 ? traction / mass : 0,
            hasControl, missing.OrderBy(WorldReferences.GoodKey, StringComparer.Ordinal).ToImmutableArray());
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
