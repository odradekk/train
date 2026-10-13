using Train.App.Console;
using Train.App.Logging;
using Train.Ui;

namespace Train.App;

internal sealed class LightingConsoleCommands(
    TrainLogger logger, Func<InspectionLighting?> current, Action<InspectionLighting> apply)
{
    internal const string ModeEventId = "lighting_mode";
    internal const string UsageEventId = "lighting_usage";
    internal const string UnavailableEventId = "lighting_unavailable";

    internal void Execute(IReadOnlyList<string> arguments)
    {
        var requested = arguments.Count == 1 ? arguments[0].ToLowerInvariant() switch
        {
            "daylight" => InspectionLighting.Daylight,
            "overcast" => InspectionLighting.Overcast,
            "night" => InspectionLighting.Night,
            "restore" => InspectionLighting.Game,
            _ => (InspectionLighting?)null
        } : null;
        if (arguments.Count > 1 || arguments.Count == 1 && requested is null)
        {
            logger.Warning(ConsoleCommandDispatcher.Category, UsageEventId,
                "用法: lighting [daylight|overcast|night|restore]");
            return;
        }
        var mode = current();
        if (mode is null)
        {
            logger.Warning(ConsoleCommandDispatcher.Category, UnavailableEventId, "检查光照不可用：没有世界场景视图。");
            return;
        }
        if (requested is { } selected)
        {
            apply(selected);
            mode = selected;
        }
        logger.Info(ConsoleCommandDispatcher.Category, ModeEventId, $"检查光照：{mode.Value switch
        {
            InspectionLighting.Game => "游戏光照",
            InspectionLighting.Daylight => "中性日光",
            InspectionLighting.Overcast => "阴天",
            InspectionLighting.Night => "夜间人工照明",
            _ => throw new InvalidOperationException("未知检查光照。")
        }}。", new Dictionary<string, object?> { ["mode"] = mode.Value.ToString().ToLowerInvariant() });
    }
}
