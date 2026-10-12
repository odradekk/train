
using Train.Content.Behaviors;
using Train.Content.Voxels;

namespace Train.Content.Composites;

public static class DesignAttributeCalculator
{
    public static DesignAttributes Calculate(IReadOnlyList<ICoreVoxelDefinition> voxels)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        var mass = 0d;
        var traction = 0d;
        double? ratedSpeed = null;
        var cargoCapacity = 0L;
        var structuralDurability = 0L;
        var hasControl = false;
        var bom = new Dictionary<VoxelDefinitionId, int>();
        var bomValue = 0L;
        foreach (var voxel in voxels)
        {
            ArgumentNullException.ThrowIfNull(voxel);
            mass += voxel.Mass;
            bom[voxel.Id] = checked(bom.GetValueOrDefault(voxel.Id) + 1);
            bomValue = checked(bomValue + voxel.Value);
            if (voxel.Behaviors.Count == 0)
                structuralDurability = checked(structuralDurability + voxel.MaxDurability);
            foreach (var behavior in voxel.Behaviors)
            {
                switch (behavior)
                {
                    case PropulsionBehavior propulsion:
                        traction += propulsion.Traction;
                        ratedSpeed = ratedSpeed is { } speed ? Math.Min(speed, propulsion.RatedSpeed) : propulsion.RatedSpeed;
                        break;
                    case ControlBehavior:
                        hasControl = true;
                        break;
                    case CargoBehavior cargo:
                        cargoCapacity = checked(cargoCapacity + cargo.Capacity);
                        break;
                }
            }
        }
        if (!double.IsFinite(mass) || !double.IsFinite(traction))
            throw new OverflowException("Design attribute sums must remain finite.");
        return new DesignAttributes(mass, traction, ratedSpeed, cargoCapacity, structuralDurability, hasControl, bom, bomValue);
    }
}
