using System.Collections.Immutable;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class AuthoringDefinitionForm
{
    private Func<ImmutableArray<T>> Rows<T>(string title, string path, ImmutableArray<T> values, Func<T> create, Func<VBoxContainer, T, Func<T>> fields, bool plots = false)
    {
        var container = Section(title, path); var rows = new List<Func<T>>();
        void Add(T value)
        {
            var box = new VBoxContainer { FocusMode = FocusModeEnum.All }; container.AddChild(box);
            box.SetMeta("array_row", true);
            if (plots) _plots.Add(box);
            var read = fields(box, value); rows.Add(read);
            Button(box, "删除此行", () => { rows.Remove(read); if (plots) _plots.Remove(box); container.RemoveChild(box); box.QueueFree(); Edited?.Invoke(); });
        }
        foreach (var value in values) Add(value);
        Button(container, "添加" + title, () => { Add(create()); Edited?.Invoke(); });
        return () => rows.Select(read => read()).ToImmutableArray();
    }
    private Func<DefinitionDraft> Lair(LairTypeDraft v)
    {
        var id = Identity(v.LocalId); var display = Display(v.Display); var scene = Reference(this, "场景模型蓝图 ID（可空）", "/scene_model", v.SceneModel?.Value, () => AuthoringReferenceKind.SceneModel, optional: true, caption: "场景模型蓝图（可空）");
        var weight = IntegerField(this, "生成权重（可空）", "/generation_weight", v.GenerationWeight, optional: true);
        var members = Rows("成员", "/members", v.Members, () => new WeightedCreature(new(""), 1), (box, member) =>
        {
            var creature = Reference(box, "生物定义 ID", "creature", member.Creature.Value, () => AuthoringReferenceKind.Creature, caption: "生物定义"); var memberWeight = IntegerField(box, "成员权重", "weight", member.Weight);
            return () => new(new(creature.Text), Int(memberWeight));
        });
        var maximum = IntegerField(this, "最大成员数（可空）", "/maximum_members", v.MaximumMembers, optional: true, unit: "只"); var spawn = NumberField(this, "生成间隔（可空）", "/spawn_interval_seconds", v.SpawnIntervalSeconds, optional: true, unit: "秒"); var raid = NumberField(this, "袭击间隔（可空）", "/raid_interval_seconds", v.RaidIntervalSeconds, optional: true, unit: "秒");
        var roaming = IntegerField(this, "游荡半径（可空）", "/roaming_radius_hops", v.RoamingRadiusHops, optional: true, unit: "跳"); var radius = IntegerField(this, "袭击半径（可空）", "/raid_radius_hops", v.RaidRadiusHops, optional: true, unit: "跳"); var damage = NumberField(this, "破坏铁轨工作时间（可空）", "/rail_damage_work_seconds", v.RailDamageWorkSeconds, optional: true, unit: "秒"); var reward = LongField(this, "清除奖励（可空）", "/clearance_reward", v.ClearanceReward, optional: true, unit: "最小货币单位");
        return () => new DefinitionDraft.LairType(new(id.Text, display(), BlueprintReference(scene), OptionalInt(weight), members(), OptionalInt(maximum), OptionalNumber(spawn), OptionalNumber(raid), OptionalInt(roaming), OptionalInt(radius), OptionalNumber(damage), OptionalLong(reward)));
    }
    private Func<DefinitionDraft> Template(SettlementTemplateDraft v)
    {
        var id = Identity(v.LocalId); var display = Display(v.Display); var type = Reference(this, "聚居地类型 ID（可空）", "/settlement_type", v.Type?.Value, () => AuthoringReferenceKind.SettlementType, optional: true, caption: "聚居地类型（可空）"); var weight = IntegerField(this, "权重（可空）", "/weight", v.Weight, optional: true); var scene = Reference(this, "场景模型蓝图 ID（可空）", "/scene_model", v.SceneModel?.Value, () => AuthoringReferenceKind.SceneModel, optional: true, caption: "场景模型蓝图（可空）");
        var plots = Rows("地块", "/plots", v.Plots, () => new TemplatePlotDraft(_plots.Count + 1, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null), (box, plot) =>
        {
            var number = IntegerField(box, "地块编号", "id", plot.Number); var size = Choice<PlotSize>(box, "地块尺寸", "size", plot.Size);
            var x = IntegerField(box, "位置 X", "at/0", plot.At.X, unit: "格"); var y = IntegerField(box, "位置 Y", "at/1", plot.At.Y, unit: "格"); var z = IntegerField(box, "位置 Z", "at/2", plot.At.Z, unit: "格");
            var orientation = Choice<VoxelOrientation>(box, "朝向", "orientation", plot.Orientation); var prebuilt = Reference(box, "预建建筑蓝图 ID（可空）", "building", plot.PrebuiltBuilding?.Value, () => AuthoringReferenceKind.Building, optional: true, caption: "预建建筑蓝图（可空）");
            return () => new(Int(number), EnumValue<PlotSize>(size), new(Int(x), Int(y), Int(z)), EnumValue<VoxelOrientation>(orientation), BlueprintReference(prebuilt));
        }, plots: true);
        var founding = Rows("起始建筑", "/founding", v.FoundingBuildings, () => new FoundingBuildingDraft(1, new("")), (box, building) =>
        {
            var plot = IntegerField(box, "地块编号", "plot", building.PlotNumber); var blueprint = Reference(box, "建筑蓝图 ID", "building", building.Building.Value, () => AuthoringReferenceKind.Building, caption: "建筑蓝图");
            return () => new(Int(plot), new(blueprint.Text));
        });
        var inventory = Rows("初始库存", "/inventory", v.Inventory, () => new TemplateInventoryDraft.Voxel(new(""), 1), (box, item) =>
        {
            var kind = Choice(box, "库存类型", "kind", item is TemplateInventoryDraft.Blueprint ? "blueprint" : "voxel", [("voxel", "体素"), ("blueprint", "蓝图")]);
            var reference = Reference(box, "引用 ID", kind.Text, item switch
            {
                TemplateInventoryDraft.Voxel voxel => voxel.Id.Value,
                TemplateInventoryDraft.Blueprint blueprint => blueprint.Id.Value,
                _ => ""
            }, () => kind.Text == "blueprint" ? AuthoringReferenceKind.InventoryBlueprint : AuthoringReferenceKind.InventoryVoxel);
            kind.TextChanged += () => { reference.Path = kind.Text; reference.RefreshChoices?.Invoke(); };
            var quantity = LongField(box, "数量", "quantity", item switch
            {
                TemplateInventoryDraft.Voxel voxel => voxel.Quantity,
                TemplateInventoryDraft.Blueprint blueprint => blueprint.Quantity,
                _ => 1L
            }, unit: "件");
            return () => (TemplateInventoryDraft)(kind.Text switch
            {
                "voxel" => new TemplateInventoryDraft.Voxel(new(reference.Text), Long(quantity)),
                "blueprint" => (TemplateInventoryDraft)new TemplateInventoryDraft.Blueprint(new(reference.Text), Long(quantity)),
                _ => throw new FormatException("无效的引用类型。")
            });
        });
        var plans = Rows("生产计划", "/production_plans", v.ProductionPlans, () => new TemplateProductionPlan(1, CapabilityKind.Production, new ProcessRef.Production(new("")), true, 0), (box, plan) =>
        {
            var plot = IntegerField(box, "地块编号", "plot", plan.PlotNumber); var capability = Choice<CapabilityKind>(box, "能力", "capability", plan.Capability);
            var kind = Choice(box, "工序类型", "process_kind", plan.Process is ProcessRef.Production ? "Production" : "Recovery", [("Production", "生产工序"), ("Recovery", "回收工序")]);
            var process = Reference(box, "工序 ID", "process", plan.Process switch { ProcessRef.Production p => p.Definition.Value, ProcessRef.Recovery p => p.Definition.Value, _ => "" }, () => kind.Text == "Production" ? AuthoringReferenceKind.ProductionProcess : AuthoringReferenceKind.RecoveryProcess);
            kind.TextChanged += () => process.RefreshChoices?.Invoke();
            var enabled = Flag(box, "启用计划", "enabled", plan.Enabled); var priority = IntegerField(box, "优先级", "priority", plan.Priority);
            return () => new(Int(plot), EnumValue<CapabilityKind>(capability), kind.Text switch { "Production" => new ProcessRef.Production(new(process.Text)), "Recovery" => new ProcessRef.Recovery(new(process.Text)), _ => throw new FormatException("无效的工序类型。") }, enabled.ButtonPressed, Int(priority));
        });
        return () => new DefinitionDraft.SettlementTemplate(new(id.Text, display(), string.IsNullOrWhiteSpace(type.Text) ? null : new SettlementTypeId(type.Text), OptionalInt(weight), BlueprintReference(scene), plots(), founding(), inventory(), plans()));
    }
}
