using System.Globalization;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

/// <summary>
/// Read-only facts about one formal object: identity, ownership, durability, operating eligibility and any
/// failure reason the formal state already carries.
/// </summary>
public sealed record WorldEditorInspection(
    ObservedObject Object,
    string Identity,
    string Owner,
    string Durability,
    string Eligibility,
    IReadOnlyList<string> Failures)
{
    public bool IsPart => Object.Kind == ObservedObjectKind.VehiclePart;
    public string CoordinateSystem => IsPart ? "vehicle-local" : "world";

    public IEnumerable<string> Lines()
    {
        yield return "level " + (IsPart ? "part" : "whole");
        yield return "coordinates " + CoordinateSystem;
        yield return "identity " + Identity;
        yield return "owner " + Owner;
        yield return "durability " + Durability;
        yield return "eligibility " + Eligibility;
        yield return "failures " + (Failures.Count == 0 ? "none" : string.Join("; ", Failures));
    }

    public IEnumerable<string> LinesZh()
    {
        yield return "层级：" + (IsPart ? "车辆零件" : "整体");
        yield return "坐标系：" + (IsPart ? "车辆局部坐标" : "世界坐标");
        yield return "身份：" + Identity;
        yield return "所属：" + Owner;
        yield return "耐久：" + Durability;
        yield return "运行资格：" + Eligibility;
        yield return "失败原因：" + (Failures.Count == 0 ? "无" : string.Join("；", Failures));
    }
}

public sealed record WorldEditorListEntry(ObservedObject Object, string Label);

public sealed record WorldEditorVehicleHit(EntityId VehicleId, EntityId PartId, LocalVoxelCoord Cell, double Distance,
    ObservationVector WorldPosition, ObservationVector LocalPosition);

