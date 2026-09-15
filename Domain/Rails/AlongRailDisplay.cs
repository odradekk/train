using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public sealed record TrainDisplayVehicleQuery(
    EntityId VehicleId,
    EntityId? AssemblyId,
    CompositeTransform Transform,
    WorldContactQuery FrontContact,
    WorldContactQuery RearContact);

public sealed record TrainDisplayAssemblyQuery(
    EntityId Id,
    RailPosition Reference,
    IReadOnlyList<EntityId> VehicleIds);

public sealed record TrainDisplaySample(
    double Alpha,
    bool IntervalEnded,
    IReadOnlyList<TrainDisplayAssemblyQuery> Assemblies,
    IReadOnlyList<TrainDisplayVehicleQuery> Vehicles);

public sealed record TrainFollowCameraQuery(
    EntityId VehicleId,
    double FocusX,
    double FocusY,
    double FocusZ,
    double HeadingX,
    double HeadingY,
    double HeadingZ);

public static class AlongRailDisplay
{
    public static double ClampAlpha(double alpha)
    {
        if (!double.IsFinite(alpha))
            return 0;
        if (alpha <= 0)
            return 0;
        if (alpha >= 1)
            return 1;
        return alpha;
    }

    public static bool TryWorldX(PhysicalRailState rails, RailPosition position, out double worldX)
    {
        ArgumentNullException.ThrowIfNull(rails);
        worldX = 0;
        var segment = rails.Segments.FirstOrDefault(item => item.Id == position.SegmentId);
        if (segment is null || !double.IsFinite(position.Distance))
            return false;
        worldX = position.Heading == RailHeading.AgainstAlignment
            ? segment.Alignment.EndX - position.Distance
            : segment.Alignment.StartX + position.Distance;
        return double.IsFinite(worldX);
    }

    public static bool TryInterpolateReference(
        PhysicalRailState rails,
        VoxelWorld world,
        RailPosition from,
        RailPosition to,
        double alpha,
        out RailPosition interpolated)
    {
        interpolated = default;
        ArgumentNullException.ThrowIfNull(rails);
        ArgumentNullException.ThrowIfNull(world);
        var t = ClampAlpha(alpha);
        if (!TryWorldX(rails, from, out var fromX) || !TryWorldX(rails, to, out var toX))
            return false;
        var worldX = fromX + t * (toX - fromX);
        return StraightTrainSolver.TryResolveReference(
            rails,
            world,
            (worldX, StraightRailFixture.CenterY, StraightRailFixture.CenterZ),
            to.Heading,
            out interpolated,
            t < 1 ? from.SegmentId : to.SegmentId);
    }

    public static TrainFollowCameraQuery FollowCamera(TrainDisplayVehicleQuery pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        var hx = pose.FrontContact.X - pose.RearContact.X;
        var hy = pose.FrontContact.Y - pose.RearContact.Y;
        var hz = pose.FrontContact.Z - pose.RearContact.Z;
        var length = Math.Sqrt(hx * hx + hy * hy + hz * hz);
        if (length <= 0 || !double.IsFinite(length))
        {
            hx = 1;
            hy = 0;
            hz = 0;
        }
        else
        {
            hx /= length;
            hy /= length;
            hz /= length;
        }

        return new TrainFollowCameraQuery(
            pose.VehicleId,
            pose.Transform.OriginX,
            pose.Transform.OriginY,
            pose.Transform.OriginZ,
            hx,
            hy,
            hz);
    }

    public static TrainDisplaySample Sample(
        IReadOnlyList<TrainAssemblyQuery> from,
        IReadOnlyList<TrainAssemblyQuery> to,
        IReadOnlyDictionary<EntityId, CompositeInstance> vehicles,
        PhysicalRailState rails,
        VoxelWorld world,
        double alpha,
        bool intervalEnded)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(rails);
        ArgumentNullException.ThrowIfNull(world);
        var t = ClampAlpha(alpha);
        var snap = intervalEnded || from.Count == 0;
        var assemblies = new List<TrainDisplayAssemblyQuery>(to.Count);
        var poses = new List<TrainDisplayVehicleQuery>();
        foreach (var current in to.OrderBy(item => item.Id.Value))
        {
            var previous = from.FirstOrDefault(item => item.Id == current.Id);
            var sameMembers = previous is not null &&
                              previous.VehicleIds.SequenceEqual(current.VehicleIds);
            RailPosition reference;
            double referenceX;
            if (snap || previous is null || !sameMembers ||
                current.HaltReason == TrainHaltReason.SupportLost ||
                previous.HaltReason == TrainHaltReason.SupportLost ||
                !TryInterpolateReference(rails, world, previous.Reference, current.Reference, t, out reference) ||
                !TryWorldX(rails, reference, out referenceX))
            {
                reference = current.Reference;
                if (!TryWorldX(rails, reference, out referenceX))
                    referenceX = current.Vehicles[0].FrontContact.X;
            }

            assemblies.Add(new TrainDisplayAssemblyQuery(current.Id, reference, current.VehicleIds));
            var currentReferenceX = current.Vehicles[0].FrontContact.X;
            foreach (var vehicle in current.Vehicles)
            {
                var frontX = referenceX + (vehicle.FrontContact.X - currentReferenceX);
                if (!TryPose(vehicles, vehicle.VehicleId, current.Id, frontX, out var pose))
                    pose = new TrainDisplayVehicleQuery(
                        vehicle.VehicleId,
                        current.Id,
                        vehicle.Transform,
                        vehicle.FrontContact,
                        vehicle.RearContact);
                poses.Add(pose);
            }
        }

        return new TrainDisplaySample(t, snap, assemblies, poses);
    }

    public static bool IntervalEnded(
        IReadOnlyList<TrainAssemblyQuery> from,
        IReadOnlyList<TrainAssemblyQuery> to,
        ulong fromGraph,
        ulong toGraph) =>
        fromGraph != toGraph ||
        from.Count != to.Count ||
        !from.Select(item => item.Id).SequenceEqual(to.Select(item => item.Id)) ||
        from.Zip(to, (left, right) =>
                !left.VehicleIds.SequenceEqual(right.VehicleIds) ||
                left.HaltReason == TrainHaltReason.SupportLost ||
                right.HaltReason == TrainHaltReason.SupportLost)
            .Any(ended => ended);

    private static bool TryPose(
        IReadOnlyDictionary<EntityId, CompositeInstance> vehicles,
        EntityId vehicleId,
        EntityId assemblyId,
        double frontX,
        out TrainDisplayVehicleQuery pose)
    {
        pose = default!;
        if (!vehicles.TryGetValue(vehicleId, out var vehicle) ||
            vehicle.Contacts.Count < 2)
            return false;
        var transform = StaticTrainFixture.FacingPositiveX(frontX);
        var posed = vehicle.WithTransform(transform);
        if (!StraightTrainSolver.TryToWorldContact(posed, 0, out var front) ||
            !StraightTrainSolver.TryToWorldContact(posed, 1, out var rear) ||
            !transform.IsValid)
            return false;
        pose = new TrainDisplayVehicleQuery(vehicleId, assemblyId, transform, front, rear);
        return true;
    }
}
