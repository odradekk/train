using System.Collections.Immutable;
using System.Text.Json;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record LairMemberReference(string Creature, string Field);
internal sealed record ParsedLair(K.LairTypeProjection Core, C.DisplayText Display,
    C.BlueprintAddress SceneModel, ImmutableArray<LairMemberReference> Members, string Path);
internal sealed record LoadedLair(K.LairTypeProjection Core, C.DisplayText Display, C.SceneModelProjection Scene);

internal static class LairParser
{
    internal static ParsedLair? Parse(byte[] bytes, LoadPackage package, string path)
    {
        using var document = ContentDocument.Parse(bytes, package, path);
        if (document is null) return null;
        C.ContentReference? subject = null;
        try
        {
            var root = document.RootElement;
            Object(root, "", "schema_version", "id", "display", "generation_weight", "members", "maximum_members",
                "spawn_interval_seconds", "raid_interval_seconds", "roaming_radius_hops", "raid_radius_hops",
                "rail_damage_work_seconds", "clearance_reward", "scene_model");
            var json = new DefinitionJson(root, package, path, "lair-type", 1);
            var id = new K.LairTypeId(json.Id);
            subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Lair(id));
            var references = ImmutableArray.CreateBuilder<LairMemberReference>();
            var members = ImmutableArray.CreateBuilder<K.WeightedCreature>();
            foreach (var (value, field) in Items(Required(root, "members", ""), "/members"))
            {
                Object(value, field, "creature", "weight");
                var creature = json.Reference(Required(value, "creature", field), field + "/creature");
                references.Add(new(creature, field + "/creature"));
                members.Add(new(new(creature), Int32(Required(value, "weight", field), field + "/weight", 1)));
            }
            if (members.Count == 0) Fail("/members", "成员不能为空。");
            Unique(members.Select(member => member.Creature), "/members");
            var ordered = members.OrderBy(member => member.Creature.Value, StringComparer.Ordinal).ToImmutableArray();
            var weight = Int32(Required(root, "generation_weight", ""), "/generation_weight", 1);
            var maximum = Int32(Required(root, "maximum_members", ""), "/maximum_members", 1);
            var spawn = Number(Required(root, "spawn_interval_seconds", ""), "/spawn_interval_seconds", positive: true);
            var raid = Number(Required(root, "raid_interval_seconds", ""), "/raid_interval_seconds", positive: true);
            var roaming = Int32(Required(root, "roaming_radius_hops", ""), "/roaming_radius_hops", 0);
            var radius = Int32(Required(root, "raid_radius_hops", ""), "/raid_radius_hops", 0);
            var damage = Number(Required(root, "rail_damage_work_seconds", ""), "/rail_damage_work_seconds", positive: true);
            var reward = Int64(Required(root, "clearance_reward", ""), "/clearance_reward", 0);
            var projection = new K.LairTypeProjection(id, Semantic("lair-type", json.Id, new
            {
                generation_weight = weight,
                members = ordered.Select(member => new { creature = member.Creature.Value, weight = member.Weight }),
                maximum_members = maximum,
                spawn_interval_seconds = spawn,
                raid_interval_seconds = raid,
                roaming_radius_hops = roaming,
                raid_radius_hops = radius,
                rail_damage_work_seconds = damage,
                clearance_reward = reward
            }), weight, ordered, maximum, spawn, raid, roaming, radius, damage, new(reward));
            return new(projection, Display(Required(root, "display", "")),
                new(C.BlueprintLibrary.Packages, new(json.Reference(Required(root, "scene_model", ""), "/scene_model"))),
                references.ToImmutable(), path);
        }
        catch (DefinitionFormatException error)
        {
            package.Reject(error.Code, path, error.Field, error.Message, subject);
            return null;
        }
    }
}
