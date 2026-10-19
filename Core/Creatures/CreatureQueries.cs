using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Creatures;

internal static class CreatureQueries
{
    internal static CreatureProjection? Projection(CoreContentProjection content, CreatureDefinitionId id)
        => content.Creatures.FirstOrDefault(creature => creature.Id == id);

    internal static long MaximumHealth(CoreContentProjection content, CreatureState creature)
        => Projection(content, creature.Definition)?.HitPoints ?? creature.Health;

    internal static long EffectiveHealth(CoreContentProjection content, CreatureState creature)
        => Math.Max(0, Math.Min(creature.Health, MaximumHealth(content, creature)));

    internal static bool MemberArmed(CoreContentProjection content, CreatureState creature)
        => Projection(content, creature.Definition)?.Weapon is not null;

    internal static long GroupDurability(CoreContentProjection content, CreatureGroupState group)
    {
        var total = 0L;
        foreach (var creature in group.Creatures)
            total = checked(total + EffectiveHealth(content, creature));
        return total;
    }

    internal static bool GroupArmed(CoreContentProjection content, CreatureGroupState group)
        => group.Creatures.Any(creature => MemberArmed(content, creature));

    internal static bool GroupHittable(CreatureGroupState group) => !group.Creatures.IsEmpty;

    internal static double GroupSpeed(CoreContentProjection content, CreatureGroupState group)
    {
        double? speed = null;
        foreach (var creature in group.Creatures)
        {
            var projection = Projection(content, creature.Definition);
            if (projection is null) continue;
            speed = speed is null ? projection.SpeedMetersPerSecond : Math.Min(speed.Value, projection.SpeedMetersPerSecond);
        }
        return speed is { } value && double.IsFinite(value) && value > 0 ? value : 0;
    }

    internal static double? MinRetreatRatio(CoreContentProjection content, CreatureGroupState group)
    {
        double? ratio = null;
        foreach (var creature in group.Creatures)
        {
            var projection = Projection(content, creature.Definition);
            if (projection is null) continue;
            ratio = ratio is null ? projection.RetreatLossRatio : Math.Min(ratio.Value, projection.RetreatLossRatio);
        }
        return ratio;
    }

    internal static double? MinRaidDuration(CoreContentProjection content, CreatureGroupState group)
    {
        double? duration = null;
        foreach (var creature in group.Creatures)
        {
            var projection = Projection(content, creature.Definition);
            if (projection is null) continue;
            duration = duration is null ? projection.RaidDurationSeconds : Math.Min(duration.Value, projection.RaidDurationSeconds);
        }
        return duration;
    }

    internal static bool AtOwnLair(WorldState world, CreatureGroupState group)
    {
        if (group.Home is not { } home || group.Location is not WorldLocation.Node node) return false;
        var lair = world.Lairs.FirstOrDefault(item => item.Id == home);
        return lair is not null && lair.Node == node.Id;
    }

    internal static bool IsOut(WorldState world, CreatureGroupState group) =>
        group.Activity is CreatureActivity.Wandering or CreatureActivity.RaidingSettlement
            or CreatureActivity.RaidingRailway or CreatureActivity.Retreating
        || group.Activity == CreatureActivity.Combat && !AtOwnLair(world, group);

    internal static bool IsGarrison(WorldState world, CreatureGroupState group) =>
        group.Activity == CreatureActivity.Guarding
        || group.Activity == CreatureActivity.Combat && AtOwnLair(world, group);

    internal static bool InCombat(WorldState world, CreatureGroupId id)
        => world.Combats.Any(combat => combat.Creatures.Any(creature => creature.Group == id));

    internal static CreatureGroupState? FindGroup(WorldState world, CreatureGroupId id)
        => world.CreatureGroups.FirstOrDefault(group => group.Id == id);

    internal static CreatureView ReadCreature(CoreContentProjection content, CreatureState creature)
        => new(creature.Id, creature.Definition, EffectiveHealth(content, creature), MaximumHealth(content, creature));

    internal static CreatureGroupView ReadGroup(CoreContentProjection content, CreatureGroupState group)
        => new(group.Id, group.Home, group.Location, group.Direction, group.Activity, GroupSpeed(content, group),
            group.Creatures.Select(creature => ReadCreature(content, creature)).ToImmutableArray());

    internal static LairView ReadLair(WorldState world, LairState lair)
        => new(lair.Id, lair.Node, lair.Type, world.CreatureGroups.Where(group => group.Home == lair.Id)
            .Select(group => group.Id).ToImmutableArray());

    internal static WorldState Replace(WorldState world, CreatureGroupState group)
        => world with
        {
            CreatureGroups = world.CreatureGroups.Where(item => item.Id != group.Id).Append(group)
                .OrderBy(item => item.Id.Value).ToImmutableArray()
        };

    internal static WorldState Remove(WorldState world, CreatureGroupId id)
        => world with { CreatureGroups = world.CreatureGroups.Where(group => group.Id != id).ToImmutableArray() };
}
