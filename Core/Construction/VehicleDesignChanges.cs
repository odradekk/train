using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Construction;

internal static class VehicleDesignChanges
{
    internal static void Validate(InstanceId instance, DesignChangeProjection change, WorldState world,
        CoreContentProjection content)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance is InstanceId.Building) throw new NotImplementedException("建筑设计应用尚未实现。");
        if (instance is not InstanceId.Vehicle { Id.Value: > 0 })
            throw new ArgumentException("车辆编号必须非零。", nameof(instance));
        ValidateInput(WorldInputs.NormalizeDesignChange(change).Target, world, content);
    }

    internal static void ValidateInput(DesignInput input, WorldState world, CoreContentProjection content)
    {
        input = WorldInputs.NormalizeDesignInput(input);
        if (input.InputIdentity != content.Identity) return;
        var current = content.Designs.SingleOrDefault(d => d.Design == input.Design.Design)
            ?? world.Designs.SingleOrDefault(d => d.Design == input.Design.Design);
        if (current is not null && !WorldJson.SameValue(input.Design, WorldInputs.NormalizeDesign(current)))
            throw new ArgumentException("同一设计身份的业务投影相互矛盾。", nameof(input));
        foreach (var good in input.AssemblyGoods)
        {
            var existing = content.DesignGoods.SingleOrDefault(g => g.Good == good.Good)
                ?? world.DesignGoods.SingleOrDefault(g => g.Good == good.Good);
            if (existing is not null && !WorldJson.SameValue(
                    existing with { MassTons = WorldInputs.Zero(existing.MassTons) }, good))
                throw new ArgumentException("同一组装体货物身份的投影相互矛盾。", nameof(input));
        }
    }

    internal static WorldState AddTarget(WorldState world, DesignInput input) => world with
    {
        Designs = world.Designs.Where(d => d.Design != input.Design.Design).Append(input.Design)
            .OrderBy(d => d.Design.Value, StringComparer.Ordinal).ToImmutableArray(),
        DesignGoods = world.DesignGoods.Concat(input.AssemblyGoods).DistinctBy(g => g.Good)
            .OrderBy(g => WorldReferences.GoodKey(g.Good), StringComparer.Ordinal).ToImmutableArray()
    };

    internal static bool Missing(WorldState world, CoreContentProjection content, DesignProjection design)
        => !design.MissingReferences.IsEmpty
            || design.Parts.Any(p => InstanceCapabilities.DefinitionMissing(world, content, design.Design, p))
            || design.ReturnUnits.Any(u => !u.MissingReferences.IsEmpty)
            || WorldReferences.DesignGoods(design).Any(g => WorldGoods.DefinitionMissing(world, content, g.Good));

    internal static CoreProblem? Check(WorldState world, CoreContentProjection content, InstanceState instance,
        DesignChangeProjection change, bool immediate)
    {
        var before = world.Designs.Single(d => d.Design == instance.Design);
        var target = change.Target.Design;
        if (change.Target.InputIdentity != content.Identity) return Problem("content_changed", "Change.Target.InputIdentity");
        if (change.Before != instance.Design || change.BeforeComposition != before.Composition)
            return Problem("invalid_state", "Change.Before");
        if (Missing(world, content, before) || Missing(AddTarget(world, change.Target), content, target))
            return Problem("content_missing", "Change");
        if (target.Kind is not DesignKind.Vehicle || !target.CanConstruct || target.MaxStructureDurability == 0
            || immediate && !before.CanConstruct)
            return Problem("invalid_design", "Change.Target");
        var removedParts = change.RemovedItems.SelectMany(u => u.Parts).ToArray();
        if (removedParts.Distinct().Count() != removedParts.Length
            || before.Parts.Any(p => !change.RetainedParts.Any(pair => pair.Before == p.Id) && !removedParts.Contains(p.Id))
            || change.RetainedParts.Any(pair => !before.Parts.Any(p => p.Id == pair.Before)
                || !target.Parts.Any(p => p.Id == pair.After)
                || before.Parts.Single(p => p.Id == pair.Before).Definition != target.Parts.Single(p => p.Id == pair.After).Definition)
            || change.RemovedItems.SelectMany(u => u.Parts).Any(id => !before.Parts.Any(p => p.Id == id)
                || change.RetainedParts.Any(p => p.Before == id)))
            return Problem("invalid_state", "Change.RetainedParts");
        if (immediate && (before.Composition != target.Composition || !change.AddedGoods.IsEmpty
                || !change.RemovedItems.IsEmpty || change.ChangedItemCount != 0
                || change.RetainedParts.Length != before.Parts.Length || change.RetainedParts.Length != target.Parts.Length))
            return Problem("invalid_design", "Change.Target");
        return null;

        CoreProblem Problem(string code, string field) => new(code, field, new WorldObjectId.Instance(instance.Id), []);
    }

    internal static CommandEffect Apply(WorldState world, CoreContentProjection content, CoreCommand.ApplyInstanceDesign command)
    {
        var instance = world.Instances.SingleOrDefault(i => i.Id == command.Instance);
        if (instance is null) return Reject("object_missing", "Instance");
        if (instance.Ownership != SettlementRelation.PlayerOwned) return Reject("not_owned", "Instance");
        if (ConstructionJobs.InstanceLocked(world, instance.Id)) return Reject("instance_locked", "Instance");
        var change = WorldInputs.NormalizeDesignChange(command.Change);
        if (Check(world, content, instance, change, true) is { } problem) return new CommandEffect.Rejected([problem]);
        var prepared = AddTarget(world, change.Target);
        prepared = prepared with
        {
            Instances = world.Instances.Select(i => i.Id != instance.Id ? i : i with
            {
                Design = change.Target.Design.Design,
                Parts = change.RetainedParts.Select(pair => new PartDurability(pair.After,
                    instance.Parts.Single(p => p.Id == pair.Before).Current)).OrderBy(p => p.Id.Value).ToImmutableArray()
            }).ToImmutableArray()
        };
        prepared = prepared with { DefinitionDependencies = WorldReferences.Dependencies(prepared, content) };
        return new CommandEffect.Accepted(content,
            instance.Design == change.Target.Design.Design ? [] : [new CoreEvent.InstanceDesignChanged(instance.Id, instance.Design, change.Target.Design.Design)], prepared);

        CommandEffect Reject(string code, string field) => new CommandEffect.Rejected([new(code, field, new WorldObjectId.Instance(command.Instance), [])]);
    }
}
