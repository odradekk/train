using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Train.Core.Contracts;

namespace Train.Core.World;

internal static class WorldJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static void Write(Utf8JsonWriter writer, WorldState world) => JsonSerializer.Serialize(writer, world, Options);
    internal static void WriteReferences(Utf8JsonWriter writer, CoreReferenceManifest references) => JsonSerializer.Serialize(writer, references, Options);

    internal static WorldState Read(JsonElement element)
    {
        try
        {
            var world = element.Deserialize<WorldState>(Options) ?? throw new CoreStateFormatException("/world");
            WorldStateValidation.Validate(world);
            var normalized = WorldInputs.NormalizeInput(world.Source);
            if (!SameValue(world.Source, normalized)) throw new CoreStateFormatException("/world/source");
            foreach (var design in world.Designs)
                if (!SameValue(design, WorldInputs.NormalizeDesign(design))) throw new CoreStateFormatException("/world/designs");
            foreach (var settlement in world.Settlements)
                if (!SameValue(settlement.Template, WorldInputs.NormalizeTemplate(settlement.Template))) throw new CoreStateFormatException("/world/settlements/template");
            return world;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        { throw new CoreStateFormatException("/world"); }
    }

    internal static CoreReferenceManifest ReadReferences(JsonElement element)
    {
        CoreReferenceManifest? references;
        try { references = element.Deserialize<CoreReferenceManifest>(Options); }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        { throw new CoreStateFormatException("/references"); }
        if (references is null || references.Designs.IsDefault || references.Templates.IsDefault
            || references.Definitions.IsDefault || references.Inputs.IsDefault || references.Definitions.Any(d => d is null)
            || references.Templates.Any(t => t is null || t.Plots.IsDefault || t.Plots.Any(p => p is null)))
            throw new CoreStateFormatException("/references");
        return references;
    }

    internal static bool SameReferences(CoreReferenceManifest left, CoreReferenceManifest right)
        => SameValue(left, right);

    private static bool SameValue<T>(T left, T right) => JsonSerializer.Serialize(left, Options) == JsonSerializer.Serialize(right, Options);

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
                var t when t == typeof(TrainPosition) => [new(typeof(TrainPosition.Node), "node"), new(typeof(TrainPosition.Edge), "edge")],
                var t when t == typeof(PlanStop) => [new(typeof(PlanStop.Waypoint), "waypoint"), new(typeof(PlanStop.Station), "station")],
                _ => []
            };
            if (variants.Length == 0) return;
            info.PolymorphismOptions = new() { TypeDiscriminatorPropertyName = "$kind" };
            foreach (var variant in variants) info.PolymorphismOptions.DerivedTypes.Add(variant);
        });
        return new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = resolver
        };
    }
}
