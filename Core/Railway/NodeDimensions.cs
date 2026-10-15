using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Railway;

internal readonly record struct NodeDimensions(double Approach, double Platform, double Departure)
{
    internal double Stop => Approach + Platform / 2;
    internal double Length => Approach + Platform + Departure;

    internal static NodeDimensions Read(WorldState world, CoreContentProjection content, NodeId node)
    {
        var rule = TrainOperations.Rule(world, content);
        return new(rule.ApproachLengthMeters,
            world.Settlements.FirstOrDefault(s => s.Node == node)?.Template.PlatformLengthMeters ?? 0,
            rule.DepartureLengthMeters);
    }
}