public static class WorldEditorInspector
{
    public static IReadOnlyList<WorldEditorListEntry> ListParts(VoxelDevelopmentSession session, EntityId vehicleId)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Vehicles.FirstOrDefault(vehicle => vehicle.Id == vehicleId)?.Parts
            .OrderBy(part => part.Id.Value)
            .Select(part => new WorldEditorListEntry(ObservedObject.VehiclePart(vehicleId, part.Id),
                $"零件 {part.Id.Value} {part.DefinitionId.Value} · 局部 ({part.Cell.X},{part.Cell.Y},{part.Cell.Z})"))
            .ToArray() ?? Array.Empty<WorldEditorListEntry>();
    }

    public static IReadOnlyList<WorldEditorListEntry> List(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var assemblies = session.QueryTrainAssemblies();
        var entries = new List<WorldEditorListEntry>();
        foreach (var vehicle in session.QueryComposites())
        {
            var assembly = assemblies.FirstOrDefault(item => item.VehicleIds.Contains(vehicle.Id));
            var owner = assembly is null ? "无编组" : "编组 " + Number(assembly.Id.Value);
            entries.Add(new WorldEditorListEntry(
                ObservedObject.Vehicle(vehicle.Id),
                $"车辆 {Number(vehicle.Id.Value)} {vehicle.DefinitionId.Value} · {owner} · {vehicle.OperationalCondition}"));
        }

        foreach (var segment in session.Rails.Segments.OrderBy(item => item.Id.Value))
        {
            entries.Add(new WorldEditorListEntry(
                ObservedObject.Segment(segment.Id),
                $"轨段 {Number(segment.Id.Value)} x {Number(segment.Alignment.StartX)}..{Number(segment.Alignment.EndX)}"));
        }

        return entries;
    }

    public static WorldEditorInspection? Inspect(VoxelDevelopmentSession session, ObservedObject item)
    {
        ArgumentNullException.ThrowIfNull(session);
        return item.Kind switch
        {
            ObservedObjectKind.Vehicle => InspectVehicle(session, item),
            ObservedObjectKind.VehiclePart => InspectPart(session, item),
            ObservedObjectKind.RailSegment => InspectSegment(session, item),
            _ => InspectCell(session, item)
        };
    }

    /// <summary>A rail voxel belongs to its segment; shared endpoint cells resolve to the lowest stable segment id.</summary>
    public static ObservedObject WorldTarget(VoxelDevelopmentSession session, WorldVoxelCoord cell) =>
        session.TryRailOwner(cell, out var segmentId)
            ? ObservedObject.Segment(segmentId)
            : ObservedObject.WorldCell(cell);


    private static WorldEditorInspection? InspectPart(VoxelDevelopmentSession session, ObservedObject item)
    {
        if (WorldEditorObserver.FindVehiclePart(session, item) is not { } found)
            return null;
        var (vehicle, part) = found;
        var damaged = session.QueryDamagedParts().FirstOrDefault(value => value.PartId == part.Id);
        return new WorldEditorInspection(item,
            $"part {part.Id.Value} {part.DefinitionId.Value} local {part.Cell.X},{part.Cell.Y},{part.Cell.Z} {part.Orientation}",
            $"vehicle {item.Id.Value}",
            $"{part.CurrentDurability}/{damaged?.MaxDurability ?? part.CurrentDurability}",
            session.QueryComposite(vehicle.Id)!.OperationalCondition.ToString(), Array.Empty<string>());
    }

    /// <summary>
    /// Nearest vehicle part hit by a ray, tested against each part cell in the transform the body is displayed with.
    /// Vehicles are not world occupancy, so they have no voxel collision shapes to query.
    /// </summary>
    public static WorldEditorVehicleHit? RaycastVehicles(
        VoxelDevelopmentSession session,
        TrainDisplaySample sample,
        ObservationVector origin,
        ObservationVector direction,
        double maxDistance)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sample);
        var length = direction.Length;
        if (length <= 0 || !double.IsFinite(length) || !double.IsFinite(maxDistance))
            return null;
        var unit = direction * (1 / length);
        WorldEditorVehicleHit? best = null;
        foreach (var vehicle in session.Vehicles.OrderBy(item => item.Id.Value))
        {
            var transform = WorldEditorObserver.VehicleFrame(session, sample, vehicle.Id)!.Transform;
            var local = transform.InverseTransformPoint(origin.X, origin.Y, origin.Z);
            var vector = transform.InverseTransformDirection(unit.X, unit.Y, unit.Z);
            var localOrigin = new ObservationVector(local.X, local.Y, local.Z);
            var localDirection = new ObservationVector(vector.X, vector.Y, vector.Z);
            foreach (var part in vehicle.Parts)
            {
                if (!TrySlab(localOrigin, localDirection, part.Cell, out var distance) ||
                    distance > maxDistance ||
                    (best is not null && distance >= best.Distance))
                    continue;
                var world = origin + unit * distance;
                // Coordinates use the formal occupancy transform, even while display poses interpolate.
                var point = vehicle.Transform.InverseTransformPoint(world.X, world.Y, world.Z);
                best = new WorldEditorVehicleHit(vehicle.Id, part.Id, part.Cell, distance, world,
                    new ObservationVector(point.X, point.Y, point.Z));
            }
        }

        return best;
    }

    private static WorldEditorInspection? InspectVehicle(VoxelDevelopmentSession session, ObservedObject item)
    {
        var vehicle = session.QueryComposite(item.Id);
        if (vehicle is null)
            return null;
        var assembly = session.QueryTrainAssemblies().FirstOrDefault(value => value.VehicleIds.Contains(item.Id));
        var damaged = session.QueryDamagedParts().Where(part => part.VehicleId == item.Id).ToArray();
        var current = vehicle.Parts.Sum(part => (long)part.CurrentDurability);
        var max = vehicle.Parts.Sum(part => (long)(damaged.FirstOrDefault(value => value.PartId == part.Id)?.MaxDurability ?? part.CurrentDurability));

        string owner;
        var failures = new List<string>();
        string eligibility;
        if (assembly is null)
        {
            owner = "no assembly";
            eligibility = $"{vehicle.OperationalCondition} · not in an assembly";
        }
        else
        {
            var index = assembly.VehicleIds.ToList().IndexOf(item.Id);
            var role = assembly.ActiveLocomotiveId == item.Id ? " active-locomotive" : "";
            owner = $"assembly {Number(assembly.Id.Value)} position {index + 1}/{assembly.VehicleIds.Count}{role}";
            var start = assembly.Running ? (TrainMotionStatus?)null : session.QueryStartEligibility(assembly.Id);
            eligibility = $"{vehicle.OperationalCondition} · {(start is { } status ? "start " + status : "running")} · speed {Number(assembly.Speed)}";
            if (assembly.HaltReason != TrainHaltReason.None)
                failures.Add("halt " + assembly.HaltReason);
            if (start is { } refusal && refusal != TrainMotionStatus.Started)
                failures.Add("start " + refusal);
            if (assembly.ActiveLocomotiveId is null)
                failures.Add("assembly has no active locomotive");
        }

        if (vehicle.Construction != ConstructionStatus.Valid)
            failures.Insert(0, "construction " + vehicle.Construction);
        if (vehicle.IdentityRetention != IdentityRetentionStatus.Retained)
            failures.Insert(0, "identity " + vehicle.IdentityRetention);
        if (session.Fault is { } fault)
            failures.Add(fault.EditId is { } editId
                ? $"simulation fault edit {Number(editId)}"
                : "simulation fault");
        var capabilities = vehicle.Capabilities.Count == 0 ? "none" : string.Join(',', vehicle.Capabilities);
        return new WorldEditorInspection(
            item,
            $"vehicle {Number(vehicle.Id.Value)} {vehicle.DefinitionId.Value} {vehicle.SizeClass} capabilities {capabilities}",
            owner,
            $"parts {vehicle.Parts.Count} total {Number(current)}/{Number(max)} damaged {damaged.Length}",
            eligibility,
            failures);
    }

    private static WorldEditorInspection? InspectSegment(VoxelDevelopmentSession session, ObservedObject item)
    {
        var segment = session.Rails.Segments.FirstOrDefault(value => value.Id == item.Id);
        if (segment is null)
            return null;
        var cells = session.Rails.OccupancyCells
            .Where(cell => cell.Supports.Any(support => support.SegmentId == item.Id))
            .Select(cell => session.Query(cell.Cell))
            .ToArray();
        var missing = cells.Where(cell => !cell.IsOccupied).Select(cell => cell.Position).ToArray();
        var current = cells.Sum(cell => (long)(cell.Voxel?.CurrentDurability ?? 0));
        var max = cells.Sum(cell => (long)(cell.MaxDurability ?? 0));
        var traversable = session.Rails.TraversableWorldX(item.Id, session.World);
        var connections = session.Rails.Connections
            .Where(value => value.FromSegment == item.Id || value.ToSegment == item.Id)
            .Select(value => $"{Number(value.FromSegment.Value)}:{value.FromEnd}->{Number(value.ToSegment.Value)}:{value.ToEnd}")
            .ToArray();
        var failures = new List<string>();
        if (missing.Length > 0)
        {
            var sample = string.Join(' ', missing.Take(3).Select(cell => $"({cell.X},{cell.Y},{cell.Z})"));
            failures.Add($"missing cells {missing.Length} {sample}{(missing.Length > 3 ? " …" : "")}");
        }

        if (!traversable.Any(interval => interval.HasPositiveLength))
            failures.Add("no traversable interval");
        var alignment = segment.Alignment;
        return new WorldEditorInspection(
            item,
            $"segment {Number(segment.Id.Value)} x {Number(alignment.StartX)}..{Number(alignment.EndX)} y {Number(alignment.StartY)} z {Number(alignment.StartZ)}",
            connections.Length == 0 ? "railway, no connections" : "railway connections " + string.Join(',', connections),
            $"cells {cells.Length - missing.Length}/{cells.Length} total {Number(current)}/{Number(max)}",
            traversable.Count == 0
                ? "not traversable"
                : "traversable x " + string.Join(' ', traversable.Select(interval => $"[{Number(interval.Start)},{Number(interval.End)}]")),
            failures);
    }

    private static WorldEditorInspection? InspectCell(VoxelDevelopmentSession session, ObservedObject item)
    {
        var query = session.Query(item.Cell);
        if (query.Voxel is not { } voxel || voxel.RuntimeId != item.RuntimeId)
            return null;
        var owners = new List<string>();
        if (session.HasRailVoxel(item.Cell) && session.Rails.TryGetOccupancy(item.Cell, out var occupancy))
            owners.Add("rail segment " + string.Join(',', occupancy.Supports.Select(support => Number(support.SegmentId.Value)).Distinct()));
        if (voxel.RuntimeId is { } runtime)
            owners.Add("detached part runtime " + Number(runtime.Value));
        var collision = session.QueryCollision(item.Cell);
        var hit = collision.Hits.FirstOrDefault();
        return new WorldEditorInspection(
            item,
            $"cell {item.Cell.X},{item.Cell.Y},{item.Cell.Z} {voxel.DefinitionId.Value} {voxel.Orientation}",
            owners.Count == 0 ? "world" : string.Join(" · ", owners),
            $"{Number(voxel.CurrentDurability)}/{Number(query.MaxDurability ?? voxel.CurrentDurability)}",
            hit is null ? "collision " + collision.Status : $"collision {collision.Status} {hit.Kind}",
            Array.Empty<string>());
    }

    private static bool TrySlab(ObservationVector origin, ObservationVector direction, LocalVoxelCoord cell, out double distance)
    {
        var near = double.NegativeInfinity;
        var far = double.PositiveInfinity;
        distance = 0;
        if (!Axis(origin.X, direction.X, cell.X, ref near, ref far) ||
            !Axis(origin.Y, direction.Y, cell.Y, ref near, ref far) ||
            !Axis(origin.Z, direction.Z, cell.Z, ref near, ref far) ||
            far < Math.Max(near, 0))
            return false;
        distance = Math.Max(near, 0);
        return true;
    }

    private static bool Axis(double origin, double direction, int min, ref double near, ref double far)
    {
        if (Math.Abs(direction) < 1e-12)
            return origin >= min && origin <= min + 1;
        var first = (min - origin) / direction;
        var second = (min + 1 - origin) / direction;
        near = Math.Max(near, Math.Min(first, second));
        far = Math.Min(far, Math.Max(first, second));
        return near <= far;
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
