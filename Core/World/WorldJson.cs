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
    internal static void WriteDesignInput(Utf8JsonWriter writer, DesignInput input) => JsonSerializer.Serialize(writer, input, Options);
    internal static DesignInput ReadDesignInput(JsonElement element) => element.Deserialize<DesignInput>(Options) ?? throw new JsonException();

    internal static void WriteDesignChange(Utf8JsonWriter writer, DesignChangeProjection change) => JsonSerializer.Serialize(writer, change, Options);
    internal static DesignChangeProjection ReadDesignChange(JsonElement element) => element.Deserialize<DesignChangeProjection>(Options) ?? throw new JsonException();

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
            {
                if (!SameValue(settlement.Template, WorldInputs.NormalizeTemplate(settlement.Template))) throw new CoreStateFormatException("/world/settlements/template");
                if (settlement.PopulationState.Period is { } period
                    && !SameValue(period.Rule, WorldInputs.NormalizePopulationRule(period.Rule)))
                    throw new CoreStateFormatException("/world/settlements/population_state/period/rule");
            }
            foreach (var job in world.FoundingJobs)
                if (!SameValue(job.Template, WorldInputs.NormalizeTemplate(job.Template)))
                    throw new CoreStateFormatException("/world/founding_jobs/template");
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

    internal static bool SameValue<T>(T left, T right) => JsonSerializer.Serialize(left, Options) == JsonSerializer.Serialize(right, Options);

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
                var t when t == typeof(InventoryOwner) => [new(typeof(InventoryOwner.Settlement), "settlement"), new(typeof(InventoryOwner.Train), "train"), new(typeof(InventoryOwner.Lease), "lease")],
                var t when t == typeof(TrainPosition) => [new(typeof(TrainPosition.Node), "node"), new(typeof(TrainPosition.Edge), "edge")],
                var t when t == typeof(PlanStop) => [new(typeof(PlanStop.Waypoint), "waypoint"), new(typeof(PlanStop.Station), "station")],
                var t when t == typeof(AutomaticGoodsAction) => [new(typeof(AutomaticGoodsAction.Buy), "buy"), new(typeof(AutomaticGoodsAction.Sell), "sell"),
                    new(typeof(AutomaticGoodsAction.Load), "load"), new(typeof(AutomaticGoodsAction.Unload), "unload")],
                var t when t == typeof(WorldObjectId) => [new(typeof(WorldObjectId.Node), "node"), new(typeof(WorldObjectId.Edge), "edge"),
                    new(typeof(WorldObjectId.Train), "train"), new(typeof(WorldObjectId.Instance), "instance"), new(typeof(WorldObjectId.Settlement), "settlement"),
                    new(typeof(WorldObjectId.Inventory), "inventory"), new(typeof(WorldObjectId.Job), "job"), new(typeof(WorldObjectId.Combat), "combat"),
                    new(typeof(WorldObjectId.Creature), "creature"), new(typeof(WorldObjectId.CreatureGroup), "creature_group"), new(typeof(WorldObjectId.Lair), "lair")],
                var t when t == typeof(WorldLocation) => [new(typeof(WorldLocation.Node), "node"), new(typeof(WorldLocation.Edge), "edge")],
                var t when t == typeof(CombatController) => [new(typeof(CombatController.Train), "train"),
                    new(typeof(CombatController.Settlement), "settlement")],
                var t when t == typeof(CombatUnitId) => [new(typeof(CombatUnitId.Instance), "instance"),
                    new(typeof(CombatUnitId.CreatureGroup), "creature_group")],
                var t when t == typeof(CombatOwner) => [new(typeof(CombatOwner.Player), "player"),
                    new(typeof(CombatOwner.Settlement), "settlement"), new(typeof(CombatOwner.Creatures), "creatures")],
                var t when t == typeof(DestroyedUnit) => [new(typeof(DestroyedUnit.Instance), "instance"),
                    new(typeof(DestroyedUnit.Creature), "creature")],
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
            TypeInfoResolver = resolver,
            Converters = { new MoneyConverter(), new BasisPointsConverter() }
        };
    }

    private sealed class MoneyConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
                throw new JsonException();
            return new(value);
        }

        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.Value);
    }

    private sealed class BasisPointsConverter : JsonConverter<BasisPoints>
    {
        public override BasisPoints Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var value))
                throw new JsonException();
            return new(value);
        }

        public override void Write(Utf8JsonWriter writer, BasisPoints value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value.Value);
    }
}
