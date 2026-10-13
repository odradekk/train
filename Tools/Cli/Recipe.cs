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
    [property: JsonRequired] SimulationPosition Target)
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
        return recipe;
    }
}

internal sealed record RecipeSave(
    [property: JsonRequired] Guid Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] DateTimeOffset SavedAt);

internal sealed record RecipeCommand(
    [property: JsonRequired] SimulationPosition At,
    [property: JsonRequired] string Kind,
    [property: JsonRequired] ulong Train,
    RecipePlan? Plan = null)
{
    internal CoreCommand Parse() => Kind switch
    {
        "train.replace_run_plan" when Plan is { Stops.IsDefault: false, Stops.IsEmpty: false } plan =>
            new CoreCommand.ReplaceRunPlan(new(Train), new(plan.Loop,
                plan.Stops.Select(stop => stop is { Kind: "waypoint" }
                    ? (PlanStop)new PlanStop.Waypoint(new(stop.Node))
                    : throw new InvalidDataException($"不支持的计划站点：{stop?.Kind}。"))
                .ToImmutableArray())),
        "train.start" when Plan is null => new CoreCommand.StartTrain(new(Train)),
        "train.stop" when Plan is null => new CoreCommand.StopTrain(new(Train)),
        _ => throw new InvalidDataException($"不支持的配方命令或参数：{Kind}。")
    };
}

internal sealed record RecipePlan(
    [property: JsonRequired] bool Loop,
    [property: JsonRequired] ImmutableArray<RecipePlanStop> Stops);

internal sealed record RecipePlanStop(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] ulong Node);
