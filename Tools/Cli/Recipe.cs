using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Train.Core.Contracts;
using Train.Content.Contracts;

namespace Train.Cli;

internal sealed record Recipe(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] string GenerationProfile,
    [property: JsonRequired] ulong Seed,
    [property: JsonRequired] string GeneratorVersion,
    [property: JsonRequired] RecipeSave Save,
    [property: JsonRequired] ImmutableArray<RecipeCommand> Commands,
    [property: JsonRequired] SimulationPosition Target,
    ImmutableArray<ulong> CheckpointSteps = default)
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal static Recipe Read(string path)
    {
        var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("配方为空。");
        if (recipe.FormatVersion != 1) throw new InvalidDataException("不支持的配方版本。");
        if (string.IsNullOrWhiteSpace(recipe.GenerationProfile) || string.IsNullOrWhiteSpace(recipe.GeneratorVersion)
            || recipe.Save is null || recipe.Save.Id == Guid.Empty || string.IsNullOrWhiteSpace(recipe.Save.Name)
            || recipe.Commands.IsDefault)
            throw new InvalidDataException("配方缺少生成输入、存档元数据或命令列表。");
        var previous = new SimulationPosition(0, 0);
        foreach (var command in recipe.Commands)
        {
            if (command is null || command.At.CompletedSteps < previous.CompletedSteps
                || command.At.CompletedSteps == previous.CompletedSteps && command.At.Sequence != previous.Sequence
                || command.At.CompletedSteps > previous.CompletedSteps && command.At.Sequence != 0)
                throw new InvalidDataException("commands.at 必须按模拟位置连续排列。");
            command.Validate();
            previous = command.At with { Sequence = checked(command.At.Sequence + 1) };
        }
        if (recipe.Target.CompletedSteps < previous.CompletedSteps
            || recipe.Target.CompletedSteps == previous.CompletedSteps && recipe.Target.Sequence != previous.Sequence
            || recipe.Target.CompletedSteps > previous.CompletedSteps && recipe.Target.Sequence != 0)
            throw new InvalidDataException("target 不是命令列表之后的完整边界。");
        ulong? lastCheckpoint = null;
        foreach (var step in recipe.CheckpointSteps.IsDefault ? [] : recipe.CheckpointSteps)
        {
            if (lastCheckpoint is { } last && step <= last || step > recipe.Target.CompletedSteps)
                throw new InvalidDataException("checkpoint_steps 必须严格递增且不超过 target。");
            lastCheckpoint = step;
        }
        return recipe with { CheckpointSteps = recipe.CheckpointSteps.IsDefault ? [] : recipe.CheckpointSteps };
    }
}

internal sealed record RecipeSave(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] DateTimeOffset SavedAt);

