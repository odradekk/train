using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public static class StraightTrainSolver
{
    public static (double X, double Y, double Z) WorldPoint(CompositeTransform transform, double x, double y, double z) =>
        transform.TransformPoint(x, y, z);

    public static (double X, double Y, double Z) WorldContact(CompositeTransform transform, CompositeContactAnchor contact) =>
        WorldPoint(transform, contact.X, contact.Y, contact.Z);

    public static bool TryToWorldContact(CompositeInstance vehicle, int index, out WorldContactQuery query)
    {
        query = default!;
        ArgumentNullException.ThrowIfNull(vehicle);
        if (index < 0 || index >= vehicle.Contacts.Count)
            return false;
        var contact = vehicle.Contacts[index];
        var bound = vehicle.PartAt(contact.BoundCell);
        if (bound is null)
            return false;
        var world = WorldContact(vehicle.Transform, contact);
        query = new WorldContactQuery(world.X, world.Y, world.Z, contact.BoundCell, bound.Id);
        return true;
    }

    public static bool TryValidatePoses(
        CompositeInstance locomotive,
        CompositeInstance carriage,
        out TrainAssemblyStatus status)
    {
        status = TrainAssemblyStatus.VehicleConstraint;
        if (!locomotive.Transform.IsValid || !carriage.Transform.IsValid)
            return false;
        if (locomotive.Contacts.Count < 2 || carriage.Contacts.Count < 2)
        {
            status = TrainAssemblyStatus.MissingContact;
            return false;
        }

        var locoFront = WorldContact(locomotive.Transform, locomotive.Contacts[0]);
        var locoRear = WorldContact(locomotive.Transform, locomotive.Contacts[1]);
        var carriageFront = WorldContact(carriage.Transform, carriage.Contacts[0]);
        var carriageRear = WorldContact(carriage.Transform, carriage.Contacts[1]);
        if (Math.Abs(Distance(locoFront, locoRear) - StaticTrainFixture.Wheelbase) > StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(Distance(carriageFront, carriageRear) - StaticTrainFixture.Wheelbase) > StaticTrainFixture.ConstraintTolerance)
            return false;
        if (Math.Abs(locoFront.Y - StraightRailFixture.CenterY) > StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(locoRear.Y - StraightRailFixture.CenterY) > StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(carriageFront.Y - StraightRailFixture.CenterY) > StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(carriageRear.Y - StraightRailFixture.CenterY) > StaticTrainFixture.ConstraintTolerance)
            return false;
        if (Math.Abs(locoFront.Z - StraightRailFixture.CenterZ) > StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(locoRear.Z - StraightRailFixture.CenterZ) > StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(carriageFront.Z - StraightRailFixture.CenterZ) > StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(carriageRear.Z - StraightRailFixture.CenterZ) > StaticTrainFixture.ConstraintTolerance)
            return false;
        var locoBox = WorldEnvelope(locomotive.Transform);
        var carriageBox = WorldEnvelope(carriage.Transform);
        if (Math.Abs(locoBox.MinX - carriageBox.MaxX - StaticTrainFixture.CouplerGap) >
            StaticTrainFixture.ConstraintTolerance)
            return false;
        if (BodiesOverlap(locomotive.Transform, carriage.Transform))
            return false;
        status = TrainAssemblyStatus.Formed;
        return true;
    }

    public static bool BodiesOverlap(CompositeTransform locomotive, CompositeTransform carriage)
    {
        var loco = WorldEnvelope(locomotive);
        var other = WorldEnvelope(carriage);
        return loco.MinX < other.MaxX && loco.MaxX > other.MinX &&
               loco.MinY < other.MaxY && loco.MaxY > other.MinY &&
               loco.MinZ < other.MaxZ && loco.MaxZ > other.MinZ;
    }

    public static VehicleEnvelopeBox WorldEnvelope(CompositeTransform transform)
    {
        var box = HollowShellFixture.Envelope;
        var corners = new[]
        {
            WorldPoint(transform, box.MinX, box.MinY, box.MinZ),
            WorldPoint(transform, box.MaxX, box.MinY, box.MinZ),
            WorldPoint(transform, box.MinX, box.MaxY, box.MinZ),
            WorldPoint(transform, box.MaxX, box.MaxY, box.MinZ),
            WorldPoint(transform, box.MinX, box.MinY, box.MaxZ),
            WorldPoint(transform, box.MaxX, box.MinY, box.MaxZ),
            WorldPoint(transform, box.MinX, box.MaxY, box.MaxZ),
            WorldPoint(transform, box.MaxX, box.MaxY, box.MaxZ)
        };
        return new VehicleEnvelopeBox(
            corners.Min(item => item.X),
            corners.Min(item => item.Y),
            corners.Min(item => item.Z),
            corners.Max(item => item.X),
            corners.Max(item => item.Y),
            corners.Max(item => item.Z));
    }

    public static bool TryResolveReference(
        PhysicalRailState rails,
        VoxelWorld world,
        (double X, double Y, double Z) frontContact,
        RailHeading heading,
        out RailPosition position)
    {
        position = default;
        ArgumentNullException.ThrowIfNull(rails);
        ArgumentNullException.ThrowIfNull(world);
        if (!TryResolveOwningSegment(rails, world, frontContact, heading, out var segment))
            return false;
        position = new RailPosition(
            segment.Id,
            StaticTrainFixture.AlongTrackDistance(frontContact.X, segment.Alignment),
            heading);
        return position.IsFinite;
    }

    public static bool TrySupportingSegment(
        PhysicalRailState rails,
        VoxelWorld world,
        (double X, double Y, double Z) contact,
        out EntityId segmentId)
    {
        if (!TryResolveOwningSegment(rails, world, contact, RailHeading.AlongAlignment, out var segment))
        {
            segmentId = default;
            return false;
        }

        segmentId = segment.Id;
        return true;
    }

    private static bool TryResolveOwningSegment(
        PhysicalRailState rails,
        VoxelWorld world,
        (double X, double Y, double Z) contact,
        RailHeading heading,
        out PhysicalRailSegment owner)
    {
        ArgumentNullException.ThrowIfNull(rails);
        ArgumentNullException.ThrowIfNull(world);
        owner = null!;
        var matches = rails.Segments
            .Where(segment =>
                MatchesAlignment(segment.Alignment, contact.Y, contact.Z) &&
                rails.TraversableWorldX(segment.Id, world).Any(interval =>
                    interval.Start <= contact.X && contact.X <= interval.End))
            .ToArray();
        if (matches.Length == 0)
            return false;
        if (matches.Length == 1)
        {
            owner = matches[0];
            return true;
        }

        if (!AreOneConnectedComponent(rails, matches.Select(segment => segment.Id)))
            return false;

        var interiors = matches
            .Where(segment =>
                segment.Alignment.StartX < contact.X && contact.X < segment.Alignment.EndX)
            .ToArray();
        if (interiors.Length == 1)
        {
            owner = interiors[0];
            return true;
        }

        if (interiors.Length > 1)
            return false;

        return TryOwnConnectedEndpoint(rails, matches, contact.X, heading, out owner);
    }

    private static bool AreOneConnectedComponent(PhysicalRailState rails, IEnumerable<EntityId> segmentIds)
    {
        var distinct = segmentIds.Distinct().ToArray();
        if (distinct.Length <= 1)
            return true;
        var seen = new HashSet<EntityId> { distinct[0] };
        var queue = new Queue<EntityId>();
        queue.Enqueue(distinct[0]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var other in distinct)
            {
                if (seen.Contains(other) || !rails.AreConnected(current, other))
                    continue;
                seen.Add(other);
                queue.Enqueue(other);
            }
        }

        return distinct.All(seen.Contains);
    }

    private static bool TryOwnConnectedEndpoint(
        PhysicalRailState rails,
        IReadOnlyList<PhysicalRailSegment> matches,
        double worldX,
        RailHeading heading,
        out PhysicalRailSegment owner)
    {
        owner = null!;
        foreach (var connection in rails.Connections)
        {
            var from = matches.FirstOrDefault(segment => segment.Id == connection.FromSegment);
            var to = matches.FirstOrDefault(segment => segment.Id == connection.ToSegment);
            if (from is null || to is null)
                continue;
            var fromX = EndWorldX(from.Alignment, connection.FromEnd);
            var toX = EndWorldX(to.Alignment, connection.ToEnd);
            if (Math.Abs(fromX - worldX) > StaticTrainFixture.ConstraintTolerance ||
                Math.Abs(toX - worldX) > StaticTrainFixture.ConstraintTolerance)
                continue;

            owner = heading == RailHeading.AlongAlignment
                ? SegmentAtTravelEnd(from, to, worldX)
                : SegmentAtTravelStart(from, to, worldX);
            return true;
        }

        return false;
    }

    private static PhysicalRailSegment SegmentAtTravelEnd(
        PhysicalRailSegment from,
        PhysicalRailSegment to,
        double worldX) =>
        Math.Abs(from.Alignment.EndX - worldX) <= StaticTrainFixture.ConstraintTolerance
            ? from
            : to;

    private static PhysicalRailSegment SegmentAtTravelStart(
        PhysicalRailSegment from,
        PhysicalRailSegment to,
        double worldX) =>
        Math.Abs(from.Alignment.StartX - worldX) <= StaticTrainFixture.ConstraintTolerance
            ? from
            : to;

    private static double EndWorldX(HorizontalStraightAlignment alignment, RailEnd end) =>
        end == RailEnd.Start ? alignment.StartX : alignment.EndX;

    public static bool HasContinuousSupport(
        PhysicalRailState rails,
        VoxelWorld world,
        IReadOnlyList<(double X, double Y, double Z)> contacts,
        RailPosition reference)
    {
        ArgumentNullException.ThrowIfNull(rails);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(contacts);
        if (contacts.Count == 0)
            return false;
        var referenceSegment = rails.Segments.FirstOrDefault(item => item.Id == reference.SegmentId);
        if (referenceSegment is null)
            return false;

        var seen = new HashSet<EntityId> { reference.SegmentId };
        var queue = new Queue<EntityId>();
        queue.Enqueue(reference.SegmentId);
        var intervals = new List<RailInterval>();
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var current = rails.Segments.First(item => item.Id == currentId);
            if (!MatchesAlignment(
                    current.Alignment,
                    referenceSegment.Alignment.StartY,
                    referenceSegment.Alignment.StartZ))
                continue;
            intervals.AddRange(rails.TraversableWorldX(currentId, world));
            foreach (var other in rails.Segments)
            {
                if (seen.Contains(other.Id) || !rails.AreConnected(currentId, other.Id))
                    continue;
                seen.Add(other.Id);
                queue.Enqueue(other.Id);
            }
        }

        var minX = contacts.Min(contact => contact.X);
        var maxX = contacts.Max(contact => contact.X);
        return RailIntervals.Merge(intervals).Any(interval => interval.Start <= minX && maxX <= interval.End);
    }

    public static bool TryValidateCouplerConnection(
        CompositeInstance locomotive,
        CompositeInstance carriage,
        LocalVoxelCoord locomotiveCell,
        LocalVoxelCoord carriageCell,
        out TrainAssemblyStatus status)
    {
        status = TrainAssemblyStatus.InvalidConnection;
        ArgumentNullException.ThrowIfNull(locomotive);
        ArgumentNullException.ThrowIfNull(carriage);
        if (locomotiveCell != StaticTrainFixture.LocomotiveRearCouplerCell ||
            carriageCell != StaticTrainFixture.CarriageFrontCouplerCell)
            return false;

        var locoFace = WorldPoint(
            locomotive.Transform,
            locomotiveCell.X + 1.0,
            locomotiveCell.Y + 0.5,
            locomotiveCell.Z + 0.5);
        var carriageFace = WorldPoint(
            carriage.Transform,
            carriageCell.X,
            carriageCell.Y + 0.5,
            carriageCell.Z + 0.5);
        if (Math.Abs(locoFace.X - carriageFace.X - StaticTrainFixture.CouplerGap) >
            StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(locoFace.Y - carriageFace.Y) > StaticTrainFixture.ConstraintTolerance ||
            Math.Abs(locoFace.Z - carriageFace.Z) > StaticTrainFixture.ConstraintTolerance)
            return false;

        var towardCarriage = (
            X: carriageFace.X - locoFace.X,
            Y: carriageFace.Y - locoFace.Y,
            Z: carriageFace.Z - locoFace.Z);
        var facing =
            locomotive.Transform.RightX * towardCarriage.X +
            locomotive.Transform.RightY * towardCarriage.Y +
            locomotive.Transform.RightZ * towardCarriage.Z;
        if (Math.Abs(facing - StaticTrainFixture.CouplerGap) > StaticTrainFixture.ConstraintTolerance)
            return false;

        status = TrainAssemblyStatus.Formed;
        return true;
    }

    private static bool MatchesAlignment(HorizontalStraightAlignment alignment, double y, double z) =>
        Math.Abs(y - alignment.StartY) <= StaticTrainFixture.ConstraintTolerance &&
        Math.Abs(z - alignment.StartZ) <= StaticTrainFixture.ConstraintTolerance;

    private static double Distance((double X, double Y, double Z) left, (double X, double Y, double Z) right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
