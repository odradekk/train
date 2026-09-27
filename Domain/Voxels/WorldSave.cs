using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;

namespace Train.Domain.Voxels;

public enum WorldSaveStatus
{
    Saved,
    Loaded,
    WriteFailed,
    ReadFailed,
    Truncated,
    UnknownFormat,
    UnknownGenerator,
    IncompatibleBase,
    MissingDefinition,
    InvalidDelta,
    Occupied,
    OutOfBounds,
    InvalidName,
    MismatchedId,
    NotFound,
    Unavailable,
    UnsupportedVehicles,
    UnsupportedIdentities,
    UnsupportedRailway,
    UnknownSource
}

public sealed record WorldSaveResult(WorldSaveStatus Status, Guid? SaveId = null, string? DisplayName = null)
{
    public bool Succeeded => Status is WorldSaveStatus.Saved or WorldSaveStatus.Loaded;
}

public readonly record struct WorldSaveInstance(
    WorldVoxelCoord Position,
    VoxelDefinitionId DefinitionId,
    VoxelOrientation Orientation,
    int Durability,
    EntityId? RuntimeId = null);

public readonly record struct WorldSaveDefinitionRef(
    VoxelDefinitionId DefinitionId,
    string SemanticRevision,
    int MaxDurability);

public sealed class WorldSaveDocument
{
    public const int CurrentFormatVersion = 3;
    public const int PredecessorFormatVersion = 2;
    public const string DefaultDisplayName = "Development World";
    public const int DisplayNameMinLength = 1;
    public const int DisplayNameMaxLength = 64;

    public int FormatVersion { get; }
    public Guid Id { get; }
    public string DisplayName { get; }
    public ulong Seed { get; }
    public string GeneratorVersion { get; }
    public VoxelWorldBounds Bounds { get; }
    public int ChunkSize { get; }
    public string BaseLayout { get; }
    public IReadOnlyList<WorldSaveDefinitionRef> ExternalDefinitions { get; }
    public IReadOnlyList<WorldVoxelCoord> Removed { get; }
    public IReadOnlyList<WorldSaveInstance> Added { get; }
    public IReadOnlyList<WorldSaveInstance> Changed { get; }
    public ulong NextEntityId { get; }
    public ulong TransactionCount { get; }
    public IReadOnlyList<WorldSaveVehicle> Vehicles { get; }
    public IReadOnlyList<WorldSaveLocalDefinition> SaveLocalDefinitions { get; }
    public WorldSaveRailway Railway { get; }
    public IReadOnlyList<TrainAssembly> Assemblies { get; }
    public ulong SimulationStep { get; }
    public IReadOnlyList<ScheduledTrainCommandQuery> ScheduledCommands { get; }
    public ulong ScheduledSequence { get; }
    public WorldSource? Source { get; }

    public WorldSaveDocument(
        int formatVersion,
        Guid id,
        string displayName,
        ulong seed,
        string generatorVersion,
        VoxelWorldBounds bounds,
        int chunkSize,
        string baseLayout,
        IReadOnlyList<WorldSaveDefinitionRef> externalDefinitions,
        IReadOnlyList<WorldVoxelCoord> removed,
        IReadOnlyList<WorldSaveInstance> added,
        IReadOnlyList<WorldSaveInstance> changed,
        ulong nextEntityId,
        ulong transactionCount,
        IReadOnlyList<WorldSaveVehicle> vehicles,
        IReadOnlyList<WorldSaveLocalDefinition> saveLocalDefinitions,
        WorldSaveRailway? railway = null,
        IReadOnlyList<TrainAssembly>? assemblies = null,
        ulong simulationStep = 0,
        IReadOnlyList<ScheduledTrainCommandQuery>? scheduledCommands = null,
        ulong scheduledSequence = 0,
        WorldSource? source = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(generatorVersion);
        ArgumentException.ThrowIfNullOrEmpty(baseLayout);
        ArgumentNullException.ThrowIfNull(externalDefinitions);
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(saveLocalDefinitions);
        FormatVersion = formatVersion;
        Id = id;
        DisplayName = displayName;
        Seed = seed;
        GeneratorVersion = generatorVersion;
        Bounds = bounds;
        ChunkSize = chunkSize;
        BaseLayout = baseLayout;
        ExternalDefinitions = externalDefinitions;
        Removed = removed;
        Added = added;
        Changed = changed;
        NextEntityId = nextEntityId;
        TransactionCount = transactionCount;
        Vehicles = vehicles;
        SaveLocalDefinitions = saveLocalDefinitions;
        Railway = railway ?? WorldSaveRailway.Empty;
        Assemblies = assemblies ?? Array.Empty<TrainAssembly>();
        SimulationStep = simulationStep;
        ScheduledCommands = scheduledCommands ?? Array.Empty<ScheduledTrainCommandQuery>();
        ScheduledSequence = scheduledSequence;
        Source = source;
    }

    public static bool SupportsFormat(int formatVersion) =>
        formatVersion is CurrentFormatVersion or PredecessorFormatVersion;

