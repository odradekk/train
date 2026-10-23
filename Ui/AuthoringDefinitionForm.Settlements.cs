using System.Collections.Immutable;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class AuthoringDefinitionForm
{
    private Func<ImmutableArray<T>> Rows<T>(string title, ImmutableArray<T> values, Func<T> create, Func<VBoxContainer, T, Func<T>> fields, bool plots = false)
    {
        var container = Section(title); var rows = new List<Func<T>>();
        void Add(T value)
        {
            var box = new VBoxContainer { FocusMode = FocusModeEnum.All }; container.AddChild(box);
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
        var id = Field(this, "本地 ID", v.LocalId); var display = Display(v.Display); var scene = Field(this, "场景模型蓝图 ID（可空）", v.SceneModel?.Value);
        var weight = Field(this, "生成权重（可空）", v.GenerationWeight);
        var members = Rows("成员", v.Members, () => new WeightedCreature(new(""), 1), (box, member) =>
        {
            var creature = Field(box, "生物定义 ID", member.Creature.Value); var memberWeight = Field(box, "成员权重", member.Weight);
            return () => new(new(creature.Text), Int(memberWeight));
        });
        var maximum = Field(this, "最大成员数（可空）", v.MaximumMembers); var spawn = Field(this, "生成间隔秒数（可空）", v.SpawnIntervalSeconds); var raid = Field(this, "袭击间隔秒数（可空）", v.RaidIntervalSeconds);
        var roaming = Field(this, "游荡半径跳数（可空）", v.RoamingRadiusHops); var radius = Field(this, "袭击半径跳数（可空）", v.RaidRadiusHops); var damage = Field(this, "破坏铁轨工作秒数（可空）", v.RailDamageWorkSeconds); var reward = Field(this, "清除奖励（可空）", v.ClearanceReward);
        return () => new DefinitionDraft.LairType(new(id.Text, display(), BlueprintReference(scene), OptionalInt(weight), members(), OptionalInt(maximum), OptionalNumber(spawn), OptionalNumber(raid), OptionalInt(roaming), OptionalInt(radius), OptionalNumber(damage), OptionalLong(reward)));
    }
    private Func<DefinitionDraft> Template(SettlementTemplateDraft v)
    {
        var id = Field(this, "本地 ID", v.LocalId); var display = Display(v.Display); var type = Field(this, "聚居地类型 ID（可空）", v.Type?.Value); var weight = Field(this, "权重（可空）", v.Weight); var scene = Field(this, "场景模型蓝图 ID（可空）", v.SceneModel?.Value);
        var plots = Rows("地块", v.Plots, () => new TemplatePlotDraft(_plots.Count + 1, PlotSize.Small, new(0, 0, 0), VoxelOrientation.PxPy, null), (box, plot) =>
        {
            var number = Field(box, "地块编号", plot.Number); var size = Field(box, "地块尺寸", plot.Size);
            var x = Field(box, "位置 X", plot.At.X); var y = Field(box, "位置 Y", plot.At.Y); var z = Field(box, "位置 Z", plot.At.Z);
            var orientation = Field(box, "朝向", plot.Orientation); var prebuilt = Field(box, "预建建筑蓝图 ID（可空）", plot.PrebuiltBuilding?.Value);
            return () => new(Int(number), EnumValue<PlotSize>(size), new(Int(x), Int(y), Int(z)), EnumValue<VoxelOrientation>(orientation), BlueprintReference(prebuilt));
        }, plots: true);
        var founding = Rows("起始建筑", v.FoundingBuildings, () => new FoundingBuildingDraft(1, new("")), (box, building) =>
        {
            var plot = Field(box, "地块编号", building.PlotNumber); var blueprint = Field(box, "建筑蓝图 ID", building.Building.Value);
            return () => new(Int(plot), new(blueprint.Text));
        });
        var inventory = Rows("初始库存", v.Inventory, () => new TemplateInventoryDraft.Voxel(new(""), 1), (box, item) =>
        {
            var kind = Field(box, "引用类型 voxel/blueprint", item is TemplateInventoryDraft.Blueprint ? "blueprint" : "voxel");
            var reference = Field(box, "引用 ID", item switch
            {
                TemplateInventoryDraft.Voxel voxel => voxel.Id.Value,
                TemplateInventoryDraft.Blueprint blueprint => blueprint.Id.Value,
                _ => ""
            });
            var quantity = Field(box, "数量", item switch
            {
                TemplateInventoryDraft.Voxel voxel => voxel.Quantity,
                TemplateInventoryDraft.Blueprint blueprint => blueprint.Quantity,
                _ => 1L
            });
            return () => (TemplateInventoryDraft)(kind.Text switch
            {
                "voxel" => new TemplateInventoryDraft.Voxel(new(reference.Text), Long(quantity)),
                "blueprint" => (TemplateInventoryDraft)new TemplateInventoryDraft.Blueprint(new(reference.Text), Long(quantity)),
                _ => throw new FormatException("无效的引用类型。")
            });
        });
        var plans = Rows("生产计划", v.ProductionPlans, () => new TemplateProductionPlan(1, CapabilityKind.Production, new ProcessRef.Production(new("")), true, 0), (box, plan) =>
        {
            var plot = Field(box, "地块编号", plan.PlotNumber); var capability = Field(box, "能力 Production/Extraction/Construction", plan.Capability);
            var kind = Field(box, "工序类型 Production/Recovery", plan.Process is ProcessRef.Production ? "Production" : "Recovery");
            var process = Field(box, "工序 ID", plan.Process switch { ProcessRef.Production p => p.Definition.Value, ProcessRef.Recovery p => p.Definition.Value, _ => "" });
            var enabled = Flag(box, "启用计划", plan.Enabled); var priority = Field(box, "优先级", plan.Priority);
            return () => new(Int(plot), EnumValue<CapabilityKind>(capability), kind.Text switch { "Production" => new ProcessRef.Production(new(process.Text)), "Recovery" => new ProcessRef.Recovery(new(process.Text)), _ => throw new FormatException("无效的工序类型。") }, enabled.ButtonPressed, Int(priority));
        });
        return () => new DefinitionDraft.SettlementTemplate(new(id.Text, display(), string.IsNullOrWhiteSpace(type.Text) ? null : new SettlementTypeId(type.Text), OptionalInt(weight), BlueprintReference(scene), plots(), founding(), inventory(), plans()));
    }
}
