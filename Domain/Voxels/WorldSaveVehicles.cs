using System.Text.Json;
using Train.Content;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;

namespace Train.Domain.Voxels;

public sealed class WorldSaveVehicle
{
    public EntityId Id { get; }
    public CompositeDefinitionId DefinitionId { get; }
    public string LastUsedRevision { get; }
    public CompositeArchetypeId ArchetypeId { get; }
    public string LastUsedArchetypeRevision { get; }
    public VehicleSizeClass SizeClass { get; }
    public CompositeTransform Transform { get; }
    public IReadOnlyList<WorldSavePart> Parts { get; }
    public IReadOnlyList<WorldSaveContact> Contacts { get; }
    public string? SaveLocalRevision { get; }

    public WorldSaveVehicle(
        EntityId id,
        CompositeDefinitionId definitionId,
        string lastUsedRevision,
        CompositeArchetypeId archetypeId,
        string lastUsedArchetypeRevision,
        VehicleSizeClass sizeClass,
        CompositeTransform transform,
        IReadOnlyList<WorldSavePart> parts,
        IReadOnlyList<WorldSaveContact> contacts,
        string? saveLocalRevision)
    {
        ArgumentException.ThrowIfNullOrEmpty(lastUsedRevision);
        ArgumentException.ThrowIfNullOrEmpty(lastUsedArchetypeRevision);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(contacts);
        Id = id;
        DefinitionId = definitionId;
        LastUsedRevision = lastUsedRevision;
        ArchetypeId = archetypeId;
        LastUsedArchetypeRevision = lastUsedArchetypeRevision;
        SizeClass = sizeClass;
        Transform = transform;
        Parts = parts;
        Contacts = contacts;
        SaveLocalRevision = saveLocalRevision;
    }

    public WorldSaveVehicle WithTransform(CompositeTransform transform) =>
        new(
            Id,
            DefinitionId,
            LastUsedRevision,
            ArchetypeId,
            LastUsedArchetypeRevision,
            SizeClass,
            transform,
            Parts,
            Contacts,
            SaveLocalRevision);
}

public readonly record struct WorldSavePart(
    EntityId Id,
    LocalVoxelCoord Cell,
    VoxelDefinitionId DefinitionId,
    VoxelOrientation Orientation,
    int Durability,
    string LastUsedRevision);

public readonly record struct WorldSaveContact(
    double X,
    double Y,
    double Z,
    LocalVoxelCoord BoundCell);

public sealed class WorldSaveLocalDefinition
{
    public string Revision { get; }
    public CompositeDefinition Definition { get; }

    public WorldSaveLocalDefinition(string revision, CompositeDefinition definition)
    {
        ArgumentException.ThrowIfNullOrEmpty(revision);
        ArgumentNullException.ThrowIfNull(definition);
        Revision = revision;
        Definition = definition;
    }
}

public sealed class WorldSaveRestore
{
    public VoxelWorld World { get; }
    public IReadOnlyList<CompositeInstance> Vehicles { get; }
    public ulong NextEntityId { get; }
    public ulong TransactionCount { get; }
    public PhysicalRailState? Rails { get; }
    public IReadOnlyList<TrainAssembly> Assemblies { get; }
    public ulong SimulationStep { get; }
    public IReadOnlyList<ScheduledTrainCommandQuery> ScheduledCommands { get; }
    public ulong ScheduledSequence { get; }
    public WorldEditQueueState Edits { get; }

    public WorldSaveRestore(
        VoxelWorld world,
        IReadOnlyList<CompositeInstance> vehicles,
        ulong nextEntityId,
        ulong transactionCount,
        PhysicalRailState? rails = null,
        IReadOnlyList<TrainAssembly>? assemblies = null,
        ulong simulationStep = 0,
        IReadOnlyList<ScheduledTrainCommandQuery>? scheduledCommands = null,
        ulong scheduledSequence = 0,
        WorldEditQueueState? edits = null)
    {
        Edits = edits ?? WorldEditQueueState.Initial;
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(vehicles);
        World = world;
        Vehicles = vehicles;
        NextEntityId = nextEntityId;
        TransactionCount = transactionCount;
        Rails = rails;
        Assemblies = assemblies ?? Array.Empty<TrainAssembly>();
        SimulationStep = simulationStep;
        ScheduledCommands = scheduledCommands ?? Array.Empty<ScheduledTrainCommandQuery>();
        ScheduledSequence = scheduledSequence;
    }
}

