using System.Collections.Immutable;
using Train.Content.Designs;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed partial class WorldLoader
{
    internal static LoadedSettlementTemplate? ResolveTemplate(LoadPackage package, ParsedSettlementTemplate source,
        References references, bool typePresent, Dictionary<string, ParsedProcess> processes)
    {
        var subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Template(source.Id));
        references.Need(package, source.Path, subject, source.Type.Value, typePresent, "/settlement_type");
        var scene = references.Scene(package, source.Path, subject, source.SceneModel, "/scene_model");
        if (scene is not null) ValidatePlotSpace(package, source, scene.Scene, subject);
        var plots = ImmutableArray.CreateBuilder<K.TemplatePlotProjection>();
        var prebuilt = new Dictionary<int, C.DesignEvaluation.Complete>();
        foreach (var plot in source.Plots)
        {
            if (plot.Building is not { } address) { plots.Add(new(plot.Number, plot.Size, null)); continue; }
            var design = references.Design(package, source.Path, subject, address, "/plots", vehicle: false);
            if (design?.Core?.Design.Kind is K.DesignKind.Building building && !PlotRules.Fits(building.PlotSize, plot.Size))
                package.Reject("invalid_field", source.Path, "/plots", "建筑类型或尺寸不能放入地块。", subject);
            if (design is not null)
            {
                plots.Add(new(plot.Number, plot.Size, design.Design));
                prebuilt.Add(plot.Number, design);
            }
        }
        var founding = ImmutableArray.CreateBuilder<K.FoundingBuildingProjection>();
        foreach (var building in source.Founding)
        {
            var plot = source.Plots.SingleOrDefault(p => p.Number == building.PlotNumber);
            if (plot is null)
                package.Reject("invalid_field", source.Path, building.SourceField + "/plot", "起始建筑的地块不存在。", subject);
            var design = references.Design(package, source.Path, subject, building.Building, building.SourceField + "/building", vehicle: false);
            if (plot is not null && design?.Core?.Design.Kind is K.DesignKind.Building kind && !PlotRules.Fits(kind.PlotSize, plot.Size))
                package.Reject("invalid_field", source.Path, building.SourceField + "/building", "起始建筑类型或尺寸不能放入地块。", subject);
            if (design is not null) founding.Add(new(building.PlotNumber, design.Design));
        }
        var inventory = references.Inventory(package, source.Path, subject, source.Inventory, "/inventory");
        var plans = ImmutableArray.CreateBuilder<K.TemplateProductionPlan>();
        foreach (var plan in source.ProductionPlans)
        {
            references.Need(package, source.Path, subject, plan.Process, processes.ContainsKey(plan.Process), plan.SourceField + "/process");
            if (!prebuilt.TryGetValue(plan.PlotNumber, out var design))
            {
                package.Reject("invalid_field", source.Path, plan.SourceField + "/plot", "初始计划必须引用有预建建筑的地块。", subject);
                continue;
            }
            if (!processes.TryGetValue(plan.Process, out var process)) continue;
            var capabilities = design.Core!.Design.Parts.SelectMany(p => p.Capabilities);
            if (plan.Capability == K.CapabilityKind.Production
                    ? !capabilities.OfType<K.PartCapability.Production>().Any()
                    : !capabilities.OfType<K.PartCapability.Extraction>().Any())
                package.Reject("invalid_field", source.Path, plan.SourceField + "/capability", "预建建筑不具备计划指定的能力。", subject);
            if ((plan.Capability == K.CapabilityKind.Extraction) != (process.Projection.Details is K.ProcessDetails.Extraction)
                || !((K.DesignKind.Building)design.Core.Design.Kind).FacilityTags.Contains(process.Projection.RequiredFacilityTag))
                package.Reject("invalid_field", source.Path, plan.SourceField + "/process", "流程种类或设施标签与预建建筑不兼容。", subject);
            plans.Add(new(plan.PlotNumber, plan.Capability, process.Reference, plan.Enabled, plan.Priority));
        }
        return Active(package) && scene is not null
            ? new(source, new(source.Id, source.Type, source.Weight, plots.ToImmutable(), founding.ToImmutable(), inventory, plans.ToImmutable(),
                PlotRules.PlatformLengthMeters(source.Plots.Select(p => p.Size))), scene) : null;
    }

    private static void ValidatePlotSpace(LoadPackage package, ParsedSettlementTemplate source,
        C.DesignSceneProjection scene, C.ContentReference subject)
    {
        (long MinX, long MaxX, long MinZ, long MaxZ)? range = scene.OccupiedBounds is { } occupied
            ? (occupied.Minimum.X, occupied.Maximum.X, occupied.Minimum.Z, occupied.Maximum.Z) : null;
        if (scene.RootFlatten is { } flatten)
            range = range is { } bounds
                ? (Math.Min(bounds.MinX, flatten.From.X), Math.Max(bounds.MaxX, flatten.To.X),
                    Math.Min(bounds.MinZ, flatten.From.Z), Math.Max(bounds.MaxZ, flatten.To.Z))
                : (flatten.From.X, flatten.To.X, flatten.From.Z, flatten.To.Z);
        var plots = source.Layout.Select(PlotRules.Footprint).ToArray();
        for (var index = 0; index < plots.Length; index++)
        {
            var plot = plots[index];
            var field = source.Plots[index].SourceField + "/at";
            if (range is not { } bounds || plot.MinX < bounds.MinX || plot.MaxX > bounds.MaxX
                || plot.MinZ < bounds.MinZ || plot.MaxZ > bounds.MaxZ)
                package.Reject("invalid_field", source.Path, field, "地块占地超出场景模型范围。", subject);
            for (var previous = 0; previous < index; previous++)
            {
                var other = plots[previous];
                if (plot.MinX <= other.MaxX && plot.MaxX >= other.MinX && plot.MinZ <= other.MaxZ && plot.MaxZ >= other.MinZ)
                    package.Reject("invalid_field", source.Path, field, "地块占地与其他地块重叠。", subject);
            }
        }
    }

    internal static LoadedLair? ResolveLair(LoadPackage package, ParsedLair source, References references)
    {
        var subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Lair(source.Core.Id));
        var scene = references.Scene(package, source.Path, subject, source.SceneModel, "/scene_model");
        foreach (var member in source.Members)
            references.Creature(package, source.Path, subject, member.Creature, member.Field);
        return Active(package) && scene is not null ? new(source.Core, source.Display, scene) : null;
    }

}