    public WorldSaveDocument With(
        Guid id,
        IReadOnlyList<WorldSaveVehicle> vehicles,
        IReadOnlyList<TrainAssembly> assemblies) =>
        new(
            FormatVersion,
            id,
            DisplayName,
            Seed,
            GeneratorVersion,
            Bounds,
            ChunkSize,
            BaseLayout,
            ExternalDefinitions,
            Removed,
            Added,
            Changed,
            NextEntityId,
            TransactionCount,
            vehicles,
            SaveLocalDefinitions,
            Railway,
            assemblies,
            SimulationStep,
            ScheduledCommands,
            ScheduledSequence,
            Source);

    public static bool TryNormalizeDisplayName(string? value, out string name)
    {
        name = "";
        if (value is null)
            return false;
        var trimmed = value.Trim();
        if (trimmed.Length is < DisplayNameMinLength or > DisplayNameMaxLength)
            return false;
        if (trimmed is "." or "..")
            return false;
        foreach (var c in trimmed)
        {
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or '\0')
                return false;
        }

        name = trimmed;
        return true;
    }
}

public static class WorldSaveLayout
{
    public static string Fingerprint(VoxelWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartArray();
            foreach (var (position, voxel) in world.OccupiedVoxels()
                .OrderBy(item => item.Key.X)
                .ThenBy(item => item.Key.Y)
                .ThenBy(item => item.Key.Z))
            {
                writer.WriteStartObject();
                writer.WriteNumber("x", position.X);
                writer.WriteNumber("y", position.Y);
                writer.WriteNumber("z", position.Z);
                writer.WriteString("definition_id", voxel.DefinitionId.Value);
                writer.WriteString("orientation", voxel.Orientation.ToString());
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}

public static class WorldSaveDelta
{
    public static void Compute(
        VoxelWorld generated,
        VoxelWorld current,
        out List<WorldVoxelCoord> removed,
        out List<WorldSaveInstance> added,
        out List<WorldSaveInstance> changed)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(current);
        var generatedMap = ToMap(generated);
        var currentMap = ToMap(current);
        removed = new List<WorldVoxelCoord>();
        added = new List<WorldSaveInstance>();
        changed = new List<WorldSaveInstance>();

        foreach (var (position, voxel) in generatedMap)
        {
            if (!currentMap.ContainsKey(position))
                removed.Add(position);
            else if (!SameInstance(voxel, currentMap[position]))
                changed.Add(ToInstance(position, currentMap[position]));
        }

        foreach (var (position, voxel) in currentMap)
        {
            if (!generatedMap.ContainsKey(position))
                added.Add(ToInstance(position, voxel));
        }

        removed.Sort(CompareCoord);
        added.Sort(CompareInstance);
        changed.Sort(CompareInstance);
    }

    private static Dictionary<WorldVoxelCoord, WorldVoxel> ToMap(VoxelWorld world)
    {
        var map = new Dictionary<WorldVoxelCoord, WorldVoxel>();
        foreach (var (position, voxel) in world.OccupiedVoxels())
            map.Add(position, voxel);
        return map;
    }

    private static bool SameInstance(WorldVoxel left, WorldVoxel right) =>
        left.DefinitionId == right.DefinitionId &&
        left.Orientation == right.Orientation &&
        left.CurrentDurability == right.CurrentDurability &&
        left.RuntimeId == right.RuntimeId;

    private static WorldSaveInstance ToInstance(WorldVoxelCoord position, WorldVoxel voxel) =>
        new(position, voxel.DefinitionId, voxel.Orientation, voxel.CurrentDurability, voxel.RuntimeId);

    private static int CompareCoord(WorldVoxelCoord left, WorldVoxelCoord right)
    {
        var x = left.X.CompareTo(right.X);
        if (x != 0)
            return x;
        var y = left.Y.CompareTo(right.Y);
        return y != 0 ? y : left.Z.CompareTo(right.Z);
    }

    private static int CompareInstance(WorldSaveInstance left, WorldSaveInstance right) =>
        CompareCoord(left.Position, right.Position);
}

public static class WorldSaveCodec
{
    public static bool TryCapture(
        VoxelWorld world,
        VoxelCatalogSnapshot catalog,
        Guid saveId,
        string displayName,
        IReadOnlyList<CompositeInstance> vehicles,
        ulong nextEntityId,
        ulong transactionCount,
        out WorldSaveDocument document,
        out WorldSaveStatus status,
        PhysicalRailState? rails = null,
        IReadOnlyList<TrainAssembly>? assemblies = null,
        ulong simulationStep = 0,
        IReadOnlyList<ScheduledTrainCommandQuery>? scheduledCommands = null,
        ulong scheduledSequence = 0,
        WorldSource? source = null)
    {
        document = null!;
        status = WorldSaveStatus.Saved;
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(vehicles);
        if (!WorldSaveDocument.TryNormalizeDisplayName(displayName, out var name))
        {
            status = WorldSaveStatus.InvalidName;
            return false;
        }

        var extraIds = (rails?.Segments.Select(item => item.Id.Value) ?? Array.Empty<ulong>())
            .Concat(assemblies?.Select(item => item.Id.Value) ?? Array.Empty<ulong>());
        if (!WorldSaveVehicleCodec.TryValidateIdentities(
                world,
                vehicles,
                nextEntityId,
                transactionCount,
                out status,
                extraIds))
            return false;

        VoxelWorld generated;
        try
        {
            generated = VoxelWorldFactory.Create(catalog, world.Identity);
        }
        catch (InvalidOperationException)
        {
            status = WorldSaveStatus.MissingDefinition;
            return false;
        }

        var savedVehicles = WorldSaveVehicleCodec.Capture(vehicles);
        var savedLocals = WorldSaveVehicleCodec.CaptureLocals(vehicles);
        if (!TryCollectReferences(world, generated, catalog, savedVehicles, savedLocals, out var references, out status))
            return false;

        WorldSaveDelta.Compute(generated, world, out var removed, out var added, out var changed);
        document = new WorldSaveDocument(
            WorldSaveDocument.CurrentFormatVersion,
            saveId,
            name,
            world.Identity.Seed,
            world.Identity.GeneratorVersion,
            world.Identity.Bounds,
            world.Identity.ChunkSize,
            WorldSaveLayout.Fingerprint(generated),
            references,
            removed,
            added,
            changed,
            nextEntityId,
            transactionCount,
            savedVehicles,
            savedLocals,
            rails is null ? WorldSaveRailway.Empty : WorldSaveRailway.Capture(rails),
            WorldSaveRailwayCodec.CaptureAssemblies(assemblies ?? Array.Empty<TrainAssembly>()),
            simulationStep,
            scheduledCommands ?? Array.Empty<ScheduledTrainCommandQuery>(),
            scheduledSequence,
            source);
        status = WorldSaveStatus.Saved;
        return true;
    }

