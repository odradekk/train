using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public static class StraightTrainSolver
{
    public static (double X, double Y, double Z) WorldPoint(CompositeTransform transform, double x, double y, double z) =>
        transform.TransformPoint(x, y, z);

    public static (double X, double Y, double Z) WorldContact(CompositeTransform transform, CompositeContactAnchor contact) =>
        WorldPoint(transform, contact.X, contact.Y, contact.Z);

    public static WorldContactQuery ToWorldContact(CompositeInstance vehicle, int index)
    {
        var contact = vehicle.Contacts[index];
        var bound = vehicle.PartAt(contact.BoundCell) ??
                    throw new InvalidOperationException($"Contact {index} is not bound on vehicle {vehicle.Id}.");
        var world = WorldContact(vehicle.Transform, contact);
        return new WorldContactQuery(world.X, world.Y, world.Z, contact.BoundCell, bound.Id);
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
        double frontContactX,
        RailHeading heading,
        out RailPosition position)
    {
        position = default;
        foreach (var segment in rails.Segments)
        {
            if (frontContactX < segment.Alignment.StartX || frontContactX > segment.Alignment.EndX)
                continue;
            position = new RailPosition(
                segment.Id,
                StaticTrainFixture.AlongTrackDistance(frontContactX, segment.Alignment),
                heading);
            return position.IsFinite;
        }

        return false;
    }

    private static double Distance((double X, double Y, double Z) left, (double X, double Y, double Z) right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
