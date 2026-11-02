using System.Collections.Immutable;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class AuthoringDefinitionForm
{
    private Func<ImmutableArray<PartCapability>> Behaviors(VBoxContainer parent, ImmutableArray<PartCapability> initial)
    {
        var rows = new List<Func<PartCapability>>();
        var container = new VBoxContainer(); parent.AddChild(container);
        var errors = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false }; parent.AddChild(errors);
        _groupErrors["/simulation/behaviors"] = errors;
        void Add(PartCapability value)
        {
            var box = new VBoxContainer(); container.AddChild(box);
            Func<PartCapability> read;
            switch (value)
            {
                case PartCapability.Propulsion p:
                    var traction = NumberField(box, "牵引力", "/simulation/behaviors/propulsion/traction", p.TractionKilonewtons, unit: "千牛"); var speed = NumberField(box, "额定速度", "/simulation/behaviors/propulsion/rated_speed", p.RatedSpeedMetersPerSecond, unit: "米/秒");
                    read = () => new PartCapability.Propulsion(Number(traction), Number(speed)); break;
                case PartCapability.Storage s:
                    var capacity = LongField(box, "库存容量", "/simulation/behaviors/storage/capacity", s.Capacity, unit: "件"); read = () => new PartCapability.Storage(Long(capacity)); break;
                case PartCapability.Production p:
                    var production = NumberField(box, "生产工作量/秒", "/simulation/behaviors/production/work_rate", p.WorkPerSecond); read = () => new PartCapability.Production(Number(production)); break;
                case PartCapability.Extraction e:
                    var extraction = NumberField(box, "开采工作量/秒", "/simulation/behaviors/extraction/work_rate", e.WorkPerSecond); read = () => new PartCapability.Extraction(Number(extraction)); break;
                case PartCapability.Construction c:
                    var work = NumberField(box, "施工工作量/秒", "/simulation/behaviors/construction/work_rate", c.WorkPerSecond); var plot = Choice<PlotSize>(box, "最大建筑地块尺寸", "/simulation/behaviors/construction/max_plot_size", c.MaximumBuildingPlotSize); var size = Choice<SizeClass>(box, "最大车辆尺寸（可空）", "/simulation/behaviors/construction/max_size_class", c.MaximumVehicleSize, optional: true);
                    read = () => new PartCapability.Construction(Number(work), EnumValue<PlotSize>(plot), OptionalEnum<SizeClass>(size)); break;
                case PartCapability.Power p:
                    var energy = NumberField(box, "能量/秒", "/simulation/behaviors/power/output", p.EnergyPerSecond); read = () => new PartCapability.Power(Number(energy)); break;
                case PartCapability.Weapon w:
                    var damage = LongField(box, "武器伤害", "/simulation/behaviors/weapon/damage", w.Damage); var interval = NumberField(box, "武器间隔", "/simulation/behaviors/weapon/interval", w.IntervalSeconds, unit: "秒"); read = () => new PartCapability.Weapon(Long(damage), Number(interval)); break;
                case PartCapability.Control:
                    box.AddChild(new Label { Text = "控制行为" }); read = () => value; break;
                case PartCapability.Platform:
                    box.AddChild(new Label { Text = "站台行为" }); read = () => value; break;
                default: throw new ArgumentOutOfRangeException(nameof(value));
            }
            rows.Add(read);
            Button(box, "删除行为", () => { rows.Remove(read); container.RemoveChild(box); box.QueueFree(); Edited?.Invoke(); });
        }
        foreach (var value in initial) Add(value);
        void AddButton(string name, Func<PartCapability> create) => Button(parent, "添加" + name, () => { Add(create()); Edited?.Invoke(); });
        AddButton("动力", () => new PartCapability.Propulsion(1, 1)); AddButton("控制", () => new PartCapability.Control());
        AddButton("库存", () => new PartCapability.Storage(1)); AddButton("生产", () => new PartCapability.Production(1));
        AddButton("开采", () => new PartCapability.Extraction(1)); AddButton("施工", () => new PartCapability.Construction(1, PlotSize.Small, null));
        AddButton("供能", () => new PartCapability.Power(1)); AddButton("武器", () => new PartCapability.Weapon(1, 1)); AddButton("站台", () => new PartCapability.Platform());
        return () => rows.Select(read => read()).ToImmutableArray();
    }
    private Func<DefinitionDraft> Voxel(VoxelDefinitionDraft v)
    {
        var id = Identity(v.LocalId); var display = Display(v.Display); var icon = AssetField(this, "图标资产路径", "/display/icon", v.Icon?.Value);
        var section = Section("模拟层", "/simulation"); var simulation = OptionalFields(section, "启用模拟层", "/simulation", v.Simulation != null, out var enabled);
        var price = LongField(simulation, "基础价值（最小货币单位，可空）", "/simulation/value", v.Simulation?.BaseValue, optional: true); var mass = NumberField(simulation, "质量（可空）", "/simulation/mass", v.Simulation?.MassTons, optional: true, unit: "吨");
        var durability = LongField(simulation, "最大耐久（可空）", "/simulation/max_durability", v.Simulation?.MaxDurability, optional: true); var raw = Flag(simulation, "原材料", "/simulation/tags", v.Simulation?.RawGood ?? false); var fuel = NumberField(simulation, "单位燃料能量（可空）", "/simulation/fuel_energy", v.Simulation?.FuelEnergyPerUnit, optional: true);
        var behaviors = Behaviors(simulation, v.Simulation?.Behaviors ?? []);
        var orientations = Choice<AllowedVoxelOrientations>(this, "允许朝向", "/appearance/allowed_orientations", v.Orientations);
        var collision = Choice(this, "碰撞类型", "/appearance/collision/kind", v.Collision switch { VoxelCollision.None => "None", VoxelCollision.FullBox => "FullBox", _ => "LocalBox" }, [("None", "无碰撞"), ("FullBox", "完整方块"), ("LocalBox", "局部方块")]);
        var local = v.Collision as VoxelCollision.LocalBox;
        var collisionFields = new VBoxContainer(); AddChild(collisionFields);
        void CollisionVisibility() => collisionFields.Visible = collision.Text == "LocalBox";
        collision.TextChanged += CollisionVisibility; CollisionVisibility();
        var ox = NumberField(collisionFields, "碰撞偏移 X", "/appearance/collision/offset/x", local?.Offset.X ?? 0, unit: "格"); var oy = NumberField(collisionFields, "碰撞偏移 Y", "/appearance/collision/offset/y", local?.Offset.Y ?? 0, unit: "格"); var oz = NumberField(collisionFields, "碰撞偏移 Z", "/appearance/collision/offset/z", local?.Offset.Z ?? 0, unit: "格");
        var sx = NumberField(collisionFields, "碰撞尺寸 X", "/appearance/collision/size/x", local?.Size.X ?? 1, unit: "格"); var sy = NumberField(collisionFields, "碰撞尺寸 Y", "/appearance/collision/size/y", local?.Size.Y ?? 1, unit: "格"); var sz = NumberField(collisionFields, "碰撞尺寸 Z", "/appearance/collision/size/z", local?.Size.Z ?? 1, unit: "格");
        var presentation = Section("表现层资产绑定", "/appearance/presentation"); var mode = Choice(presentation, "表现类型", "/appearance/presentation/kind", v.Presentation switch { AuthoringVoxelPresentation.Cube => "Cube", AuthoringVoxelPresentation.Mesh => "Mesh", _ => "None" }, [("None", "无表现"), ("Cube", "方块贴图"), ("Mesh", "模型")]);
        var cube = v.Presentation as AuthoringVoxelPresentation.Cube; var mesh = v.Presentation as AuthoringVoxelPresentation.Mesh;
        var cubeFields = new VBoxContainer(); presentation.AddChild(cubeFields); var meshFields = new VBoxContainer(); presentation.AddChild(meshFields);
        var shapes = MultipleChoice(cubeFields, "允许形状", "/appearance/presentation/shapes", string.Join(',', cube?.Shapes.Select(s => s.Name) ?? []), () => [("cube", "方块"), ("half", "半高方块"), ("plate", "薄板"), ("wedge", "斜坡"), ("corner-wedge", "角斜坡"), ("beam", "梁"), ("cylinder", "圆柱")]); var material = Choice<MaterialCategory>(cubeFields, "材质类别", "/appearance/presentation/material", cube?.Material ?? MaterialCategory.PaintedMetal); var span = IntegerField(cubeFields, "贴图跨度", "/appearance/presentation/tile_span", cube?.TileSpan ?? 1, unit: "格");
        var top = AssetField(cubeFields, "顶面贴图", "/appearance/presentation/faces/top", cube?.Top.Value); var side = AssetField(cubeFields, "侧面贴图", "/appearance/presentation/faces/side", cube?.Side.Value); var bottom = AssetField(cubeFields, "底面贴图", "/appearance/presentation/faces/bottom", cube?.Bottom.Value); var front = AssetField(cubeFields, "正面贴图（可空）", "/appearance/presentation/faces/front", cube?.Front?.Value);
        var model = AssetField(meshFields, "模型路径", "/appearance/presentation/model", mesh?.Model.Value); var surfaces = SurfaceMaterials(meshFields, mesh?.SurfaceMaterials ?? []);
        void Visibility() { cubeFields.Visible = mode.Text == "Cube"; meshFields.Visible = mode.Text == "Mesh"; }
        mode.TextChanged += Visibility; Visibility();
        var wheelset = Flag(this, "轮组表现", "/appearance/behaviors/wheelset", v.AppearanceBehaviors.Wheelset); var lightFields = OptionalFields(this, "灯光表现", "/appearance/behaviors/light", v.AppearanceBehaviors.Light != null, out var light);
        var color = TextField(lightFields, "灯光颜色", "/appearance/behaviors/light/color", v.AppearanceBehaviors.Light?.Color.Value ?? "#ffffff"); var range = NumberField(lightFields, "灯光范围", "/appearance/behaviors/light/range", v.AppearanceBehaviors.Light?.RangeCells ?? 1, unit: "格");
        return () => new DefinitionDraft.Voxel(new(id.Text, display(), Asset(icon), enabled.ButtonPressed ? new(OptionalLong(price), OptionalNumber(mass), OptionalLong(durability), raw.ButtonPressed, OptionalNumber(fuel), behaviors()) : null,
            EnumValue<AllowedVoxelOrientations>(orientations), collision.Text switch { "None" => new VoxelCollision.None(), "FullBox" => new VoxelCollision.FullBox(), "LocalBox" => new VoxelCollision.LocalBox(new(Number(ox), Number(oy), Number(oz)), new(Number(sx), Number(sy), Number(sz))), _ => throw new FormatException("无效的碰撞类型。") },
            mode.Text switch { "None" => null, "Cube" => new AuthoringVoxelPresentation.Cube(Items(shapes).Select(s => new VoxelShape(s)).ToImmutableArray(), EnumValue<MaterialCategory>(material), Int(span), new(top.Text), new(side.Text), new(bottom.Text), Asset(front)), "Mesh" => new AuthoringVoxelPresentation.Mesh(new(model.Text), Items(surfaces).Select(s => EnumText<MaterialCategory>(s, surfaces.TooltipText)).ToImmutableArray()), _ => throw new FormatException("无效的表现类型。") }, new(wheelset.ButtonPressed, light.ButtonPressed ? new(new(color.Text), Number(range)) : null)));
    }
}
