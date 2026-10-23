using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Combat;

internal sealed record SettlementMoraleDamage(SettlementId Settlement, long Damage, int WeaponsDestroyed);

internal static class SettlementMorale
{
    internal static WorldState Recover(WorldState world, CoreContentProjection content)
    {
        if (CombatRules.FindRule(world, content) is not { } rule) return world;
        ImmutableArray<SettlementState>.Builder? settlements = null;
        for (var index = 0; index < world.Settlements.Length; index++)
        {
            var settlement = world.Settlements[index];
            if (settlement.Morale == 1 && !settlement.Surrendered) continue;
            if (world.Combats.Any(combat => combat.Location is WorldLocation.Node node
                    && node.Id == settlement.Node)) continue;
            if (HasHostileUnit(world, settlement)) continue;
            var morale = Math.Min(1, settlement.Morale + rule.MoraleRecoveryPerSecond * SimulationTime.StepSeconds);
            var surrendered = settlement.Surrendered && morale < rule.SurrenderThreshold;
            if (morale == settlement.Morale && surrendered == settlement.Surrendered) continue;
            settlements ??= world.Settlements.ToBuilder();
            settlements[index] = settlement with { Morale = morale, Surrendered = surrendered };
        }
        return settlements is null ? world : world with { Settlements = settlements.ToImmutable() };
    }

    internal static WorldState Apply(WorldState world, WorldState before, CoreContentProjection content,
        CombatId combat, ImmutableArray<SettlementMoraleDamage> damages,
        ImmutableArray<CoreEvent>.Builder events)
    {
        if (damages.IsEmpty || CombatRules.FindRule(world, content) is not { } rule) return world;
        foreach (var damage in damages)
        {
            var settlement = world.Settlements.Single(item => item.Id == damage.Settlement);
            var denominator = BuildingDurability(before, damage.Settlement);
            var loss = (denominator == 0 ? 0 : damage.Damage / (double)denominator)
                * rule.MoraleDamageCoefficient
                + damage.WeaponsDestroyed * rule.WeaponDestroyedMoraleLoss;
            var morale = Math.Clamp(settlement.Morale - loss, 0, 1);
            var nowSurrendered = !settlement.Surrendered
                && settlement.Relation == SettlementRelation.Hostile
                && morale < rule.SurrenderThreshold;
            if (nowSurrendered) events.Add(new CoreEvent.SettlementSurrendered(combat, settlement.Id));
            world = world with
            {
                Settlements = world.Settlements.Select(item => item.Id == settlement.Id
                    ? item with { Morale = morale, Surrendered = item.Surrendered || nowSurrendered } : item)
                    .ToImmutableArray()
            };
        }
        return world;
    }

    // 生物群对所有聚居地敌对；玩家列车只对敌对聚居地敌对。
    private static bool HasHostileUnit(WorldState world, SettlementState settlement)
        => world.CreatureGroups.Any(group => group.Location is WorldLocation.Node node
            && node.Id == settlement.Node)
            || (settlement.Relation == SettlementRelation.Hostile
                && world.Trains.Any(train => train.Position is TrainPosition.Node node
                    && node.Id == settlement.Node));

    private static long BuildingDurability(WorldState world, SettlementId id)
    {
        var settlement = world.Settlements.Single(item => item.Id == id);
        long total = 0;
        foreach (var plot in settlement.Plots.Where(plot => plot.Building is not null))
        {
            var instance = world.Instances.Single(item => item.Id == new InstanceId.Building(plot.Building!.Value));
            var design = world.Designs.Single(item => item.Design == instance.Design);
            total = checked(total + design.MaxStructureDurability);
        }
        return total;
    }
}