    public static string Serialize(WorldSaveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", document.Id.ToString("D"));
            writer.WriteString("base_layout", document.BaseLayout);
            writer.WriteStartObject("bounds");
            writer.WriteNumber("x", document.Bounds.SizeX);
            writer.WriteNumber("y", document.Bounds.SizeY);
            writer.WriteNumber("z", document.Bounds.SizeZ);
            writer.WriteEndObject();
            writer.WriteNumber("chunk_size", document.ChunkSize);
            WriteDelta(writer, document);
            writer.WriteString("display_name", document.DisplayName);
            WriteReferences(writer, document.ExternalDefinitions);
            writer.WriteNumber("save_format_version", document.FormatVersion);
            writer.WriteNumber("seed", document.Seed);
            writer.WriteString("generator_version", document.GeneratorVersion);
            writer.WriteNumber("next_entity_id", document.NextEntityId);
            writer.WriteNumber("transaction_count", document.TransactionCount);
            WorldSaveVehicleCodec.WriteVehicles(writer, document.Vehicles);
            WorldSaveVehicleCodec.WriteLocals(writer, document.SaveLocalDefinitions);
            WorldSaveRailwayCodec.WriteRailway(writer, document.Railway);
            WorldSaveRailwayCodec.WriteAssemblies(writer, document.Assemblies);
            writer.WriteNumber("simulation_step", document.SimulationStep);
            WorldSaveRailwayCodec.WriteCommands(writer, document.ScheduledCommands);
            writer.WriteNumber("scheduled_sequence", document.ScheduledSequence);
            if (document.Source is { } source)
                WorldSourceCodec.Write(writer, source);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool TryDeserialize(string json, out WorldSaveDocument document, out WorldSaveStatus status)
    {
        document = null!;
        status = WorldSaveStatus.UnknownFormat;
        if (string.IsNullOrWhiteSpace(json))
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        if (WorldSaveVehicleCodec.ContainsNonFiniteLiteral(json))
        {
            status = WorldSaveStatus.InvalidDelta;
            return false;
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException)
        {
            status = WorldSaveStatus.Truncated;
            return false;
        }

        using (parsed)
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                return Reject(WorldSaveStatus.UnknownFormat, out status);
            return TryReadDocument(parsed.RootElement, out document, out status);
        }
    }

    private static bool TryCollectReferences(
        VoxelWorld world,
        VoxelWorld generated,
        VoxelCatalogSnapshot catalog,
        IReadOnlyList<WorldSaveVehicle> vehicles,
        IReadOnlyList<WorldSaveLocalDefinition> locals,
        out List<WorldSaveDefinitionRef> references,
        out WorldSaveStatus status)
    {
        references = new List<WorldSaveDefinitionRef>();
        status = WorldSaveStatus.Saved;
        var seen = new HashSet<VoxelDefinitionId>();
        var extra = new HashSet<VoxelDefinitionId>();
        WorldSaveVehicleCodec.CollectVoxelIds(vehicles, locals, extra);
        foreach (var source in new[] { world, generated })
        {
            foreach (var (_, voxel) in source.OccupiedVoxels())
                extra.Add(voxel.DefinitionId);
        }

        foreach (var id in extra)
        {
            if (!seen.Add(id))
                continue;
            if (!catalog.TryGet(id, out var entry))
            {
                status = WorldSaveStatus.MissingDefinition;
                return false;
            }

            references.Add(new WorldSaveDefinitionRef(
                id,
                entry.SemanticRevision,
                entry.Definition.MaxDurability));
        }

        references.Sort((left, right) =>
            string.Compare(left.DefinitionId.Value, right.DefinitionId.Value, StringComparison.Ordinal));
        return true;
    }

