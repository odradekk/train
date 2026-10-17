using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class AuthoringDefinitionForm : VBoxContainer
{
    private Func<DefinitionDraft>? _capture;
    private readonly List<Control> _plots = [];
    internal event Action? Edited;
    internal event Action? GridRequested;
    internal event Action? PreviewRequested;

    internal void Load(DefinitionDraft draft)
    {
        foreach (var child in GetChildren()) { RemoveChild(child); child.QueueFree(); }
        _plots.Clear();
        _capture = draft switch
        {
            DefinitionDraft.Voxel v => Voxel(v.Value),
            DefinitionDraft.Blueprint b => Blueprint(b),
            DefinitionDraft.SettlementType s => SettlementType(s.Value),
            DefinitionDraft.LairType l => Lair(l.Value),
            DefinitionDraft.SettlementTemplate t => Template(t.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(draft))
        };
        Button(this, "更新预览", () => PreviewRequested?.Invoke());
    }

    internal DefinitionDraft Capture() => _capture?.Invoke() ?? throw new InvalidOperationException("尚未加载定义。");
    internal void SelectPlot(int index)
    {
        if (index >= 0 && index < _plots.Count) _plots[index].GrabFocus();
    }

    private LineEdit Field(VBoxContainer parent, string title, object? value)
    {
        parent.AddChild(new Label { Text = title });
        var field = new LineEdit { TooltipText = title, Text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "" };
        parent.AddChild(field);
        field.TextChanged += _ => Edited?.Invoke();
        return field;
    }
    private CheckBox Flag(VBoxContainer parent, string title, bool value)
    {
        var field = new CheckBox { Text = title, ButtonPressed = value };
        parent.AddChild(field);
        field.Toggled += _ => Edited?.Invoke();
        return field;
    }
    private static void Button(VBoxContainer parent, string title, Action action)
    {
        var button = new Button { Text = title };
        parent.AddChild(button);
        button.Pressed += action;
    }
    private VBoxContainer Section(string title)
    {
        AddChild(new Label { Text = title });
        var box = new VBoxContainer(); AddChild(box); return box;
    }
    private Func<DisplayText> Display(DisplayText value)
    {
        var name = Field(this, "名称", value.Name); var description = Field(this, "说明", value.Description);
        return () => new(name.Text, description.Text);
    }
    private static int Int(LineEdit f) => Parse(f, s => int.Parse(s, CultureInfo.InvariantCulture));
    private static long Long(LineEdit f) => Parse(f, s => long.Parse(s, CultureInfo.InvariantCulture));
    private static double Number(LineEdit f) => Parse(f, s => double.Parse(s, CultureInfo.InvariantCulture));
    private static int? OptionalInt(LineEdit f) => string.IsNullOrWhiteSpace(f.Text) ? null : Int(f);
    private static long? OptionalLong(LineEdit f) => string.IsNullOrWhiteSpace(f.Text) ? null : Long(f);
    private static double? OptionalNumber(LineEdit f) => string.IsNullOrWhiteSpace(f.Text) ? null : Number(f);
    private static T Parse<T>(LineEdit field, Func<string, T> parse)
    {
        try { return parse(field.Text); }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        { throw new FormatException($"{field.TooltipText}：{field.Text} 格式无效。", ex); }
    }
    private static T EnumText<T>(string text, string title) where T : struct, Enum => Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value) ? value : throw new FormatException($"{title}：{text} 无效。");
    private static T EnumValue<T>(LineEdit f) where T : struct, Enum => EnumText<T>(f.Text, f.TooltipText);
    private static T? OptionalEnum<T>(LineEdit f) where T : struct, Enum => string.IsNullOrWhiteSpace(f.Text) ? null : EnumValue<T>(f);
    private static string[] Items(LineEdit f) => f.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static AuthoringAssetPath? Asset(LineEdit f) => string.IsNullOrWhiteSpace(f.Text) ? null : new(f.Text);
    private static BlueprintId? BlueprintReference(LineEdit f) => string.IsNullOrWhiteSpace(f.Text) ? null : new(f.Text);

    private Func<DefinitionDraft> SettlementType(SettlementTypeDraft v)
    {
        var id = Field(this, "本地 ID", v.LocalId); var display = Display(v.Display);
        var symbol = Field(this, "地图标记资产", v.MapSymbol?.Value); var major = Flag(this, "主要聚居地", v.Major);
        var weight = Field(this, "权重（可空）", v.Weight); var terrain = Field(this, "偏好地形 ID（逗号分隔）", string.Join(',', v.PreferredTerrain.Select(x => x.Value)));
        var minimum = Field(this, "最少人口（可空）", v.MinimumPopulation); var maximum = Field(this, "最多人口（可空）", v.MaximumPopulation);
        return () => new DefinitionDraft.SettlementType(new(id.Text, display(), Asset(symbol), major.ButtonPressed, OptionalInt(weight), Items(terrain).Select(x => new TerrainTypeId(x)).ToImmutableArray(), OptionalLong(minimum), OptionalLong(maximum)));
    }

    private Func<DefinitionDraft> Blueprint(DefinitionDraft.Blueprint v)
    {
        var id = Field(this, "本地 ID", v.LocalId); var display = Display(v.Value.Display);
        Func<DesignSpecification> specification;
        switch (v.Value.Specification)
        {
            case DesignSpecification.Vehicle x:
                var size = Field(this, "车辆尺寸（Small/Medium/Large/ExtraLarge/Titan，可空）", x.Size);
                specification = () => new DesignSpecification.Vehicle(OptionalEnum<SizeClass>(size)); break;
            case DesignSpecification.Building x:
                var plot = Field(this, "地块尺寸（可空）", x.PlotSize); var kind = Field(this, "建筑类型（可空）", x.Kind);
                var tags = Field(this, "设施标签（逗号分隔）", string.Join(',', x.FacilityTags));
                specification = () => new DesignSpecification.Building(OptionalEnum<PlotSize>(plot), OptionalEnum<BuildingKind>(kind), Items(tags).Select(s => EnumText<FacilityTag>(s, tags.TooltipText)).ToImmutableArray()); break;
            case DesignSpecification.Assembly x:
                var rotation = BuildRotation(x.Rotation); specification = () => new DesignSpecification.Assembly(rotation()); break;
            case DesignSpecification.FunctionalComposite x:
                var behaviors = Behaviors(this, x.Behaviors); var compositeRotation = BuildRotation(x.Rotation);
                specification = () => new DesignSpecification.FunctionalComposite(behaviors(), compositeRotation()); break;
            case DesignSpecification.SceneModel x:
                var enabled = Flag(this, "平整区域", x.Flatten != null);
                var fromX = Field(this, "平整起点 X", x.Flatten?.From.X ?? 0); var fromZ = Field(this, "平整起点 Z", x.Flatten?.From.Z ?? 0);
                var toX = Field(this, "平整终点 X", x.Flatten?.To.X ?? 0); var toZ = Field(this, "平整终点 Z", x.Flatten?.To.Z ?? 0); var height = Field(this, "平整高度", x.Flatten?.Height ?? 0);
                specification = () => new DesignSpecification.SceneModel(enabled.ButtonPressed ? new(new(Int(fromX), Int(fromZ)), new(Int(toX), Int(toZ)), Int(height)) : null); break;
            case DesignSpecification.Creature x:
                var creatureEnabled = Flag(this, "生物行为", x.Behavior != null);
                var hp = Field(this, "生命", x.Behavior?.HitPoints ?? 1); var speed = Field(this, "移动速度", x.Behavior?.SpeedMetersPerSecond ?? 1);
                var retreat = Field(this, "撤退阈值", x.Behavior?.RetreatThreshold ?? 0); var raid = Field(this, "袭击持续秒数", x.Behavior?.RaidDurationSeconds ?? 1);
                var weaponEnabled = Flag(this, "生物武器", x.Weapon != null); var damage = Field(this, "伤害", x.Weapon?.Damage ?? 1); var interval = Field(this, "攻击间隔秒数", x.Weapon?.IntervalSeconds ?? 1);
                specification = () => new DesignSpecification.Creature(creatureEnabled.ButtonPressed ? new(Long(hp), Number(speed), Number(retreat), Number(raid)) : null, weaponEnabled.ButtonPressed ? new(Long(damage), Number(interval)) : null); break;
            default: throw new ArgumentOutOfRangeException(nameof(v));
        }
        Button(this, "编辑体素网格", () => GridRequested?.Invoke());
        return () => new DefinitionDraft.Blueprint(id.Text, v.Value with { Display = display(), Specification = specification() });
    }
    private Func<RotationAppearance?> BuildRotation(RotationAppearance? v)
    {
        var enabled = Flag(this, "转动表现", v != null); var axis = Field(this, "转轴 X/Y/Z", v?.Axis ?? RotationAxis.Y);
        var x = Field(this, "支点 X", v?.Pivot.X ?? 0); var y = Field(this, "支点 Y", v?.Pivot.Y ?? 0); var z = Field(this, "支点 Z", v?.Pivot.Z ?? 0);
        var min = Field(this, "最小角度", v?.MinimumDegrees ?? 0); var max = Field(this, "最大角度", v?.MaximumDegrees ?? 0); var speed = Field(this, "角速度", v?.SpeedDegreesPerSecond ?? 0);
        return () => enabled.ButtonPressed ? new(EnumValue<RotationAxis>(axis), new(Int(x), Int(y), Int(z)), Number(min), Number(max), Number(speed)) : null;
    }
}
