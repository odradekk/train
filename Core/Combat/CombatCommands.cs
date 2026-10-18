using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Combat;

internal static class CombatCommands
{
    internal static void Validate(CoreCommand command)
    {
        switch (command)
        {
            case CoreCommand.SetTrainEngagement set:
                Identity(set.Train.Value, nameof(command));
                if (!CombatRules.Valid(set.Rules)) throw new ArgumentException("列车交战规则无效。", nameof(command));
                return;
            case CoreCommand.SetSettlementEngagement set:
                Identity(set.Settlement.Value, nameof(command));
                if (!CombatRules.Valid(set.Rules)) throw new ArgumentException("聚居地交战规则无效。", nameof(command));
                return;
            case CoreCommand.Retreat retreat:
                Identity(retreat.Train.Value, nameof(command));
                return;
            case CoreCommand.FocusFire focus:
                Identity(focus.Combat.Value, nameof(command));
                ArgumentNullException.ThrowIfNull(focus.Controller);
                ArgumentNullException.ThrowIfNull(focus.Target);
                switch (focus.Controller)
                {
                    case CombatController.Train train: Identity(train.Id.Value, nameof(command)); break;
                    case CombatController.Settlement settlement: Identity(settlement.Id.Value, nameof(command)); break;
                    default: throw new ArgumentException("战斗控制方无效。", nameof(command));
                }
                return;
            default:
                throw new InvalidOperationException("战斗命令种类无效。");
        }
    }

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, CoreCommand command)
    {
        switch (command)
        {
            case CoreCommand.SetTrainEngagement set:
            {
                var train = world.Trains.SingleOrDefault(t => t.Id == set.Train);
                if (train is null) return Reject("object_missing", "Train", new WorldObjectId.Train(set.Train));
                return Accepted(content, world with
                {
                    Trains = world.Trains.Select(t => t.Id == set.Train ? t with { Engagement = set.Rules } : t)
                        .ToImmutableArray()
                });
            }
            case CoreCommand.SetSettlementEngagement set:
            {
                var settlement = world.Settlements.SingleOrDefault(s => s.Id == set.Settlement);
                if (settlement is null)
                    return Reject("object_missing", "Settlement", new WorldObjectId.Settlement(set.Settlement));
                if (settlement.Relation != SettlementRelation.PlayerOwned)
                    return Reject("not_owned", "Settlement", new WorldObjectId.Settlement(set.Settlement));
                return Accepted(content, world with
                {
                    Settlements = world.Settlements.Select(s => s.Id == set.Settlement ? s with { Engagement = set.Rules } : s)
                        .ToImmutableArray()
                });
            }
            case CoreCommand.Retreat retreat:
                return Retreat(world, content, retreat.Train);
            case CoreCommand.FocusFire focus:
                return Focus(world, content, focus);
            default:
                throw new InvalidOperationException("战斗命令种类无效。");
        }
    }

    private static CommandEffect Retreat(WorldState world, CoreContentProjection content, TrainId trainId)
    {
        var train = world.Trains.SingleOrDefault(t => t.Id == trainId);
        if (train is null) return Reject("object_missing", "Train", new WorldObjectId.Train(trainId));
        var controller = new CombatController.Train(trainId);
        var found = CombatQueries.Controller(world, controller);
        if (found is null)
            return train.Activity == TrainActivity.Retreating
                ? Accepted(content, world) : Reject("invalid_state", "Train.Activity", new WorldObjectId.Train(trainId));
        var (combat, index) = found.Value;
        var state = combat.Controllers[index];
        if (state.Retreating) return Accepted(content, world);
        var rule = content.CombatRules.SingleOrDefault(r => r.Id == world.Source.Configuration.Rules.Combat);
        if (rule is null) return Reject("content_missing", "CombatRule");
        var steps = WorldResources.IntervalSteps(rule.RetreatSeconds)
            ?? throw new InvalidOperationException("战斗规则脱离时长超出可表示范围。");
        var controllers = combat.Controllers.SetItem(index, state with
        {
            Retreating = true, RemainingDisengagementSteps = steps
        });
        var events = CombatQueries.Units(world, controller)
            .Select(unit => (CoreEvent)new CoreEvent.CombatRetreatStarted(combat.Id, unit, steps)).ToImmutableArray();
        return new CommandEffect.Accepted(content, events, world with
        {
            Combats = world.Combats.Select(c => c.Id == combat.Id ? combat with { Controllers = controllers } : c)
                .ToImmutableArray(),
            Trains = world.Trains.Select(t => t.Id == trainId
                ? t with { Activity = TrainActivity.Retreating, SpeedMetersPerSecond = 0 } : t).ToImmutableArray()
        });
    }

    private static CommandEffect Focus(WorldState world, CoreContentProjection content, CoreCommand.FocusFire focus)
    {
        var combat = world.Combats.SingleOrDefault(c => c.Id == focus.Combat);
        if (combat is null) return Reject("object_missing", "Combat", new WorldObjectId.Combat(focus.Combat));
        if (CombatQueries.Controller(world, focus.Controller) is not { } located || located.Combat.Id != combat.Id)
            return Reject("invalid_state", "Controller", Subject(focus.Controller));
        if (focus.Controller is CombatController.Settlement settlement)
        {
            var state = world.Settlements.SingleOrDefault(s => s.Id == settlement.Id);
            if (state is null) return Reject("object_missing", "Controller", Subject(focus.Controller));
            if (state.Relation != SettlementRelation.PlayerOwned)
                return Reject("not_owned", "Controller", Subject(focus.Controller));
        }
        if (focus.Controller is CombatController.Train trainController
            && !world.Trains.Any(t => t.Id == trainController.Id))
            return Reject("object_missing", "Controller", Subject(focus.Controller));
        if (CombatQueries.Target(focus.Target) is not { } target)
            return Reject("invalid_state", "Target", focus.Target);
        if (!CombatQueries.Exists(world, target))
            return Reject("object_missing", "Target", focus.Target);
        if (!CombatQueries.IsParticipant(world, combat, target))
            return Reject("invalid_state", "Target", focus.Target);
        var units = CombatQueries.Units(world, focus.Controller);
        if (!units.Any(unit => CombatQueries.Hostile(world, unit, target)))
            return Reject("invalid_state", "Target", focus.Target);
        var scope = focus.Controller switch
        {
            CombatController.Train trainScope => world.Trains.Single(t => t.Id == trainScope.Id).Engagement.Scope,
            CombatController.Settlement settlementScope => world.Settlements.Single(s => s.Id == settlementScope.Id).Engagement.Scope,
            _ => throw new InvalidOperationException("战斗控制方种类无效。")
        };
        if (scope == TargetScope.ArmedOnly && !CombatQueries.Armed(world, content, target))
            return Reject("invalid_state", "Target", focus.Target);
        var index = located.Index;
        var controllers = combat.Controllers.SetItem(index, combat.Controllers[index] with { FocusTarget = target });
        return Accepted(content, world with
        {
            Combats = world.Combats.Select(c => c.Id == combat.Id ? combat with { Controllers = controllers } : c)
                .ToImmutableArray()
        });
    }

    private static WorldObjectId Subject(CombatController controller) => controller switch
    {
        CombatController.Train train => new WorldObjectId.Train(train.Id),
        CombatController.Settlement settlement => new WorldObjectId.Settlement(settlement.Id),
        _ => throw new InvalidOperationException("战斗控制方种类无效。")
    };

    private static CommandEffect Accepted(CoreContentProjection content, WorldState world)
        => new CommandEffect.Accepted(content, [], world);

    private static CommandEffect.Rejected Reject(string code, string field, WorldObjectId? subject = null)
        => new([new CoreProblem(code, field, subject, [])]);

    private static void Identity(ulong value, string parameter)
    {
        if (value == 0) throw new ArgumentException("世界身份必须非零。", parameter);
    }
}
