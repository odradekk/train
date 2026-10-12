
using System.Collections.Frozen;
using Train.Content.Voxels;

namespace Train.Content.Composites;

public sealed class DesignAttributes
{
    public double Mass { get; }

    public double Traction { get; }

    public double? RatedSpeed { get; }

    public long CargoCapacity { get; }

    public long StructuralDurabilityMax { get; }

    public bool HasControl { get; }

    public IReadOnlyDictionary<VoxelDefinitionId, int> Bom { get; }

    public long BomValue { get; }

    internal DesignAttributes(
        double mass,
        double traction,
        double? ratedSpeed,
        long cargoCapacity,
        long structuralDurabilityMax,
        bool hasControl,
        IReadOnlyDictionary<VoxelDefinitionId, int> bom,
        long bomValue)
    {
        Mass = mass;
        Traction = traction;
        RatedSpeed = ratedSpeed;
        CargoCapacity = cargoCapacity;
        StructuralDurabilityMax = structuralDurabilityMax;
        HasControl = hasControl;
        Bom = bom.ToFrozenDictionary();
        BomValue = bomValue;
    }
}
