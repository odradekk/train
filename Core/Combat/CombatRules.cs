using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Combat;

internal static class CombatRules
{
    internal static TrainEngagementRules DefaultTrainEngagement(bool armed) => new(
        TargetPriority.ArmedFirst, TargetScope.ArmedOnly,
        armed ? EncounterAction.Engage : EncounterAction.Retreat, 0);

    internal static SettlementEngagementRules DefaultSettlementEngagement { get; } = new(
        TargetPriority.ArmedFirst, TargetScope.All);

    internal static bool Armed(DesignProjection design) => design.Parts.Any(part =>
        part.MaxDurability > 0 && part.Capabilities.Any(capability => capability is PartCapability.Weapon));

    internal static bool Armed(WorldState world, CoreContentProjection content, InstanceState instance)
    {
        var design = world.Designs.Single(d => d.Design == instance.Design);
        return design.Parts.Any(part => part.Capabilities.Any(capability => capability is PartCapability.Weapon)
            && !InstanceCapabilities.DefinitionMissing(world, content, design.Design, part)
            && Math.Min(instance.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability) > 0);
    }

    internal static bool Valid(TrainEngagementRules rules) => rules is not null
        && Enum.IsDefined(rules.Priority) && Enum.IsDefined(rules.Scope) && Enum.IsDefined(rules.Encounter)
        && double.IsFinite(rules.RetreatStructureRatio) && rules.RetreatStructureRatio is >= 0 and <= 1;

    internal static bool Valid(SettlementEngagementRules rules) => rules is not null
        && Enum.IsDefined(rules.Priority) && Enum.IsDefined(rules.Scope);

    internal static CombatRuleProjection? FindRule(WorldState world, CoreContentProjection content)
        => content.CombatRules.SingleOrDefault(rule => rule.Id == world.Source.Configuration.Rules.Combat);

    internal static CombatRuleProjection Rule(WorldState world, CoreContentProjection content)
        => FindRule(world, content) ?? throw new InvalidOperationException("战斗规则缺失。");
}