internal static partial class WorldSaveVehicleCodec
{
    public static IReadOnlyList<WorldSaveVehicle> Capture(IReadOnlyList<CompositeInstance> vehicles)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        return vehicles
            .OrderBy(item => item.Id.Value)
            .Select(vehicle => new WorldSaveVehicle(
                vehicle.Id,
                vehicle.DefinitionId,
                vehicle.LastUsedRevision,
                vehicle.ArchetypeId,
                vehicle.LastUsedArchetypeRevision,
                vehicle.SizeClass,
                vehicle.Transform,
                vehicle.Parts.Select(part => new WorldSavePart(
                    part.Id,
                    part.Cell,
                    part.DefinitionId,
                    part.Orientation,
                    part.CurrentDurability,
                    part.LastUsedRevision)).ToArray(),
                vehicle.Contacts.Select(contact => new WorldSaveContact(
                    contact.X,
                    contact.Y,
                    contact.Z,
                    contact.BoundCell)).ToArray(),
                vehicle.SaveLocalRevision))
            .ToArray();
    }

    public static IReadOnlyList<WorldSaveLocalDefinition> CaptureLocals(IReadOnlyList<CompositeInstance> vehicles)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        var locals = new Dictionary<string, WorldSaveLocalDefinition>(StringComparer.Ordinal);
        foreach (var vehicle in vehicles.OrderBy(item => item.Id.Value))
        {
            if (vehicle.SaveLocalDefinition is null || vehicle.SaveLocalRevision is null)
                continue;
            locals.TryAdd(
                vehicle.SaveLocalRevision,
                new WorldSaveLocalDefinition(vehicle.SaveLocalRevision, vehicle.SaveLocalDefinition));
        }

        return locals.Values
            .OrderBy(item => item.Revision, StringComparer.Ordinal)
            .ToArray();
    }

    public static void CollectVoxelIds(IEnumerable<WorldSaveVehicle> vehicles, IEnumerable<WorldSaveLocalDefinition> locals, HashSet<VoxelDefinitionId> ids)
    {
        foreach (var vehicle in vehicles)
        {
            foreach (var part in vehicle.Parts)
                ids.Add(part.DefinitionId);
        }

        foreach (var local in locals)
        {
            foreach (var voxel in local.Definition.Voxels)
                ids.Add(voxel.DefinitionId);
        }
    }

    public static void WriteVehicles(Utf8JsonWriter writer, IReadOnlyList<WorldSaveVehicle> vehicles)
    {
        writer.WriteStartArray("vehicles");
        foreach (var vehicle in vehicles)
        {
            writer.WriteStartObject();
            writer.WriteString("archetype_id", vehicle.ArchetypeId.Value);
            writer.WriteStartArray("contacts");
            foreach (var contact in vehicle.Contacts)
                WriteContact(writer, contact);
            writer.WriteEndArray();
            writer.WriteString("definition_id", vehicle.DefinitionId.Value);
            writer.WriteNumber("id", vehicle.Id.Value);
            writer.WriteString("last_used_archetype_revision", vehicle.LastUsedArchetypeRevision);
            writer.WriteString("last_used_revision", vehicle.LastUsedRevision);
            writer.WriteStartArray("parts");
            foreach (var part in vehicle.Parts)
            {
                writer.WriteStartObject();
                writer.WriteString("definition_id", part.DefinitionId.Value);
                writer.WriteNumber("durability", part.Durability);
                writer.WriteNumber("id", part.Id.Value);
                writer.WriteString("last_used_revision", part.LastUsedRevision);
                writer.WriteString("orientation", part.Orientation.ToString());
                writer.WriteNumber("x", part.Cell.X);
                writer.WriteNumber("y", part.Cell.Y);
                writer.WriteNumber("z", part.Cell.Z);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (vehicle.SaveLocalRevision is not null)
                writer.WriteString("save_local_revision", vehicle.SaveLocalRevision);
            writer.WriteString("size_class", VehicleSize.ToWire(vehicle.SizeClass));
            WriteTransform(writer, vehicle.Transform);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public static void WriteLocals(Utf8JsonWriter writer, IReadOnlyList<WorldSaveLocalDefinition> locals)
    {
        writer.WriteStartArray("save_local_definitions");
        foreach (var local in locals)
        {
            writer.WriteStartObject();
            writer.WriteString("archetype_id", local.Definition.ArchetypeId.Value);
            writer.WriteStartArray("contacts");
            foreach (var contact in local.Definition.Contacts)
            {
                WriteContact(writer, new WorldSaveContact(contact.X, contact.Y, contact.Z, contact.BoundCell));
            }

            writer.WriteEndArray();
            writer.WriteString("description", local.Definition.Description);
            writer.WriteString("id", local.Definition.Id.Value);
            writer.WriteString("name", local.Definition.Name);
            writer.WriteString("revision", local.Revision);
            writer.WriteString("size_class", VehicleSize.ToWire(local.Definition.SizeClass));
            writer.WriteStartArray("voxels");
            foreach (var voxel in local.Definition.Voxels)
            {
                writer.WriteStartObject();
                writer.WriteString("definition_id", voxel.DefinitionId.Value);
                writer.WriteString("orientation", voxel.Orientation.ToString());
                writer.WriteNumber("x", voxel.Cell.X);
                writer.WriteNumber("y", voxel.Cell.Y);
                writer.WriteNumber("z", voxel.Cell.Z);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public static bool TryReadVehicles(
        JsonElement element,
        out IReadOnlyList<WorldSaveVehicle> vehicles,
        out WorldSaveStatus status)
    {
        vehicles = Array.Empty<WorldSaveVehicle>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var list = new List<WorldSaveVehicle>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadVehicle(item, out var vehicle, out status))
                return false;
            list.Add(vehicle);
        }

        vehicles = list;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    public static bool TryReadLocals(
        JsonElement element,
        out IReadOnlyList<WorldSaveLocalDefinition> locals,
        out WorldSaveStatus status)
    {
        locals = Array.Empty<WorldSaveLocalDefinition>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var list = new List<WorldSaveLocalDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadLocal(item, out var local, out status))
                return false;
            if (!seen.Add(local.Revision))
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }

            list.Add(local);
        }

        locals = list;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    public static bool TryRestoreVehicles(
        WorldSaveDocument document,
        VoxelCatalogSnapshot voxels,
        CompositeCatalogSnapshot composites,
        VoxelWorld world,
        out IReadOnlyList<CompositeInstance> vehicles,
        out WorldSaveStatus status)
    {
        vehicles = Array.Empty<CompositeInstance>();
        status = WorldSaveStatus.InvalidDelta;
        var locals = document.SaveLocalDefinitions.ToDictionary(item => item.Revision, StringComparer.Ordinal);
        var reconstructed = new List<CompositeInstance>();
        foreach (var saved in document.Vehicles)
        {
            CompositeDefinition? saveLocal = null;
            if (saved.SaveLocalRevision is not null)
            {
                if (!locals.TryGetValue(saved.SaveLocalRevision, out var local))
                {
                    status = WorldSaveStatus.MissingDefinition;
                    return false;
                }

                saveLocal = local.Definition;
                if (saveLocal.Id != saved.DefinitionId ||
                    saveLocal.ArchetypeId != saved.ArchetypeId ||
                    saveLocal.SizeClass != saved.SizeClass)
                {
                    status = WorldSaveStatus.InvalidDelta;
                    return false;
                }
            }

            var parts = new List<CompositePart>(saved.Parts.Count);
            var cells = new HashSet<LocalVoxelCoord>();
            foreach (var part in saved.Parts)
            {
                if (part.Durability <= 0 ||
                    !cells.Add(part.Cell) ||
                    string.IsNullOrEmpty(part.LastUsedRevision) ||
                    !part.Orientation.IsOrthogonal)
                {
                    status = WorldSaveStatus.InvalidDelta;
                    return false;
                }

                parts.Add(new CompositePart(
                    part.Id,
                    part.Cell,
                    part.DefinitionId,
                    part.Orientation,
                    part.Durability,
                    part.LastUsedRevision));
            }

            var contacts = new List<CompositeContactAnchor>(saved.Contacts.Count);
            foreach (var contact in saved.Contacts)
            {
                if (!double.IsFinite(contact.X) || !double.IsFinite(contact.Y) || !double.IsFinite(contact.Z))
                {
                    status = WorldSaveStatus.InvalidDelta;
                    return false;
                }

                if (parts.All(part => part.Cell != contact.BoundCell))
                {
                    status = WorldSaveStatus.InvalidDelta;
                    return false;
                }

                contacts.Add(new CompositeContactAnchor(contact.X, contact.Y, contact.Z, contact.BoundCell));
            }

            if (!saved.Transform.IsFinite || !saved.Transform.IsValid)
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }

            reconstructed.Add(new CompositeInstance(
                saved.Id,
                saved.DefinitionId,
                saved.LastUsedRevision,
                saved.ArchetypeId,
                saved.LastUsedArchetypeRevision,
                saved.SizeClass,
                saved.Transform,
                parts,
                contacts,
                saved.SaveLocalRevision,
                saveLocal));
        }

        if (!VoxelWorldReloadAdapter.TryAdaptVehicles(reconstructed, voxels, composites, out var adapted, out status))
            return false;
        if (!TryValidateIdentities(world, adapted, document.NextEntityId, document.TransactionCount, out status))
            return false;
        vehicles = adapted;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    public static bool ContainsNonFiniteLiteral(string json) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            json,
            @"(?<=[:\[,])\s*(-?Infinity|NaN)\s*(?=[,\]}])");

    internal static bool TryValidateIdentities(
        VoxelWorld world,
        IReadOnlyList<CompositeInstance> vehicles,
        ulong nextEntityId,
        ulong transactionCount,
        out WorldSaveStatus status,
        IEnumerable<ulong>? extraIds = null)
    {
        status = WorldSaveStatus.InvalidDelta;
        var ids = new HashSet<ulong>();
        ulong maxId = 0;
        foreach (var extra in extraIds ?? Array.Empty<ulong>())
        {
            if (extra == 0 || !ids.Add(extra))
                return false;
            maxId = Math.Max(maxId, extra);
        }

        foreach (var vehicle in vehicles)
        {
            if (!ids.Add(vehicle.Id.Value))
                return false;
            maxId = Math.Max(maxId, vehicle.Id.Value);
            foreach (var part in vehicle.Parts)
            {
                if (!ids.Add(part.Id.Value))
                    return false;
                maxId = Math.Max(maxId, part.Id.Value);
            }
        }

        foreach (var (_, voxel) in world.OccupiedVoxels())
        {
            if (voxel.RuntimeId is not { } runtime)
                continue;
            if (!ids.Add(runtime.Value))
                return false;
            maxId = Math.Max(maxId, runtime.Value);
        }

        if (nextEntityId == 0 ||
            (maxId != 0 && nextEntityId <= maxId) ||
            transactionCount == ulong.MaxValue)
            return false;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadVehicle(JsonElement element, out WorldSaveVehicle vehicle, out WorldSaveStatus status)
    {
        vehicle = null!;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong id = 0;
        string? definitionText = null;
        string? lastUsed = null;
        string? archetypeText = null;
        string? lastUsedArchetype = null;
        string? sizeText = null;
        string? saveLocalRevision = null;
        CompositeTransform transform = default;
        IReadOnlyList<WorldSavePart>? parts = null;
        IReadOnlyList<WorldSaveContact>? contacts = null;
        var hasId = false;
        var hasTransform = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "id":
                    if (!TryReadEntityId(property.Value, out id))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasId = true;
                    break;
                case "definition_id":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    definitionText = property.Value.GetString();
                    break;
                case "last_used_revision":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    lastUsed = property.Value.GetString();
                    break;
                case "archetype_id":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    archetypeText = property.Value.GetString();
                    break;
                case "last_used_archetype_revision":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    lastUsedArchetype = property.Value.GetString();
                    break;
                case "size_class":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    sizeText = property.Value.GetString();
                    break;
                case "save_local_revision":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    saveLocalRevision = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(saveLocalRevision))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    break;
                case "transform":
                    if (!TryReadTransform(property.Value, out transform, out status))
                        return false;
                    hasTransform = true;
                    break;
                case "parts":
                    if (!TryReadParts(property.Value, out parts, out status))
                        return false;
                    break;
                case "contacts":
                    if (!TryReadContacts(property.Value, out contacts, out status))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        if (!hasId || definitionText is null || lastUsed is null || archetypeText is null ||
            lastUsedArchetype is null || sizeText is null || !hasTransform || parts is null || contacts is null)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        try
        {
            if (!VehicleSize.TryParse(sizeText, out var size))
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }

            vehicle = new WorldSaveVehicle(
                new EntityId(id),
                CompositeDefinitionId.Parse(definitionText),
                lastUsed,
                CompositeArchetypeId.Parse(archetypeText),
                lastUsedArchetype,
                size,
                transform,
                parts,
                contacts,
                saveLocalRevision);
            status = WorldSaveStatus.Loaded;
            return true;
        }
        catch (ArgumentException)
        {
            status = WorldSaveStatus.InvalidDelta;
            return false;
        }
    }

    private static bool TryReadLocal(JsonElement element, out WorldSaveLocalDefinition local, out WorldSaveStatus status)
    {
        local = null!;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? revision = null;
        string? idText = null;
        string? name = null;
        string? description = null;
        string? archetypeText = null;
        string? sizeText = null;
        IReadOnlyList<CompositeVoxelSpec>? voxels = null;
        IReadOnlyList<WorldSaveContact>? contacts = null;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "revision":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    revision = property.Value.GetString();
                    break;
                case "id":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    idText = property.Value.GetString();
                    break;
                case "name":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    name = property.Value.GetString();
                    break;
                case "description":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    description = property.Value.GetString();
                    break;
                case "archetype_id":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    archetypeText = property.Value.GetString();
                    break;
                case "size_class":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    sizeText = property.Value.GetString();
                    break;
                case "voxels":
                    if (!TryReadLocalVoxels(property.Value, out voxels, out status))
                        return false;
                    break;
                case "contacts":
                    if (!TryReadContacts(property.Value, out contacts, out status))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        if (revision is null || idText is null || name is null || description is null ||
            archetypeText is null || sizeText is null || voxels is null || contacts is null)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        try
        {
            if (!VehicleSize.TryParse(sizeText, out var size))
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }

            var definition = new CompositeDefinition(
                CompositeDefinitionId.Parse(idText),
                name,
                description,
                CompositeArchetypeId.Parse(archetypeText),
                size,
                voxels,
                contacts.Select(contact => new CompositeContactAnchor(contact.X, contact.Y, contact.Z, contact.BoundCell)).ToArray());
            var computed = CompositeRevisions.ComputeSemanticRevision(definition);
            if (!string.Equals(computed, revision, StringComparison.Ordinal))
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }

            local = new WorldSaveLocalDefinition(revision, definition);
            status = WorldSaveStatus.Loaded;
            return true;
        }
        catch (ArgumentException)
        {
            status = WorldSaveStatus.InvalidDelta;
            return false;
        }
    }

    private static bool TryReadParts(JsonElement element, out IReadOnlyList<WorldSavePart> parts, out WorldSaveStatus status)
    {
        parts = Array.Empty<WorldSavePart>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var list = new List<WorldSavePart>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadPart(item, out var part, out status))
                return false;
            list.Add(part);
        }

        parts = list;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadPart(JsonElement element, out WorldSavePart part, out WorldSaveStatus status)
    {
        part = default;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        ulong id = 0;
        var x = 0;
        var y = 0;
        var z = 0;
        var durability = 0;
        string? definitionText = null;
        string? orientationText = null;
        string? revision = null;
        var hasId = false;
        var hasX = false;
        var hasY = false;
        var hasZ = false;
        var hasDurability = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "id":
                    if (!TryReadEntityId(property.Value, out id))
                    {
                        status = WorldSaveStatus.InvalidDelta;
                        return false;
                    }

                    hasId = true;
                    break;
                case "x":
                case "y":
                case "z":
                case "durability":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number))
                        return false;
                    if (property.Name == "x")
                    {
                        x = number;
                        hasX = true;
                    }
                    else if (property.Name == "y")
                    {
                        y = number;
                        hasY = true;
                    }
                    else if (property.Name == "z")
                    {
                        z = number;
                        hasZ = true;
                    }
                    else
                    {
                        durability = number;
                        hasDurability = true;
                    }

                    break;
                case "definition_id":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    definitionText = property.Value.GetString();
                    break;
                case "orientation":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    orientationText = property.Value.GetString();
                    break;
                case "last_used_revision":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    revision = property.Value.GetString();
                    break;
                default:
                    return false;
            }
        }

        if (!hasId || !hasX || !hasY || !hasZ || !hasDurability || definitionText is null ||
            orientationText is null || revision is null)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        try
        {
            if (!VoxelOrientation.TryParse(orientationText, out var orientation) || !orientation.IsOrthogonal)
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }

            part = new WorldSavePart(
                new EntityId(id),
                new LocalVoxelCoord(x, y, z),
                VoxelDefinitionId.Parse(definitionText),
                orientation,
                durability,
                revision);
            status = WorldSaveStatus.Loaded;
            return true;
        }
        catch (ArgumentException)
        {
            status = WorldSaveStatus.InvalidDelta;
            return false;
        }
    }

    private static bool TryReadLocalVoxels(
        JsonElement element,
        out IReadOnlyList<CompositeVoxelSpec> voxels,
        out WorldSaveStatus status)
    {
        voxels = Array.Empty<CompositeVoxelSpec>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var list = new List<CompositeVoxelSpec>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadLocalVoxel(item, out var spec, out status))
                return false;
            list.Add(spec);
        }

        voxels = list;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadLocalVoxel(JsonElement element, out CompositeVoxelSpec spec, out WorldSaveStatus status)
    {
        spec = null!;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var x = 0;
        var y = 0;
        var z = 0;
        string? definitionText = null;
        string? orientationText = null;
        var hasX = false;
        var hasY = false;
        var hasZ = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "x":
                case "y":
                case "z":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number))
                        return false;
                    if (property.Name == "x")
                    {
                        x = number;
                        hasX = true;
                    }
                    else if (property.Name == "y")
                    {
                        y = number;
                        hasY = true;
                    }
                    else
                    {
                        z = number;
                        hasZ = true;
                    }

                    break;
                case "definition_id":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    definitionText = property.Value.GetString();
                    break;
                case "orientation":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return false;
                    orientationText = property.Value.GetString();
                    break;
                default:
                    return false;
            }
        }

        if (!hasX || !hasY || !hasZ || definitionText is null || orientationText is null)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        try
        {
            if (!VoxelOrientation.TryParse(orientationText, out var orientation))
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }

            spec = new CompositeVoxelSpec(new LocalVoxelCoord(x, y, z), VoxelDefinitionId.Parse(definitionText), orientation);
            status = WorldSaveStatus.Loaded;
            return true;
        }
        catch (ArgumentException)
        {
            status = WorldSaveStatus.InvalidDelta;
            return false;
        }
    }

    private static bool TryReadContacts(JsonElement element, out IReadOnlyList<WorldSaveContact> contacts, out WorldSaveStatus status)
    {
        contacts = Array.Empty<WorldSaveContact>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return false;
        var list = new List<WorldSaveContact>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadContact(item, out var contact, out status))
                return false;
            list.Add(contact);
        }

        contacts = list;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadContact(JsonElement element, out WorldSaveContact contact, out WorldSaveStatus status)
    {
        contact = default;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double x = 0;
        double y = 0;
        double z = 0;
        var bx = 0;
        var by = 0;
        var bz = 0;
        var hasX = false;
        var hasY = false;
        var hasZ = false;
        var hasBx = false;
        var hasBy = false;
        var hasBz = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            switch (property.Name)
            {
                case "x":
                case "y":
                case "z":
                    if (!TryReadFiniteDouble(property.Value, out var value, out status))
                        return false;
                    if (property.Name == "x")
                    {
                        x = value;
                        hasX = true;
                    }
                    else if (property.Name == "y")
                    {
                        y = value;
                        hasY = true;
                    }
                    else
                    {
                        z = value;
                        hasZ = true;
                    }

                    break;
                case "bound_x":
                case "bound_y":
                case "bound_z":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number))
                        return false;
                    if (property.Name == "bound_x")
                    {
                        bx = number;
                        hasBx = true;
                    }
                    else if (property.Name == "bound_y")
                    {
                        by = number;
                        hasBy = true;
                    }
                    else
                    {
                        bz = number;
                        hasBz = true;
                    }

                    break;
                default:
                    return false;
            }
        }

        if (!hasX || !hasY || !hasZ || !hasBx || !hasBy || !hasBz)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        contact = new WorldSaveContact(x, y, z, new LocalVoxelCoord(bx, by, bz));
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadTransform(JsonElement element, out CompositeTransform transform, out WorldSaveStatus status)
    {
        transform = default;
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double ox = 0, oy = 0, oz = 0, rx = 0, ry = 0, rz = 0, ux = 0, uy = 0, uz = 0;
        var has = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            if (!TryReadFiniteDouble(property.Value, out var value, out status))
                return false;
            switch (property.Name)
            {
                case "origin_x": ox = value; has++; break;
                case "origin_y": oy = value; has++; break;
                case "origin_z": oz = value; has++; break;
                case "right_x": rx = value; has++; break;
                case "right_y": ry = value; has++; break;
                case "right_z": rz = value; has++; break;
                case "up_x": ux = value; has++; break;
                case "up_y": uy = value; has++; break;
                case "up_z": uz = value; has++; break;
                default:
                    return false;
            }
        }

        if (has != 9)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        if (!CompositeTransform.TryRestore(ox, oy, oz, rx, ry, rz, ux, uy, uz, out transform))
        {
            status = WorldSaveStatus.InvalidDelta;
            return false;
        }

        status = WorldSaveStatus.Loaded;
        return true;
    }

    internal static bool TryReadEntityId(JsonElement element, out ulong id)
    {
        id = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out id) && id != 0;
    }

    internal static bool TryReadFiniteDouble(JsonElement element, out double value, out WorldSaveStatus status)
    {
        value = 0;
        status = WorldSaveStatus.InvalidDelta;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out value) || !double.IsFinite(value))
            return false;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static void WriteTransform(Utf8JsonWriter writer, CompositeTransform transform)
    {
        writer.WriteStartObject("transform");
        WriteDouble(writer, "origin_x", transform.OriginX);
        WriteDouble(writer, "origin_y", transform.OriginY);
        WriteDouble(writer, "origin_z", transform.OriginZ);
        WriteDouble(writer, "right_x", transform.RightX);
        WriteDouble(writer, "right_y", transform.RightY);
        WriteDouble(writer, "right_z", transform.RightZ);
        WriteDouble(writer, "up_x", transform.UpX);
        WriteDouble(writer, "up_y", transform.UpY);
        WriteDouble(writer, "up_z", transform.UpZ);
        writer.WriteEndObject();
    }

    private static void WriteContact(Utf8JsonWriter writer, WorldSaveContact contact)
    {
        writer.WriteStartObject();
        writer.WriteNumber("bound_x", contact.BoundCell.X);
        writer.WriteNumber("bound_y", contact.BoundCell.Y);
        writer.WriteNumber("bound_z", contact.BoundCell.Z);
        WriteDouble(writer, "x", contact.X);
        WriteDouble(writer, "y", contact.Y);
        WriteDouble(writer, "z", contact.Z);
        writer.WriteEndObject();
    }

    internal static void WriteDouble(Utf8JsonWriter writer, string name, double value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(VoxelDefinition.FormatMass(value));
    }
}