    private static void WriteDelta(Utf8JsonWriter writer, WorldSaveDocument document)
    {
        writer.WriteStartObject("delta");
        writer.WriteStartArray("added");
        foreach (var instance in document.Added)
            WriteInstance(writer, instance);
        writer.WriteEndArray();
        writer.WriteStartArray("changed");
        foreach (var instance in document.Changed)
            WriteInstance(writer, instance);
        writer.WriteEndArray();
        writer.WriteStartArray("removed");
        foreach (var position in document.Removed)
            WriteCoord(writer, position);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteReferences(Utf8JsonWriter writer, IReadOnlyList<WorldSaveDefinitionRef> references)
    {
        writer.WriteStartArray("external_definitions");
        foreach (var item in references)
        {
            writer.WriteStartObject();
            writer.WriteString("id", item.DefinitionId.Value);
            writer.WriteNumber("max_durability", item.MaxDurability);
            writer.WriteString("semantic_revision", item.SemanticRevision);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteInstance(Utf8JsonWriter writer, WorldSaveInstance instance)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", instance.Position.X);
        writer.WriteNumber("y", instance.Position.Y);
        writer.WriteNumber("z", instance.Position.Z);
        writer.WriteString("definition_id", instance.DefinitionId.Value);
        writer.WriteString("orientation", instance.Orientation.ToString());
        writer.WriteNumber("durability", instance.Durability);
        if (instance.RuntimeId is { } runtimeId)
            writer.WriteNumber("runtime_id", runtimeId.Value);
        writer.WriteEndObject();
    }

    private static void WriteCoord(Utf8JsonWriter writer, WorldVoxelCoord position)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", position.X);
        writer.WriteNumber("y", position.Y);
        writer.WriteNumber("z", position.Z);
        writer.WriteEndObject();
    }

    private static bool TryReadDocument(JsonElement root, out WorldSaveDocument document, out WorldSaveStatus status)
    {
        document = null!;
        status = WorldSaveStatus.UnknownFormat;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var formatVersion = 0;
        Guid id = default;
        string? displayName = null;
        ulong seed = 0;
        string? generatorVersion = null;
        VoxelWorldBounds bounds = default;
        var chunkSize = 0;
        string? baseLayout = null;
        IReadOnlyList<WorldSaveDefinitionRef>? references = null;
        IReadOnlyList<WorldVoxelCoord>? removed = null;
        IReadOnlyList<WorldSaveInstance>? added = null;
        IReadOnlyList<WorldSaveInstance>? changed = null;
        IReadOnlyList<WorldSaveVehicle>? vehicles = null;
        IReadOnlyList<WorldSaveLocalDefinition>? locals = null;
        WorldSaveRailway? railway = null;
        IReadOnlyList<TrainAssembly>? assemblies = null;
        IReadOnlyList<ScheduledTrainCommandQuery>? scheduledCommands = null;
        WorldSource? source = null;
        ulong nextEntityId = 0;
        ulong transactionCount = 0;
        ulong simulationStep = 0;
        ulong scheduledSequence = 0;
        var hasFormat = false;
        var hasId = false;
        var hasSeed = false;
        var hasBounds = false;
        var hasChunk = false;
        var hasDelta = false;
        var hasNextEntity = false;
        var hasTransaction = false;
        var hasSimulationStep = false;
        var hasScheduledSequence = false;

        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return Reject(WorldSaveStatus.UnknownFormat, out status);
            switch (property.Name)
            {
                case "save_format_version":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt32(out formatVersion))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    hasFormat = true;
                    break;
                case "id":
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(property.Value.GetString(), out id))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    hasId = true;
                    break;
                case "display_name":
                    if (property.Value.ValueKind != JsonValueKind.String ||
                        !WorldSaveDocument.TryNormalizeDisplayName(property.Value.GetString(), out displayName))
                        return Reject(WorldSaveStatus.InvalidName, out status);
                    break;
                case "seed":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetUInt64(out seed))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    hasSeed = true;
                    break;
                case "generator_version":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    generatorVersion = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(generatorVersion))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    break;
                case "bounds":
                    if (!TryReadBounds(property.Value, out bounds))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    hasBounds = true;
                    break;
                case "chunk_size":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt32(out chunkSize) ||
                        chunkSize <= 0)
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    hasChunk = true;
                    break;
                case "base_layout":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    baseLayout = property.Value.GetString();
                    if (string.IsNullOrWhiteSpace(baseLayout))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    break;
                case "external_definitions":
                    if (!TryReadReferences(property.Value, out references, out status))
                        return false;
                    break;
                case "delta":
                    if (!TryReadDelta(property.Value, out removed, out added, out changed, out status))
                        return false;
                    hasDelta = true;
                    break;
                case "next_entity_id":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetUInt64(out nextEntityId))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    hasNextEntity = true;
                    break;
                case "transaction_count":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetUInt64(out transactionCount))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    hasTransaction = true;
                    break;
                case "vehicles":
                    if (!WorldSaveVehicleCodec.TryReadVehicles(property.Value, out vehicles, out status))
                        return false;
                    break;
                case "save_local_definitions":
                    if (!WorldSaveVehicleCodec.TryReadLocals(property.Value, out locals, out status))
                        return false;
                    break;
                case "railway":
                    if (!WorldSaveRailwayCodec.TryReadRailway(property.Value, out railway, out status))
                        return false;
                    break;
                case "assemblies":
                    if (!WorldSaveRailwayCodec.TryReadAssemblies(property.Value, out assemblies, out status))
                        return false;
                    break;
                case "simulation_step":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetUInt64(out simulationStep))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    hasSimulationStep = true;
                    break;
                case "scheduled_commands":
                    if (!WorldSaveRailwayCodec.TryReadCommands(property.Value, out scheduledCommands, out status))
                        return false;
                    break;
                case "scheduled_sequence":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetUInt64(out scheduledSequence))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    hasScheduledSequence = true;
                    break;
                case "world_source":
                    if (!WorldSourceCodec.TryRead(property.Value, out var readSource, out status))
                        return false;
                    source = readSource;
                    break;
                default:
                    return Reject(WorldSaveStatus.UnknownFormat, out status);
            }
        }

        if (!hasFormat || !hasId || displayName is null || !hasSeed || generatorVersion is null ||
            !hasBounds || !hasChunk || baseLayout is null || references is null || !hasDelta ||
            !hasNextEntity || !hasTransaction || vehicles is null || locals is null)
            return Reject(WorldSaveStatus.Truncated, out status);
        if (!WorldSaveDocument.SupportsFormat(formatVersion))
        {
            status = WorldSaveStatus.UnknownFormat;
            return false;
        }

        if (formatVersion == WorldSaveDocument.CurrentFormatVersion &&
            (railway is null || assemblies is null || !hasSimulationStep || scheduledCommands is null || !hasScheduledSequence))
            return Reject(WorldSaveStatus.Truncated, out status);

        document = new WorldSaveDocument(
            formatVersion,
            id,
            displayName,
            seed,
            generatorVersion,
            bounds,
            chunkSize,
            baseLayout,
            references,
            removed!,
            added!,
            changed!,
            nextEntityId,
            transactionCount,
            vehicles,
            locals,
            railway,
            assemblies,
            simulationStep,
            scheduledCommands,
            scheduledSequence,
            source);
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool Reject(WorldSaveStatus value, out WorldSaveStatus status)
    {
        status = value;
        return false;
    }

    private static bool TryReadBounds(JsonElement element, out VoxelWorldBounds bounds)
    {
        bounds = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var x = 0;
        var y = 0;
        var z = 0;
        var hasX = false;
        var hasY = false;
        var hasZ = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value) || value <= 0)
                return false;
            switch (property.Name)
            {
                case "x":
                    x = value;
                    hasX = true;
                    break;
                case "y":
                    y = value;
                    hasY = true;
                    break;
                case "z":
                    z = value;
                    hasZ = true;
                    break;
                default:
                    return false;
            }
        }

        if (!hasX || !hasY || !hasZ)
            return false;
        bounds = new VoxelWorldBounds(x, y, z);
        return true;
    }

    private static bool TryReadReferences(
        JsonElement element,
        out IReadOnlyList<WorldSaveDefinitionRef> references,
        out WorldSaveStatus status)
    {
        references = Array.Empty<WorldSaveDefinitionRef>();
        status = WorldSaveStatus.UnknownFormat;
        if (element.ValueKind != JsonValueKind.Array)
            return Reject(WorldSaveStatus.UnknownFormat, out status);
        var list = new List<WorldSaveDefinitionRef>();
        var seen = new HashSet<VoxelDefinitionId>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return Reject(WorldSaveStatus.UnknownFormat, out status);
            var fields = new HashSet<string>(StringComparer.Ordinal);
            string? idText = null;
            string? revision = null;
            var maxDurability = 0;
            var hasMax = false;
            foreach (var property in item.EnumerateObject())
            {
                if (!fields.Add(property.Name))
                    return Reject(WorldSaveStatus.UnknownFormat, out status);
                switch (property.Name)
                {
                    case "id":
                        if (property.Value.ValueKind != JsonValueKind.String)
                            return Reject(WorldSaveStatus.UnknownFormat, out status);
                        idText = property.Value.GetString();
                        break;
                    case "semantic_revision":
                        if (property.Value.ValueKind != JsonValueKind.String)
                            return Reject(WorldSaveStatus.UnknownFormat, out status);
                        revision = property.Value.GetString();
                        break;
                    case "max_durability":
                        if (property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out maxDurability) ||
                            maxDurability is < VoxelDefinition.MaxDurabilityMin or > VoxelDefinition.MaxDurabilityMax)
                            return Reject(WorldSaveStatus.UnknownFormat, out status);
                        hasMax = true;
                        break;
                    default:
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                }
            }

            if (idText is null || string.IsNullOrWhiteSpace(revision) || !hasMax)
                return Reject(WorldSaveStatus.Truncated, out status);
            try
            {
                var id = VoxelDefinitionId.Parse(idText);
                if (!seen.Add(id))
                {
                    status = WorldSaveStatus.InvalidDelta;
                    return false;
                }

                list.Add(new WorldSaveDefinitionRef(id, revision, maxDurability));
            }
            catch (ArgumentException)
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }
        }

        references = list;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadDelta(
        JsonElement element,
        out IReadOnlyList<WorldVoxelCoord> removed,
        out IReadOnlyList<WorldSaveInstance> added,
        out IReadOnlyList<WorldSaveInstance> changed,
        out WorldSaveStatus status)
    {
        removed = Array.Empty<WorldVoxelCoord>();
        added = Array.Empty<WorldSaveInstance>();
        changed = Array.Empty<WorldSaveInstance>();
        status = WorldSaveStatus.InvalidDelta;
        if (element.ValueKind != JsonValueKind.Object)
        {
            status = WorldSaveStatus.UnknownFormat;
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasRemoved = false;
        var hasAdded = false;
        var hasChanged = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return Reject(WorldSaveStatus.UnknownFormat, out status);
            switch (property.Name)
            {
                case "removed":
                    if (!TryReadCoords(property.Value, out removed, out status))
                        return false;
                    hasRemoved = true;
                    break;
                case "added":
                    if (!TryReadInstances(property.Value, out added, out status))
                        return false;
                    hasAdded = true;
                    break;
                case "changed":
                    if (!TryReadInstances(property.Value, out changed, out status))
                        return false;
                    hasChanged = true;
                    break;
                default:
                    return Reject(WorldSaveStatus.UnknownFormat, out status);
            }
        }

        if (!hasRemoved || !hasAdded || !hasChanged)
        {
            status = WorldSaveStatus.UnknownFormat;
            return false;
        }

        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadCoords(
        JsonElement element,
        out IReadOnlyList<WorldVoxelCoord> coords,
        out WorldSaveStatus status)
    {
        coords = Array.Empty<WorldVoxelCoord>();
        status = WorldSaveStatus.InvalidDelta;
        if (element.ValueKind != JsonValueKind.Array)
            return Reject(WorldSaveStatus.UnknownFormat, out status);
        var list = new List<WorldVoxelCoord>();
        var seen = new HashSet<WorldVoxelCoord>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadCoord(item, out var position))
                return Reject(WorldSaveStatus.UnknownFormat, out status);
            if (!seen.Add(position))
                return Reject(WorldSaveStatus.InvalidDelta, out status);
            list.Add(position);
        }

        coords = list;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadInstances(
        JsonElement element,
        out IReadOnlyList<WorldSaveInstance> instances,
        out WorldSaveStatus status)
    {
        instances = Array.Empty<WorldSaveInstance>();
        status = WorldSaveStatus.InvalidDelta;
        if (element.ValueKind != JsonValueKind.Array)
            return Reject(WorldSaveStatus.UnknownFormat, out status);
        var list = new List<WorldSaveInstance>();
        var seen = new HashSet<WorldVoxelCoord>();
        foreach (var item in element.EnumerateArray())
        {
            if (!TryReadInstance(item, out var instance, out status))
                return false;
            if (!seen.Add(instance.Position))
            {
                status = WorldSaveStatus.Occupied;
                return false;
            }

            list.Add(instance);
        }

        instances = list;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryReadCoord(JsonElement element, out WorldVoxelCoord position)
    {
        position = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var x = 0;
        var y = 0;
        var z = 0;
        var hasX = false;
        var hasY = false;
        var hasZ = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return false;
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value))
                return false;
            switch (property.Name)
            {
                case "x":
                    x = value;
                    hasX = true;
                    break;
                case "y":
                    y = value;
                    hasY = true;
                    break;
                case "z":
                    z = value;
                    hasZ = true;
                    break;
                default:
                    return false;
            }
        }

        if (!hasX || !hasY || !hasZ)
            return false;
        position = new WorldVoxelCoord(x, y, z);
        return true;
    }

    private static bool TryReadInstance(JsonElement element, out WorldSaveInstance instance, out WorldSaveStatus status)
    {
        instance = default;
        status = WorldSaveStatus.InvalidDelta;
        if (element.ValueKind != JsonValueKind.Object)
            return Reject(WorldSaveStatus.UnknownFormat, out status);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var x = 0;
        var y = 0;
        var z = 0;
        var durability = 0;
        string? idText = null;
        string? orientationText = null;
        EntityId? runtimeId = null;
        var hasX = false;
        var hasY = false;
        var hasZ = false;
        var hasDurability = false;
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                return Reject(WorldSaveStatus.UnknownFormat, out status);
            switch (property.Name)
            {
                case "x":
                case "y":
                case "z":
                case "durability":
                    if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var number))
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
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
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    idText = property.Value.GetString();
                    break;
                case "orientation":
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return Reject(WorldSaveStatus.UnknownFormat, out status);
                    orientationText = property.Value.GetString();
                    break;
                case "runtime_id":
                    if (property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetUInt64(out var runtime) ||
                        runtime == 0)
                        return Reject(WorldSaveStatus.InvalidDelta, out status);
                    runtimeId = new EntityId(runtime);
                    break;
                default:
                    return Reject(WorldSaveStatus.UnknownFormat, out status);
            }
        }

        if (!hasX || !hasY || !hasZ || !hasDurability || idText is null || orientationText is null)
            return Reject(WorldSaveStatus.Truncated, out status);
        if (durability <= 0)
            return Reject(WorldSaveStatus.InvalidDelta, out status);
        try
        {
            var id = VoxelDefinitionId.Parse(idText);
            if (!VoxelOrientation.TryParse(orientationText, out var orientation))
                return Reject(WorldSaveStatus.InvalidDelta, out status);
            instance = new WorldSaveInstance(new WorldVoxelCoord(x, y, z), id, orientation, durability, runtimeId);
            status = WorldSaveStatus.Loaded;
            return true;
        }
        catch (ArgumentException)
        {
            return Reject(WorldSaveStatus.InvalidDelta, out status);
        }
    }
}

