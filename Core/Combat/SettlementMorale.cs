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
        var changed = false;
        var settlements = world.Settlements.Select(settlement =>
        {
            if (world.Combats.Any(combat => combat.Location is WorldLocation.Node node
                    && node.Id == settlement.Node)) return settlement;
            var morale = Math.Min(1, settlement.Morale + rule.MoraleRecoveryPerSecond * SimulationTime.StepSeconds);
            var surrendered = settlement.Surrendered && morale < rule.SurrenderThreshold;
            if (morale == settlement.Morale && surrendered == settlement.Surrendered) return settlement;
            changed = true;
            return settlement with { Morale = morale, Surrendered = surrendered };
        }).ToImmutableArray();
        return changed ? world with { Settlements = settlements } : world;
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