internal sealed record RecipeCommand(
    [property: JsonRequired] SimulationPosition At,
    [property: JsonRequired] string Kind,
    ulong? Train = null,
    RecipePlan? Plan = null,
    JsonElement? Request = null,
    ulong? Seller = null,
    ulong? Vehicle = null,
    long? MaximumPayment = null,
    ulong? Settlement = null,
    ulong? LocalInventory = null,
    RecipeGood? Good = null,
    long? Quantity = null,
    ulong? Facility = null,
    string? Blueprint = null,
    ImmutableArray<ulong> Sources = default,
    ulong? Node = null,
    ulong? Edge = null,
    ulong? Break = null,
    int? Track = null,
    double? DistanceMeters = null,
    [property: JsonConverter(typeof(RecipeOptionalIdConverter))] RecipeOptionalId? MaterialSource = null,
    ImmutableArray<ulong> OrderedVehicles = default)
{
    internal void Validate()
    {
        if (Kind is "construction.commission_vehicle_work" or "construction.start_owned")
        {
            RejectUnused(facility: Kind == "construction.commission_vehicle_work", request: Kind == "construction.start_owned",
                payment: Kind == "construction.commission_vehicle_work");
            if (Kind == "construction.commission_vehicle_work")
            {
                if (Facility is null or 0 || string.IsNullOrWhiteSpace(Blueprint) || Sources.IsDefault
                    || Sources.Any(source => source == 0) || MaximumPayment is null or < 0)
                    throw Invalid();
            }
            else ParseOwned(null);
            return;
        }
        Parse();
    }

    internal CoreCommand Parse(IContentView content)
    {
        if (Kind == "construction.commission_vehicle_work")
        {
            Validate();
            return new CoreCommand.CommissionVehicleWork(new JobRequest.BuildVehicle(
                new(new(Facility!.Value), CapabilityKind.Construction), BlueprintDesign(content, Blueprint!),
                Sources.Select(source => new InventoryId(source)).ToImmutableArray(), 0), new(MaximumPayment!.Value));
        }
        if (Kind == "construction.start_owned")
        {
            Validate();
            var job = ParseOwned(content);
            return new CoreCommand.StartOwnedJob(new JobRequest.BuildBuilding(
                new(new(job.Facility.Building), CapabilityKind.Construction), new(job.Plot),
                BlueprintDesign(content, job.Blueprint),
                job.Sources.Select(source => new InventoryId(source)).ToImmutableArray(), job.Priority));
        }
        return Parse();
    }

    internal CoreCommand Parse()
    {
        var trainCommand = Kind is "train.replace_run_plan" or "train.start" or "train.stop" or "train.disband";
        var transfer = Kind is "inventory.load_goods" or "inventory.unload_goods";
        var lease = Kind == "lease.acquire";
        var vehicle = Kind == "market.buy_vehicle";
        var form = Kind == "train.form";
        var found = Kind == "settlement.found";
        var expand = Kind == "rail.expand";
        var repair = Kind == "rail.repair";
        var automatic = Kind == "rail.set_automatic_repair";
        var debugBreak = Kind == "rail.debug_add_break";
        if (!(trainCommand || transfer || lease || vehicle || form || found || expand || repair || automatic || debugBreak))
            throw Invalid();
        RejectUnused(train: trainCommand || transfer, plan: Kind == "train.replace_run_plan", request: lease,
            vehicle: vehicle, payment: lease || vehicle, transfer: transfer, vehicles: form, node: found,
            edge: expand || repair || automatic || debugBreak, railBreak: repair,
            track: debugBreak, distance: debugBreak, material: found || expand || repair || automatic);
        switch (Kind)
        {
            case "train.replace_run_plan" when Plan is { Stops.IsDefault: false, Stops.IsEmpty: false } plan:
                return new CoreCommand.ReplaceRunPlan(new(Train!.Value), new(plan.Loop,
                    plan.Stops.Select(stop => stop?.Parse() ?? throw Invalid()).ToImmutableArray()));
            case "train.start": return new CoreCommand.StartTrain(new(Train!.Value));
            case "train.stop": return new CoreCommand.StopTrain(new(Train!.Value));
            case "train.disband": return new CoreCommand.DisbandTrain(new(Train!.Value));
            case "train.form" when !OrderedVehicles.IsDefault && OrderedVehicles.All(id => id > 0):
                return new CoreCommand.FormTrain(OrderedVehicles.Select(id => new VehicleId(id)).ToImmutableArray());
            case "settlement.found" when MaterialSource?.Value is > 0:
                return new CoreCommand.FoundSettlement(new(Node!.Value), new(RequiredMaterial()));
            case "lease.acquire" when Lease() is { Kind: "rent", Settlement: > 0, Capacity: > 0, DurationSteps: > 0 } rent
                && MaximumPayment is >= 0:
                return new CoreCommand.AcquireLease(new LeaseRequest.Rent(new(rent.Settlement), rent.Capacity,
                    rent.DurationSteps), new(MaximumPayment.Value));
            case "market.buy_vehicle" when Seller is > 0 && Vehicle is > 0 && MaximumPayment is >= 0:
                return new CoreCommand.BuyVehicle(new(Seller.Value), new(Vehicle.Value), new(MaximumPayment.Value));
            case "inventory.load_goods" or "inventory.unload_goods" when Settlement is > 0
                && LocalInventory is > 0 && Good is not null && Quantity is > 0:
                return Kind == "inventory.load_goods"
                    ? new CoreCommand.LoadGoods(new(Train!.Value), new(Settlement.Value), new(LocalInventory.Value),
                        Good.Parse(), Quantity.Value)
                    : new CoreCommand.UnloadGoods(new(Train!.Value), new(Settlement.Value), new(LocalInventory.Value),
                        Good.Parse(), Quantity.Value);
            case "rail.expand" when MaterialSource?.Value is > 0:
                return new CoreCommand.ExpandRail(new(Edge!.Value), new(RequiredMaterial()));
            case "rail.repair" when MaterialSource?.Value is > 0:
                return new CoreCommand.RepairRail(new(Edge!.Value), new(Break!.Value), new(RequiredMaterial()));
            case "rail.set_automatic_repair":
                return new CoreCommand.SetAutomaticRailRepair(new(Edge!.Value),
                    MaterialSource!.Value.Value is { } source ? new SettlementId(source) : null);
            case "rail.debug_add_break" when Track is >= 1 && DistanceMeters is >= 0 && double.IsFinite(DistanceMeters.Value):
                return new CoreCommand.DebugAddRailBreak(new(Edge!.Value), Track.Value, DistanceMeters.Value);
            default: throw Invalid();
        }
    }

    private ulong RequiredMaterial() => MaterialSource!.Value.Value ?? throw Invalid();

    private void RejectUnused(bool train = false, bool plan = false, bool request = false, bool vehicle = false,
        bool payment = false, bool transfer = false, bool facility = false, bool vehicles = false, bool node = false,
        bool edge = false, bool railBreak = false, bool track = false, bool distance = false, bool material = false)
    {
        if ((train ? Train is null or 0 : Train is not null)
            || (plan ? Plan is null : Plan is not null)
            || (request ? Request is null : Request is not null)
            || (vehicle ? Seller is null || Vehicle is null : Seller is not null || Vehicle is not null)
            || (payment ? MaximumPayment is null : MaximumPayment is not null)
            || (transfer ? Settlement is null || LocalInventory is null || Good is null || Quantity is null
                : Settlement is not null || LocalInventory is not null || Good is not null || Quantity is not null)
            || (facility ? Facility is null || Blueprint is null || Sources.IsDefault
                : Facility is not null || Blueprint is not null || !Sources.IsDefault)
            || (vehicles ? OrderedVehicles.IsDefault : !OrderedVehicles.IsDefault)
            || (node ? Node is null or 0 : Node is not null)
            || (edge ? Edge is null or 0 : Edge is not null)
            || (railBreak ? Break is null or 0 : Break is not null)
            || (track ? Track is null : Track is not null)
            || (distance ? DistanceMeters is null : DistanceMeters is not null)
            || (material ? MaterialSource is null : MaterialSource is not null))
            throw Invalid();
    }

    private RecipeLeaseRequest Lease()
    {
        try
        {
            return Request is { ValueKind: JsonValueKind.Object } element
                ? JsonSerializer.Deserialize<RecipeLeaseRequest>(element, Recipe.Json) ?? throw Invalid()
                : throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
    }

    private RecipeOwnedJob ParseOwned(IContentView? content)
    {
        RecipeOwnedJob job;
        try
        {
            job = Request is { ValueKind: JsonValueKind.Object } element
                ? JsonSerializer.Deserialize<RecipeOwnedJob>(element, Recipe.Json) ?? throw Invalid()
                : throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
        if (job.Kind != "build_building" || job.Facility is not { Building: > 0, Capability: "construction" }
            || job.Plot == 0 || string.IsNullOrWhiteSpace(job.Blueprint) || job.Sources.IsDefault
            || job.Sources.Any(source => source == 0))
            throw Invalid();
        if (content is not null) BlueprintDesign(content, job.Blueprint);
        return job;
    }

    private static DesignInput BlueprintDesign(IContentView content, string blueprint)
    {
        var found = content.FindBlueprint(new(BlueprintLibrary.Packages, new(blueprint)));
        if (found is not ContentLookup<BlueprintRecord>.Found
            { Value.Evaluation: DesignEvaluation.Complete { IsValid: true, Core: { } design } })
            throw new InvalidDataException($"蓝图不可用：{blueprint}。");
        return design;
    }

    private InvalidDataException Invalid() => new($"不支持的配方命令或参数：{Kind}。");
}

internal readonly record struct RecipeOptionalId(ulong? Value);

internal sealed class RecipeOptionalIdConverter : JsonConverter<RecipeOptionalId>
{
    public override bool HandleNull => true;

    public override RecipeOptionalId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return new(null);
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt64(out var value) || value == 0)
            throw new JsonException("材料来源必须是非零身份或 null。");
        return new(value);
    }

    public override void Write(Utf8JsonWriter writer, RecipeOptionalId value, JsonSerializerOptions options)
    {
        if (value.Value is { } number) writer.WriteNumberValue(number);
        else writer.WriteNullValue();
    }
}

