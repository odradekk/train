using System.Collections.Immutable;
using System.Text;
using Train.App;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed record BattleReport(CombatId Combat, WorldLocation Location, CombatSummary Summary);

internal sealed class BattleReportLog
{
    private readonly List<BattleReport> _reports = [];
    private Guid? _session;

    internal IReadOnlyList<BattleReport> Reports => _reports;

    internal void Clear()
    {
        _reports.Clear();
        _session = null;
    }

    internal void Observe(HostBinding binding, ImmutableArray<EventEnvelope> events)
    {
        if (_session != binding.Session)
        {
            _reports.Clear();
            _session = binding.Session;
        }
        foreach (var envelope in events)
            if (envelope.Event is CoreEvent.CombatEnded ended)
                _reports.Add(new(ended.Combat, ended.Location, ended.Summary));
    }

    internal static string Describe(BattleReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(Place(report.Location));
        text.AppendLine($"开始步：{report.Summary.StartedAtStep}");
        text.AppendLine($"结束步：{report.Summary.EndedAtStep}");
        foreach (var side in report.Summary.Sides)
        {
            text.AppendLine(Owner(side.Owner));
            foreach (var unit in side.UnitsLost) text.AppendLine($"损失单位：{Unit(unit)}");
            foreach (var part in side.PartsLost) text.AppendLine($"损失部件：{Instance(part.Instance)} 部件 {part.Part.Value}");
            foreach (var cargo in side.CargoLost) text.AppendLine($"损失货物：{Good(cargo.Good)} × {cargo.Quantity}");
            text.AppendLine($"奖励：{side.Reward.Value}");
        }
        return text.ToString().ReplaceLineEndings("\n");
    }

    internal static string Place(WorldLocation location) => location switch
    {
        WorldLocation.Node node => $"节点 {node.Id.Value}",
        WorldLocation.Edge edge => $"线路 {edge.Id.Value} · {edge.DistanceMeters:0.##} m",
        _ => location.ToString()
    };

    private static string Owner(CombatOwner owner) => owner switch
    {
        CombatOwner.Player => "玩家",
        CombatOwner.Settlement settlement => $"聚居地 {settlement.Id.Value}",
        CombatOwner.Creatures => "生物",
        _ => owner.ToString()
    };

    private static string Unit(DestroyedUnit unit) => unit switch
    {
        DestroyedUnit.Instance instance => Instance(instance.Value),
        DestroyedUnit.Creature creature => $"生物 {creature.Value.Creature.Value}",
        _ => unit.ToString()
    };

    private static string Instance(InstancePresentationRef instance) => instance.Instance switch
    {
        InstanceId.Vehicle vehicle => $"车辆 {vehicle.Id.Value}（设计 {instance.Design.Value}）",
        InstanceId.Building building => $"建筑 {building.Id.Value}（设计 {instance.Design.Value}）",
        _ => instance.Instance.ToString()
    };

    private static string Good(GoodId good) => good switch
    {
        GoodId.Voxel voxel => voxel.Definition.Value,
        GoodId.Composite composite => composite.Definition.Value,
        GoodId.Assembly assembly => assembly.Composition.Value,
        _ => good.ToString()
    };
}