public static class WorldSaveRestorer
{
    public static bool TryBuild(
        VoxelCatalogSnapshot catalog,
        CompositeCatalogSnapshot composites,
        WorldSaveDocument document,
        VoxelWorldIdentity expectedIdentity,
        out WorldSaveRestore restore,
        out WorldSaveStatus status)
    {
        restore = null!;
        status = WorldSaveStatus.InvalidDelta;
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(composites);
        ArgumentNullException.ThrowIfNull(document);

        if (!WorldSaveDocument.SupportsFormat(document.FormatVersion))
        {
            status = WorldSaveStatus.UnknownFormat;
            return false;
        }

        if (!TryResolveIdentity(document, out var identity, out status))
            return false;
        if (identity != expectedIdentity)
        {
            status = WorldSaveStatus.IncompatibleBase;
            return false;
        }

        VoxelWorld generated;
        try
        {
            generated = VoxelWorldFactory.Create(catalog, identity);
        }
        catch (InvalidOperationException exception)
        {
            status = exception.Message.Contains("Unknown generator", StringComparison.Ordinal)
                ? WorldSaveStatus.UnknownGenerator
                : WorldSaveStatus.MissingDefinition;
            return false;
        }

        if (!string.Equals(WorldSaveLayout.Fingerprint(generated), document.BaseLayout, StringComparison.Ordinal))
        {
            status = WorldSaveStatus.IncompatibleBase;
            return false;
        }

        if (!TryValidateDefinitionRecords(catalog, generated, document, out status))
            return false;
        if (!TryValidateDelta(catalog, generated, document, out status))
            return false;
        if (!ApplySavedDurability(catalog, generated, document))
        {
            status = WorldSaveStatus.MissingDefinition;
            return false;
        }

        ApplyDelta(catalog, generated, document);
        if (!WorldSaveVehicleCodec.TryRestoreVehicles(
                document,
                catalog,
                composites,
                generated,
                out var vehicles,
                out status))
            return false;
        if (!WorldSaveRailwayCodec.TryRestore(
                document,
                generated,
                vehicles,
                catalog,
                out var rails,
                out var assemblies,
                out var commands,
                out status))
            return false;
        restore = new WorldSaveRestore(
            generated,
            vehicles,
            document.NextEntityId,
            document.TransactionCount,
            rails,
            assemblies,
            document.SimulationStep,
            commands,
            document.ScheduledSequence);
        status = WorldSaveStatus.Loaded;
        return true;
    }

