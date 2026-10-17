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
        void Add(PartCapability value)
        {
            var box = new VBoxContainer(); container.AddChild(box);
            Func<PartCapability> read;
            switch (value)
            {
                case PartCapability.Propulsion p:
                    var traction = Field(box, "牵引力 kN", p.TractionKilonewtons); var speed = Field(box, "额定速度 m/s", p.RatedSpeedMetersPerSecond);
                    read = () => new PartCapability.Propulsion(Number(traction), Number(speed)); break;
                case PartCapability.Storage s:
                    var capacity = Field(box, "库存容量", s.Capacity); read = () => new PartCapability.Storage(Long(capacity)); break;
                case PartCapability.Production p:
                    var production = Field(box, "生产工作量/秒", p.WorkPerSecond); read = () => new PartCapability.Production(Number(production)); break;
                case PartCapability.Extraction e:
                    var extraction = Field(box, "开采工作量/秒", e.WorkPerSecond); read = () => new PartCapability.Extraction(Number(extraction)); break;
                case PartCapability.Construction c:
                    var work = Field(box, "施工工作量/秒", c.WorkPerSecond); var plot = Field(box, "最大建筑地块尺寸", c.MaximumBuildingPlotSize); var size = Field(box, "最大车辆尺寸（可空）", c.MaximumVehicleSize);
                    read = () => new PartCapability.Construction(Number(work), EnumValue<PlotSize>(plot), OptionalEnum<SizeClass>(size)); break;
                case PartCapability.Power p:
                    var energy = Field(box, "能量/秒", p.EnergyPerSecond); read = () => new PartCapability.Power(Number(energy)); break;
                case PartCapability.Weapon w:
                    var damage = Field(box, "武器伤害", w.Damage); var interval = Field(box, "武器间隔秒数", w.IntervalSeconds); read = () => new PartCapability.Weapon(Long(damage), Number(interval)); break;
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
        var id = Field(this, "本地 ID", v.LocalId); var display = Display(v.Display); var icon = Field(this, "图标资产路径", v.Icon?.Value);
        var simulation = Section("模拟层"); var enabled = Flag(simulation, "启用模拟层", v.Simulation != null);
        var price = Field(simulation, "基础价值（可空）", v.Simulation?.BaseValue); var mass = Field(simulation, "质量吨（可空）", v.Simulation?.MassTons);
        var durability = Field(simulation, "最大耐久（可空）", v.Simulation?.MaxDurability); var raw = Flag(simulation, "原材料", v.Simulation?.RawGood ?? false); var fuel = Field(simulation, "单位燃料能量（可空）", v.Simulation?.FuelEnergyPerUnit);
        var behaviors = Behaviors(simulation, v.Simulation?.Behaviors ?? []);
        var orientations = Field(this, "允许朝向 None/FourWay/All24", v.Orientations);
        var collision = Field(this, "碰撞 None/FullBox/LocalBox", v.Collision switch { VoxelCollision.None => "None", VoxelCollision.FullBox => "FullBox", _ => "LocalBox" });
        var local = v.Collision as VoxelCollision.LocalBox;
        var ox = Field(this, "碰撞偏移 X", local?.Offset.X ?? 0); var oy = Field(this, "碰撞偏移 Y", local?.Offset.Y ?? 0); var oz = Field(this, "碰撞偏移 Z", local?.Offset.Z ?? 0);
        var sx = Field(this, "碰撞尺寸 X", local?.Size.X ?? 1); var sy = Field(this, "碰撞尺寸 Y", local?.Size.Y ?? 1); var sz = Field(this, "碰撞尺寸 Z", local?.Size.Z ?? 1);
        var presentation = Section("表现层资产绑定"); var mode = Field(presentation, "表现 None/Cube/Mesh", v.Presentation switch { AuthoringVoxelPresentation.Cube => "Cube", AuthoringVoxelPresentation.Mesh => "Mesh", _ => "None" });
        var cube = v.Presentation as AuthoringVoxelPresentation.Cube; var mesh = v.Presentation as AuthoringVoxelPresentation.Mesh;
        var cubeFields = new VBoxContainer(); presentation.AddChild(cubeFields); var meshFields = new VBoxContainer(); presentation.AddChild(meshFields);
        var shapes = Field(cubeFields, "形状（逗号分隔）", string.Join(',', cube?.Shapes.Select(s => s.Name) ?? [])); var material = Field(cubeFields, "材质类别", cube?.Material ?? MaterialCategory.PaintedMetal); var span = Field(cubeFields, "贴图跨度", cube?.TileSpan ?? 1);
        var top = Field(cubeFields, "顶面贴图", cube?.Top.Value); var side = Field(cubeFields, "侧面贴图", cube?.Side.Value); var bottom = Field(cubeFields, "底面贴图", cube?.Bottom.Value); var front = Field(cubeFields, "正面贴图（可空）", cube?.Front?.Value);
        var model = Field(meshFields, "模型路径", mesh?.Model.Value); var surfaces = Field(meshFields, "各表面材质（逗号分隔）", string.Join(',', mesh?.SurfaceMaterials ?? []));
        void Visibility() { cubeFields.Visible = mode.Text == "Cube"; meshFields.Visible = mode.Text == "Mesh"; }
        mode.TextChanged += _ => Visibility(); Visibility();
        var wheelset = Flag(this, "轮组表现", v.AppearanceBehaviors.Wheelset); var light = Flag(this, "灯光表现", v.AppearanceBehaviors.Light != null);
        var color = Field(this, "灯光颜色", v.AppearanceBehaviors.Light?.Color.Value ?? "#ffffff"); var range = Field(this, "灯光范围格数", v.AppearanceBehaviors.Light?.RangeCells ?? 1);
        return () => new DefinitionDraft.Voxel(new(id.Text, display(), Asset(icon), enabled.ButtonPressed ? new(OptionalLong(price), OptionalNumber(mass), OptionalLong(durability), raw.ButtonPressed, OptionalNumber(fuel), behaviors()) : null,
            EnumValue<AllowedVoxelOrientations>(orientations), collision.Text switch { "None" => new VoxelCollision.None(), "FullBox" => new VoxelCollision.FullBox(), "LocalBox" => new VoxelCollision.LocalBox(new(Number(ox), Number(oy), Number(oz)), new(Number(sx), Number(sy), Number(sz))), _ => throw new FormatException("无效的碰撞类型。") },
            mode.Text switch { "None" => null, "Cube" => new AuthoringVoxelPresentation.Cube(Items(shapes).Select(s => new VoxelShape(s)).ToImmutableArray(), EnumValue<MaterialCategory>(material), Int(span), new(top.Text), new(side.Text), new(bottom.Text), Asset(front)), "Mesh" => new AuthoringVoxelPresentation.Mesh(new(model.Text), Items(surfaces).Select(s => EnumText<MaterialCategory>(s, surfaces.TooltipText)).ToImmutableArray()), _ => throw new FormatException("无效的表现类型。") }, new(wheelset.ButtonPressed, light.ButtonPressed ? new(new(color.Text), Number(range)) : null)));
    }
}
