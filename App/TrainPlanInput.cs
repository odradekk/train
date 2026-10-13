using System.Collections.Immutable;
using System.Globalization;
using Train.Core.Contracts;

namespace Train.App;

internal sealed record PlanRowInput(NodeId Node, bool Station, string DwellSeconds, ulong? UnchangedDwellSteps = null);

internal static class TrainPlanInput
{
    internal static bool TryPlan(TrainId train, bool loop, IReadOnlyList<PlanRowInput> rows,
        out CoreCommand.ReplaceRunPlan? command, out string? error)
    {
        command = null;
        error = "列车和节点 ID 必须非零，计划至少有一项，停站秒数必须为可换算为模拟步数的非负整数。";
        if (train.Value == 0 || rows.Count == 0) return false;
        var stops = ImmutableArray.CreateBuilder<PlanStop>();
        foreach (var row in rows)
        {
            if (row.Node.Value == 0) return false;
            if (!row.Station)
            {
                stops.Add(new PlanStop.Waypoint(row.Node));
                continue;
            }
            ulong steps;
            if (row.UnchangedDwellSteps is { } unchanged) steps = unchanged;
            else
            {
                if (!ulong.TryParse(row.DwellSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    return false;
                try { steps = checked(seconds * (ulong)SimulationTime.StepsPerSecond); }
                catch (OverflowException) { return false; }
            }
            stops.Add(new PlanStop.Station(row.Node, steps, []));
        }
        command = new(train, new(loop, stops.ToImmutable()));
        error = null;
        return true;
    }
}
