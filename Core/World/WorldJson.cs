using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Train.Core.Contracts;

namespace Train.Core.World;

internal static class WorldJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static void Write(Utf8JsonWriter writer, WorldState? world) => JsonSerializer.Serialize(writer, world, Options);
    internal static void WriteReferences(Utf8JsonWriter writer, CoreReferenceManifest references) => JsonSerializer.Serialize(writer, references, Options);

    internal static WorldState? Read(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        CheckDuplicates(element, "/world");
        try
        {
            var world = element.Deserialize<WorldState>(Options) ?? throw new WorldStateValidation.InvalidState("/world");
            WorldStateValidation.Validate(world);
            var empty = new CoreContentProjection(new(new string('0', 64)), [], [], [], [], [], [], [], [], [], [], [], [], [], []);
            var normalized = WorldInputs.Normalize(world.Source, empty, new(new(0, 0, long.MaxValue, long.MaxValue))).Input;
            if (!SameValue(world.Source, normalized)) throw new WorldStateValidation.InvalidState("/world/source");
            foreach (var design in world.Designs)
                if (!SameValue(design, WorldInputs.NormalizeDesign(design))) throw new WorldStateValidation.InvalidState("/world/designs");
            foreach (var settlement in world.Settlements)
                if (!SameValue(settlement.Template, WorldInputs.NormalizeTemplate(settlement.Template))) throw new WorldStateValidation.InvalidState("/world/settlements/template");
            return world;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        { throw new WorldStateValidation.InvalidState("/world"); }
    }

    internal static CoreReferenceManifest ReadReferences(JsonElement element)
    {
        CheckDuplicates(element, "/references");
        CoreReferenceManifest? references;
        try { references = element.Deserialize<CoreReferenceManifest>(Options); }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        { throw new WorldStateValidation.InvalidState("/references"); }
        if (references is null || references.Designs.IsDefault || references.Templates.IsDefault
            || references.Definitions.IsDefault || references.Inputs.IsDefault || references.Definitions.Any(d => d is null))
            throw new WorldStateValidation.InvalidState("/references");
        return references;
    }

    internal static bool SameReferences(CoreReferenceManifest left, CoreReferenceManifest right)
        => SameValue(left, right);

    private static bool SameValue<T>(T left, T right) => JsonSerializer.Serialize(left, Options) == JsonSerializer.Serialize(right, Options);

    private static void CheckDuplicates(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var child = path + "/" + property.Name.Replace("~", "~0").Replace("/", "~1");
                if (!names.Add(property.Name)) throw new WorldStateValidation.InvalidState(child);
                CheckDuplicates(property.Value, child);
            }
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in element.EnumerateArray()) CheckDuplicates(child, path + "/" + index++);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            JsonDerivedType[] variants = info.Type switch
            {
                var t when t == typeof(GoodId) => [new(typeof(GoodId.Voxel), "voxel"), new(typeof(GoodId.Composite), "composite"), new(typeof(GoodId.Assembly), "assembly")],
                var t when t == typeof(ProcessRef) => [new(typeof(ProcessRef.Production), "production"), new(typeof(ProcessRef.Recovery), "recovery")],
                var t when t == typeof(CoreDefinitionReference) => [new(typeof(CoreDefinitionReference.Voxel), "voxel"), new(typeof(CoreDefinitionReference.Composite), "composite"),
                    new(typeof(CoreDefinitionReference.Process), "process"), new(typeof(CoreDefinitionReference.Rule), "rule"), new(typeof(CoreDefinitionReference.SettlementType), "settlement_type"),
                    new(typeof(CoreDefinitionReference.Template), "template"), new(typeof(CoreDefinitionReference.Terrain), "terrain"), new(typeof(CoreDefinitionReference.Creature), "creature"), new(typeof(CoreDefinitionReference.Lair), "lair")],
                var t when t == typeof(DesignKind) => [new(typeof(DesignKind.Vehicle), "vehicle"), new(typeof(DesignKind.Building), "building"), new(typeof(DesignKind.LargeEquipment), "large_equipment")],
                var t when t == typeof(PartCapability) => [new(typeof(PartCapability.Propulsion), "propulsion"), new(typeof(PartCapability.Control), "control"), new(typeof(PartCapability.Storage), "storage"),
                    new(typeof(PartCapability.Production), "production"), new(typeof(PartCapability.Extraction), "extraction"), new(typeof(PartCapability.Construction), "construction"), new(typeof(PartCapability.Power), "power"),
                    new(typeof(PartCapability.Weapon), "weapon"), new(typeof(PartCapability.Platform), "platform")],
                var t when t == typeof(NodeOccupant) => [new(typeof(NodeOccupant.Empty), "empty"), new(typeof(NodeOccupant.Settlement), "settlement"), new(typeof(NodeOccupant.Lair), "lair")],
                var t when t == typeof(ResourceRenewal) => [new(typeof(ResourceRenewal.NonRenewable), "non_renewable"), new(typeof(ResourceRenewal.Renewable), "renewable")],
                var t when t == typeof(InstanceId) => [new(typeof(InstanceId.Vehicle), "vehicle"), new(typeof(InstanceId.Building), "building")],
                var t when t == typeof(InstanceDetails) => [new(typeof(InstanceDetails.Vehicle), "vehicle"), new(typeof(InstanceDetails.Building), "building")],
                var t when t == typeof(InventoryOwner) => [new(typeof(InventoryOwner.Settlement), "settlement"), new(typeof(InventoryOwner.Train), "train")],
                _ => []
            };
            if (variants.Length == 0) return;
            info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "$kind" };
            foreach (var variant in variants) info.PolymorphismOptions.DerivedTypes.Add(variant);
        });
        return new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = resolver
        };
    }
}