internal sealed record RecipeOwnedJob(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] RecipeFacility Facility,
    [property: JsonRequired] ulong Plot,
    [property: JsonRequired] string Blueprint,
    [property: JsonRequired] ImmutableArray<ulong> Sources,
    [property: JsonRequired] int Priority);

internal sealed record RecipeFacility(
    [property: JsonRequired] ulong Building,
    [property: JsonRequired] string Capability);

internal sealed record RecipeLeaseRequest(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] ulong Settlement,
    [property: JsonRequired] long Capacity,
    [property: JsonRequired] ulong DurationSteps);

internal sealed record RecipePlan(
    [property: JsonRequired] bool Loop,
    [property: JsonRequired] ImmutableArray<RecipePlanStop> Stops);

internal sealed record RecipePlanStop(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] ulong Node,
    ulong? DwellSteps = null,
    ImmutableArray<RecipeGoodsRule> GoodsRules = default)
{
    internal PlanStop Parse()
    {
        if (Node == 0) throw new InvalidDataException("计划节点必须非零。");
        return Kind switch
        {
            "waypoint" when DwellSteps is null && GoodsRules.IsDefault => new PlanStop.Waypoint(new(Node)),
            "station" => new PlanStop.Station(new(Node), DwellSteps ?? 0,
                GoodsRules.IsDefault ? [] : GoodsRules.Select(rule => rule?.Parse()
                    ?? throw new InvalidDataException("货物规则不能为空。")).ToImmutableArray()),
            _ => throw new InvalidDataException($"不支持的计划站点或参数：{Kind}。")
        };
    }
}

