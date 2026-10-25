using Train.Core.Construction;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Settlements;

internal readonly record struct BuildingCapabilities(double Production, double Extraction, double Construction, double Power)
{
    internal static BuildingCapabilities Read(WorldState world, CoreContentProjection content, BuildingId building)
    {
        var id = new InstanceId.Building(building);
        var instance = world.GetInstance(id);
        if (ConstructionJobs.InstanceLocked(world, id)) return new(0, 0, 0, 0);
        double production = 0, extraction = 0, construction = 0, power = 0;
        foreach (var part in InstanceCapabilities.EffectiveParts(world, content, instance,
            static part => part.Capabilities.Any(c => c is PartCapability.Production or PartCapability.Extraction
                or PartCapability.Construction or PartCapability.Power)))
            foreach (var capability in part.Capabilities)
                switch (capability)
                {
                    case PartCapability.Production p: production += p.WorkPerSecond; break;
                    case PartCapability.Extraction p: extraction += p.WorkPerSecond; break;
                    case PartCapability.Construction p: construction += p.WorkPerSecond; break;
                    case PartCapability.Power p: power += p.EnergyPerSecond; break;
                }
        return new(production, extraction, construction, power);
    }
}
