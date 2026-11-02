using System.Collections.Immutable;
using System.Globalization;
using Train.App.Logging;
using Train.Core.Contracts;

namespace Train.App;

internal sealed class TrainConsoleCommands(HostCoordinator host, TrainLogger logger)
{
    internal const string Usage = "用法：train form <车辆ID>...、train disband|start|stop <列车ID>，或 train plan <列车ID> once|loop station:<节点ID>:<停站秒数> waypoint:<节点ID>...；请输入非零 ID，停站秒数须为可换算为模拟步数的非负整数。";

    internal void Execute(IReadOnlyList<string> args)
    {
        if (!TryParse(args, out var command, out var error))
        {
            logger.Warning("train", "train_usage", error!);
            return;
        }
        if (host.GetStatus() is not { } status)
        {
            logger.Warning("train", "train_session_unavailable", "没有活动模拟会话，请先创建或读取存档，再执行列车命令。");
            return;
        }
        var result = host.Submit(status.Binding, command!, CommandSource.Console);
        CommandDiagnostics.Report(logger, result, host.Content);
    }

    internal static bool TryParse(IReadOnlyList<string> args, out CoreCommand? command, out string? error)
    {
        command = null;
        error = Usage;
        if (args.Count < 2) return false;
        var operation = args[0].ToLowerInvariant();
        if (operation == "form")
        {
            var vehicles = ImmutableArray.CreateBuilder<VehicleId>();
            for (var i = 1; i < args.Count; i++)
            {
                if (!TryId(args[i], out var vehicle)) return false;
                vehicles.Add(new(vehicle));
            }
            command = new CoreCommand.FormTrain(vehicles.ToImmutable());
        }
        else
        {
            if (!TryId(args[1], out var id)) return false;
            var train = new TrainId(id);
            if (args.Count == 2)
                command = operation switch
                {
                    "disband" => new CoreCommand.DisbandTrain(train),
                    "start" => new CoreCommand.StartTrain(train),
                    "stop" => new CoreCommand.StopTrain(train),
                    _ => null
                };
            else if (operation == "plan" && args.Count >= 4)
            {
                var mode = args[2].ToLowerInvariant();
                if (mode is not ("once" or "loop")) return false;
                var rows = new List<PlanRowInput>();
                for (var i = 3; i < args.Count; i++)
                {
                    var parts = args[i].Split(':');
                    if (parts.Length < 2 ||
                        !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var node)) return false;
                    if (parts[0].Equals("waypoint", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
                        rows.Add(new(new(node), false, ""));
                    else if (parts[0].Equals("station", StringComparison.OrdinalIgnoreCase) && parts.Length == 3)
                        rows.Add(new(new(node), true, parts[2]));
                    else return false;
                }
                var parsed = TrainPlanInput.TryPlan(train, mode == "loop", rows, out var plan, out error);
                command = plan;
                if (!parsed) error += " " + Usage;
                return parsed;
            }
        }
        if (command is null) return false;
        error = null;
        return true;
    }

    private static bool TryId(string text, out ulong id) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != 0;
}
