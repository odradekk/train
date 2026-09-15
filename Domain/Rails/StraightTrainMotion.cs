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
        foreach (var part in locomotive.Parts.OrderBy(item => item.Cell))
        {
            if (!voxels.TryGet(part.DefinitionId, out var entry) || entry.Definition.Propulsion is null)
                continue;
            parameters = entry.Definition.Propulsion;
            return true;
        }

        return false;
    }

    public static bool TryResolveLocomotive(
        CompositeInstance locomotive,
        VoxelCatalogSnapshot voxels,
        out PropulsionParameters parameters,
        out TrainMotionStatus status)
    {
        parameters = PropulsionParameters.Default;
        status = TrainMotionStatus.MissingPropulsion;
        ArgumentNullException.ThrowIfNull(locomotive);
        ArgumentNullException.ThrowIfNull(voxels);
        var capabilities = TrainAssemblyRules.LiveCapabilities(locomotive, voxels);
        if (!TrainAssemblyRules.HasCapability(capabilities, CapabilityTypes.Propulsion) ||
            !TryResolvePropulsion(locomotive, voxels, out parameters))
            return false;
        if (!TrainAssemblyRules.HasCapability(capabilities, CapabilityTypes.Control))
        {
            status = TrainMotionStatus.MissingControl;
            return false;
        }

        status = TrainMotionStatus.Advanced;
        return true;
    }

    public static bool TryResolveTrackContact(
        IEnumerable<CompositeInstance> members,
        VoxelCatalogSnapshot voxels,
        out TrackContactParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(voxels);
        parameters = TrackContactParameters.Default;
        foreach (var vehicle in members)
        {
            ArgumentNullException.ThrowIfNull(vehicle);
            foreach (var part in vehicle.Parts.OrderBy(item => item.Cell))
            {
                if (!voxels.TryGet(part.DefinitionId, out var entry) || entry.Definition.TrackContact is null)
                    continue;
                parameters = entry.Definition.TrackContact;
                return true;
            }
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

    public static void IntegrateCoast(
        double speed,
        double deceleration,
        double dt,
        out double newSpeed,
        out double displacement)
    {
        if (!double.IsFinite(speed) || speed <= 0)
        {
            newSpeed = 0;
            displacement = 0;
            return;
        }

        if (!double.IsFinite(deceleration) || deceleration <= 0 ||
            !double.IsFinite(dt) || dt <= 0)
        {
            newSpeed = speed;
            displacement = 0;
            return;
        }

        var timeToZero = speed / deceleration;
        if (timeToZero >= dt)
        {
            newSpeed = speed - deceleration * dt;
            displacement = (speed + newSpeed) * 0.5 * dt;
            return;
        }

        newSpeed = 0;
        displacement = speed * 0.5 * timeToZero;
    }

    public static bool TryAdvanceAssembly(
        TrainAssembly assembly,
        IReadOnlyList<CompositeInstance> members,
        PhysicalRailState rails,
        VoxelWorld world,
        VoxelCatalogSnapshot voxels,
        out TrainAssembly updated,
        out IReadOnlyList<CompositeInstance> newMembers,
        out TrainMotionStatus status)
    {
        updated = assembly;
        newMembers = members;
        status = TrainMotionStatus.Advanced;
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(rails);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(voxels);
        if (members.Count == 0 || members.Count != assembly.VehicleIds.Count)
        {
            status = TrainMotionStatus.NoAssembly;
            return false;
        }

        if (!assembly.Running && assembly.Speed <= 0)
            return true;

        double requestedSpeed;
        double requestedDx;
        if (assembly.Running)
        {
            if (assembly.ActiveLocomotiveId is not { } locoId)
            {
                status = TrainMotionStatus.MissingPropulsion;
                return false;
            }

            var locomotive = members.FirstOrDefault(item => item.Id == locoId);
            if (locomotive is null || !TryResolveLocomotive(locomotive, voxels, out var propulsion, out status))
                return false;

            IntegrateTraction(
                assembly.Speed,
                propulsion.TargetSpeed,
                propulsion.Acceleration,
                SimulationClock.StepDuration,
                out requestedSpeed,
                out requestedDx);
        }
        else
        {
            if (!TryResolveTrackContact(members, voxels, out var trackContact))
            {
                status = TrainMotionStatus.VehicleConstraint;
                return false;
            }

            IntegrateCoast(
                assembly.Speed,
                trackContact.CoastDeceleration,
                SimulationClock.StepDuration,
                out requestedSpeed,
                out requestedDx);
        }

        if (!TryCollectContacts(members, out var contacts, out var fronts))
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

        var moved = new List<CompositeInstance>(members.Count);
        for (var index = 0; index < members.Count; index++)
            moved.Add(members[index].WithTransform(StaticTrainFixture.FacingPositiveX(fronts[index] + actualDx)));

        if (!TryValidateMovedMembers(assembly, moved))
        {
            status = TrainMotionStatus.VehicleConstraint;
            return false;
        }

        if (!TryCollectContacts(moved, out var nextContacts, out _))
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
        newMembers = moved;
        return true;
    }

    private static bool TryCollectContacts(
        IReadOnlyList<CompositeInstance> members,
        out (double X, double Y, double Z)[] contacts,
        out double[] fronts)
    {
        var collected = new List<(double X, double Y, double Z)>();
        fronts = new double[members.Count];
        for (var index = 0; index < members.Count; index++)
        {
            if (!StraightTrainSolver.TryCollectLiveContacts(members[index], out var vehicleContacts, out var front, out _))
            {
                contacts = Array.Empty<(double X, double Y, double Z)>();
                fronts = Array.Empty<double>();
                return false;
            }

            fronts[index] = front.X;
            collected.AddRange(vehicleContacts);
        }

        contacts = collected.ToArray();
        return true;
    }

    private static bool TryValidateMovedMembers(
        TrainAssembly assembly,
        IReadOnlyList<CompositeInstance> members)
    {
        if (members.Count == 2 && assembly.Connections.Count == 1)
            return StraightTrainSolver.TryValidatePoses(members[0], members[1], out _);

        return members.All(member =>
            member.Transform.IsValid &&
            StraightTrainSolver.TryCollectLiveContacts(member, out _, out _, out _));
    }
}
