using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Train.Core.Contracts;

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
            command.Parse();
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
    RecipeLeaseRequest? Request = null,
    ulong? Seller = null,
    ulong? Vehicle = null,
    long? MaximumPayment = null,
    ulong? Settlement = null,
    ulong? LocalInventory = null,
    RecipeGood? Good = null,
    long? Quantity = null)
{
    internal CoreCommand Parse()
    {
        var trainCommand = Kind is "train.replace_run_plan" or "train.start" or "train.stop";
        var transfer = Kind is "inventory.load_goods" or "inventory.unload_goods";
        var lease = Kind == "lease.acquire";
        var vehicle = Kind == "market.buy_vehicle";
        if (!(trainCommand || transfer || lease || vehicle)
            || (trainCommand || transfer ? Train is null or 0 : Train is not null)
            || Kind != "train.replace_run_plan" && Plan is not null
            || !lease && Request is not null
            || !vehicle && (Seller is not null || Vehicle is not null)
            || !(lease || vehicle) && MaximumPayment is not null
            || !transfer && (Settlement is not null || LocalInventory is not null || Good is not null || Quantity is not null))
            throw Invalid();
        switch (Kind)
        {
            case "train.replace_run_plan" when Plan is { Stops.IsDefault: false, Stops.IsEmpty: false } plan:
                return new CoreCommand.ReplaceRunPlan(new(Train!.Value), new(plan.Loop,
                    plan.Stops.Select(stop => stop?.Parse() ?? throw Invalid()).ToImmutableArray()));
            case "train.start": return new CoreCommand.StartTrain(new(Train!.Value));
            case "train.stop": return new CoreCommand.StopTrain(new(Train!.Value));
            case "lease.acquire" when Request is { Kind: "rent", Settlement: > 0, Capacity: > 0, DurationSteps: > 0 } rent
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
            default: throw Invalid();
        }
    }

    private InvalidDataException Invalid() => new($"不支持的配方命令或参数：{Kind}。");
}

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
