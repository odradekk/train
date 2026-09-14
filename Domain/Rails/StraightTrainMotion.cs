using Train.Content;
using Train.Domain.Behaviors;
using Train.Domain.Composites;
using Train.Domain.Simulation;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public static class StraightTrainMotion
{
    public static bool TryResolvePropulsion(
        CompositeInstance locomotive,
        VoxelCatalogSnapshot voxels,
        out PropulsionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(locomotive);
        ArgumentNullException.ThrowIfNull(voxels);
        parameters = PropulsionParameters.Default;
        foreach (var part in locomotive.Parts)
        {
            if (!voxels.TryGet(part.DefinitionId, out var entry) || entry.Definition.Propulsion is null)
                continue;
            parameters = entry.Definition.Propulsion;
            return true;
        }

        return false;
    }

    public static void IntegrateTraction(
        double speed,
        double targetSpeed,
        double acceleration,
        double dt,
        out double newSpeed,
        out double displacement)
    {
        if (!double.IsFinite(speed) || speed < 0)
        {
            newSpeed = 0;
            displacement = 0;
            return;
        }

        if (!double.IsFinite(targetSpeed) || targetSpeed <= 0 ||
            !double.IsFinite(acceleration) || acceleration <= 0 ||
            !double.IsFinite(dt) || dt <= 0)
        {
            newSpeed = Math.Min(Math.Max(0, speed), Math.Max(0, targetSpeed));
            displacement = 0;
            return;
        }

        if (speed > targetSpeed)
        {
            var room = speed - targetSpeed;
            var timeToCap = room / acceleration;
            if (timeToCap >= dt)
            {
                newSpeed = speed - acceleration * dt;
                displacement = (speed + newSpeed) * 0.5 * dt;
                return;
            }

            var decelDisp = (speed + targetSpeed) * 0.5 * timeToCap;
            var cruiseDisp = targetSpeed * (dt - timeToCap);
            newSpeed = targetSpeed;
            displacement = decelDisp + cruiseDisp;
            return;
        }

        if (speed < targetSpeed)
        {
            var room = targetSpeed - speed;
            var timeToCap = room / acceleration;
            if (timeToCap >= dt)
            {
                newSpeed = speed + acceleration * dt;
                displacement = (speed + newSpeed) * 0.5 * dt;
                return;
            }

            var accelDisp = (speed + targetSpeed) * 0.5 * timeToCap;
            var cruiseDisp = targetSpeed * (dt - timeToCap);
            newSpeed = targetSpeed;
            displacement = accelDisp + cruiseDisp;
            return;
        }

        newSpeed = speed;
        displacement = speed * dt;
    }

    public static bool TryAdvanceAssembly(
        TrainAssembly assembly,
        CompositeInstance locomotive,
        CompositeInstance carriage,
        PhysicalRailState rails,
        VoxelWorld world,
        VoxelCatalogSnapshot voxels,
        out TrainAssembly updated,
        out CompositeInstance newLocomotive,
        out CompositeInstance newCarriage,
        out TrainMotionStatus status)
    {
        updated = assembly;
        newLocomotive = locomotive;
        newCarriage = carriage;
        status = TrainMotionStatus.Advanced;
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(locomotive);
        ArgumentNullException.ThrowIfNull(carriage);
        ArgumentNullException.ThrowIfNull(rails);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(voxels);

        if (!assembly.Running)
            return true;
        if (!TryResolvePropulsion(locomotive, voxels, out var propulsion))
        {
            status = TrainMotionStatus.MissingPropulsion;
            return false;
        }

        IntegrateTraction(
            assembly.Speed,
            propulsion.TargetSpeed,
            propulsion.Acceleration,
            SimulationClock.StepDuration,
            out var requestedSpeed,
            out var requestedDx);

        if (!TryToContacts(locomotive, carriage, out var contacts, out var frontX))
        {
            status = TrainMotionStatus.VehicleConstraint;
            return false;
        }

        var intervals = StraightTrainSolver.ConnectedTraversableWorldX(rails, world, assembly.Reference);
        var allowed = StraightTrainSolver.MaxForwardTravel(contacts, intervals);
        var actualDx = Math.Min(Math.Max(0, requestedDx), allowed);
        var hitLimit = actualDx + StaticTrainFixture.ConstraintTolerance < requestedDx ||
                       allowed <= StaticTrainFixture.ConstraintTolerance;
        var newSpeed = hitLimit ? 0 : requestedSpeed;
        var running = assembly.Running && !hitLimit;
        if (actualDx <= 0 && !hitLimit)
        {
            updated = assembly.WithMotion(newSpeed, running);
            return true;
        }

        var newFront = frontX + actualDx;
        newLocomotive = locomotive.WithTransform(StaticTrainFixture.FacingPositiveX(newFront));
        newCarriage = carriage.WithTransform(
            StaticTrainFixture.FacingPositiveX(newFront - StaticTrainFixture.Wheelbase - StaticTrainFixture.CouplerGap - 2 * StaticTrainFixture.Overhang));
        if (!StraightTrainSolver.TryValidatePoses(newLocomotive, newCarriage, out _))
        {
            status = TrainMotionStatus.VehicleConstraint;
            return false;
        }

        if (!TryToContacts(newLocomotive, newCarriage, out var nextContacts, out _))
        {
            status = TrainMotionStatus.VehicleConstraint;
            return false;
        }

        if (!StraightTrainSolver.TryResolveReference(
                rails,
                world,
                nextContacts[0],
                RailHeading.AlongAlignment,
                out var reference,
                assembly.Reference.SegmentId))
        {
            status = TrainMotionStatus.InsufficientSupport;
            return false;
        }

        if (!StraightTrainSolver.HasContinuousSupport(rails, world, nextContacts, reference))
        {
            status = TrainMotionStatus.DisconnectedPath;
            return false;
        }

        updated = assembly.WithReferenceAndMotion(reference, newSpeed, running);
        return true;
    }

    private static bool TryToContacts(
        CompositeInstance locomotive,
        CompositeInstance carriage,
        out (double X, double Y, double Z)[] contacts,
        out double frontX)
    {
        contacts = Array.Empty<(double X, double Y, double Z)>();
        frontX = 0;
        if (!StraightTrainSolver.TryToWorldContact(locomotive, 0, out var locoFront) ||
            !StraightTrainSolver.TryToWorldContact(locomotive, 1, out var locoRear) ||
            !StraightTrainSolver.TryToWorldContact(carriage, 0, out var carriageFront) ||
            !StraightTrainSolver.TryToWorldContact(carriage, 1, out var carriageRear))
            return false;
        contacts =
        [
            (locoFront.X, locoFront.Y, locoFront.Z),
            (locoRear.X, locoRear.Y, locoRear.Z),
            (carriageFront.X, carriageFront.Y, carriageFront.Z),
            (carriageRear.X, carriageRear.Y, carriageRear.Z)
        ];
        frontX = locoFront.X;
        return true;
    }
}