    public static bool TryResolveIdentity(
        WorldSaveDocument document,
        out VoxelWorldIdentity identity,
        out WorldSaveStatus status)
    {
        identity = default;
        status = WorldSaveStatus.UnknownGenerator;
        var slice = document.GeneratorVersion switch
        {
            VoxelWorldIdentity.Slice4GeneratorVersion => VoxelDevelopmentSlice.Slice4,
            VoxelWorldIdentity.Slice3GeneratorVersion => VoxelDevelopmentSlice.Slice3,
            VoxelWorldIdentity.Slice2GeneratorVersion => VoxelDevelopmentSlice.Slice2,
            VoxelWorldIdentity.Slice1GeneratorVersion => VoxelDevelopmentSlice.Slice1,
            _ => (VoxelDevelopmentSlice)(-1)
        };
        if ((int)slice < 0)
            return false;
        try
        {
            identity = new VoxelWorldIdentity(
                slice,
                document.Seed,
                document.GeneratorVersion,
                document.Bounds,
                document.ChunkSize);
            status = WorldSaveStatus.Loaded;
            return true;
        }
        catch (ArgumentException)
        {
            status = WorldSaveStatus.IncompatibleBase;
            return false;
        }
    }

    private static bool TryValidateDefinitionRecords(
        VoxelCatalogSnapshot catalog,
        VoxelWorld generated,
        WorldSaveDocument document,
        out WorldSaveStatus status)
    {
        status = WorldSaveStatus.MissingDefinition;
        var required = new HashSet<VoxelDefinitionId>();
        foreach (var (_, voxel) in generated.OccupiedVoxels())
            required.Add(voxel.DefinitionId);
        foreach (var instance in document.Added.Concat(document.Changed))
            required.Add(instance.DefinitionId);
        foreach (var vehicle in document.Vehicles)
        {
            foreach (var part in vehicle.Parts)
                required.Add(part.DefinitionId);
        }

        foreach (var local in document.SaveLocalDefinitions)
        {
            foreach (var voxel in local.Definition.Voxels)
                required.Add(voxel.DefinitionId);
        }

        var recorded = new HashSet<VoxelDefinitionId>();
        foreach (var item in document.ExternalDefinitions)
        {
            if (!recorded.Add(item.DefinitionId))
            {
                status = WorldSaveStatus.InvalidDelta;
                return false;
            }
        }

        foreach (var id in required)
        {
            if (!recorded.Contains(id) || !catalog.TryGet(id, out _))
                return false;
        }

        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryValidateDelta(
        VoxelCatalogSnapshot catalog,
        VoxelWorld generated,
        WorldSaveDocument document,
        out WorldSaveStatus status)
    {
        status = WorldSaveStatus.InvalidDelta;
        var claimed = new HashSet<WorldVoxelCoord>();
        foreach (var position in document.Removed)
        {
            if (!generated.Bounds.Contains(position))
            {
                status = WorldSaveStatus.OutOfBounds;
                return false;
            }

            if (!generated.Query(position).IsOccupied || !claimed.Add(position))
                return false;
        }

        foreach (var instance in document.Changed)
        {
            if (!generated.Bounds.Contains(instance.Position))
            {
                status = WorldSaveStatus.OutOfBounds;
                return false;
            }

            if (!generated.Query(instance.Position).IsOccupied || !claimed.Add(instance.Position))
            {
                status = claimed.Contains(instance.Position) ? WorldSaveStatus.Occupied : WorldSaveStatus.InvalidDelta;
                return false;
            }

            if (!TryValidateInstance(catalog, instance, out status))
                return false;
        }

        foreach (var instance in document.Added)
        {
            if (!generated.Bounds.Contains(instance.Position))
            {
                status = WorldSaveStatus.OutOfBounds;
                return false;
            }

            if (generated.Query(instance.Position).IsOccupied && !document.Removed.Contains(instance.Position))
            {
                status = WorldSaveStatus.Occupied;
                return false;
            }

            if (!claimed.Add(instance.Position))
            {
                status = WorldSaveStatus.Occupied;
                return false;
            }

            if (!TryValidateInstance(catalog, instance, out status))
                return false;
        }

        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool TryValidateInstance(
        VoxelCatalogSnapshot catalog,
        WorldSaveInstance instance,
        out WorldSaveStatus status)
    {
        status = WorldSaveStatus.InvalidDelta;
        if (instance.Durability <= 0)
            return false;
        if (!catalog.TryGet(instance.DefinitionId, out var entry))
        {
            status = WorldSaveStatus.MissingDefinition;
            return false;
        }

        if (!instance.Orientation.IsOrthogonal ||
            !instance.Orientation.IsAllowedBy(entry.Definition.AllowedOrientations))
            return false;
        status = WorldSaveStatus.Loaded;
        return true;
    }

    private static bool ApplySavedDurability(
        VoxelCatalogSnapshot catalog,
        VoxelWorld world,
        WorldSaveDocument document)
    {
        var savedMax = document.ExternalDefinitions.ToDictionary(
            item => item.DefinitionId,
            item => item.MaxDurability);
        foreach (var (position, voxel) in world.OccupiedVoxels().ToArray())
        {
            if (!catalog.TryGet(voxel.DefinitionId, out var entry))
                return false;
            if (!savedMax.TryGetValue(voxel.DefinitionId, out var recorded))
                return false;
            var durability = Math.Min(recorded, entry.Definition.MaxDurability);
            if (durability != voxel.CurrentDurability)
                world.TryReplace(position, voxel with { CurrentDurability = durability });
        }

        return true;
    }

    private static void ApplyDelta(
        VoxelCatalogSnapshot catalog,
        VoxelWorld world,
        WorldSaveDocument document)
    {
        foreach (var position in document.Removed)
            world.TryRemove(position);
        foreach (var instance in document.Changed.Concat(document.Added))
        {
            var entry = catalog.Entries[instance.DefinitionId];
            var durability = Math.Min(instance.Durability, entry.Definition.MaxDurability);
            var voxel = new WorldVoxel(instance.DefinitionId, durability, instance.Orientation, instance.RuntimeId);
            if (!world.TryReplace(instance.Position, voxel))
                world.TryAdd(instance.Position, voxel);
        }
    }
}

public static class WorldSaveFixture
{
    public static readonly WorldVoxelCoord RemovedGround = new(4, 0, 4);
    public static readonly WorldVoxelCoord AddedHollow = new(4, 1, 4);
    public static readonly WorldVoxelCoord DamagedCube = new(14, 14, 14);
    public static readonly WorldVoxelCoord ReorientedCube = new(17, 17, 17);
    public static readonly WorldVoxelCoord SavedLedge = new(15, 1, 8);
    public static readonly VoxelDefinitionId HollowId = new("core", "hollow");
    public static readonly VoxelDefinitionId LedgeId = new("core", "ledge");
    public static readonly VoxelOrientation Reoriented = new(VoxelAxis.NegativeZ, VoxelAxis.PositiveY);

    public static void ApplyFixedSequence(VoxelDevelopmentSession session, bool includeLedge = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        Require(session.Remove(RemovedGround).Status == WorldCommandStatus.Removed, "remove (4,0,4)");
        Require(
            session.Add(AddedHollow, HollowId, VoxelOrientation.Identity).Status == WorldCommandStatus.Added,
            "add hollow (4,1,4)");
        Require(session.Damage(DamagedCube, 25).Status == WorldCommandStatus.Damaged, "damage (14,14,14)");
        Require(session.Remove(ReorientedCube).Status == WorldCommandStatus.Removed, "remove (17,17,17)");
        Require(
            session.Add(ReorientedCube, VoxelWorldFactory.DevelopmentDefinitionId, Reoriented).Status ==
            WorldCommandStatus.Added,
            "re-add (17,17,17)");
        if (!includeLedge)
            return;
        Require(
            session.Add(SavedLedge, LedgeId, VoxelOrientation.Yaw90).Status == WorldCommandStatus.Added,
            "add ledge (15,1,8)");
    }

    private static void Require(bool condition, string step)
    {
        if (!condition)
            throw new InvalidOperationException($"Fixed save sequence failed at {step}.");
    }
}