internal sealed record RecipeGood(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] string Definition)
{
    internal GoodId Parse() => Kind == "voxel" && !string.IsNullOrWhiteSpace(Definition)
        ? new GoodId.Voxel(new(Definition)) : throw new InvalidDataException("配方货物须为 voxel 并提供 definition。");
}

internal sealed record RecipeGoodsRule(
    [property: JsonRequired] RecipeGoodsAction Action,
    [property: JsonRequired] RecipeGood Good,
    [property: JsonRequired] ulong LocalInventory,
    [property: JsonRequired] long TargetQuantity)
{
    internal AutomaticGoodsRule Parse()
    {
        if (Action is null || Good is null || LocalInventory == 0 || TargetQuantity < 0)
            throw new InvalidDataException("自动货物规则参数无效。");
        return new(Action.Parse(), Good.Parse(), new(LocalInventory), TargetQuantity);
    }
}

internal sealed record RecipeGoodsAction(
    [property: JsonRequired] string Kind,
    long? MaximumUnitPrice = null,
    long? MinimumUnitPrice = null)
{
    internal AutomaticGoodsAction Parse() => Kind switch
    {
        "buy" when MinimumUnitPrice is null && MaximumUnitPrice is null or >= 0 =>
            new AutomaticGoodsAction.Buy(MaximumUnitPrice is { } value ? new Money(value) : null),
        "sell" when MaximumUnitPrice is null && MinimumUnitPrice is null or >= 0 =>
            new AutomaticGoodsAction.Sell(MinimumUnitPrice is { } value ? new Money(value) : null),
        _ => throw new InvalidDataException($"不支持的自动货物动作或参数：{Kind}。")
    };
}
