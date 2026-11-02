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
    internal event Action<Action<string>>? AssetRequested;
    private PackageId _package = new("core");
    private ImmutableArray<AuthoringReferenceChoice> _choices = [];
    private readonly List<FormField> _fields = [];
    private readonly Dictionary<string, Label> _groupErrors = [];

    internal void Load(DefinitionDraft draft, PackageId package, ImmutableArray<AuthoringReferenceChoice> choices)
    {
        _package = package; _choices = choices; _fields.Clear(); _groupErrors.Clear();
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
    }

    internal DefinitionDraft Capture() => _capture?.Invoke() ?? throw new InvalidOperationException("尚未加载定义。");
    internal void SelectPlot(int index)
    {
        if (index >= 0 && index < _plots.Count) _plots[index].GrabFocus();
    }

    private CheckBox Flag(VBoxContainer parent, string title, string path, bool value, Action<bool>? changed = null)
    {
        var field = new CheckBox { Text = title, ButtonPressed = value };
        parent.AddChild(field);
        var binding = Register(parent, field, title, path, () => field.ButtonPressed.ToString(), _ => { });
        field.Toggled += on => { changed?.Invoke(on); binding.Changed(); };
        return field;
    }
    private VBoxContainer OptionalFields(VBoxContainer parent, string title, string path, bool value, out CheckBox enabled)
    {
        var fields = new VBoxContainer { Visible = value };
        enabled = Flag(parent, title, path, value, on => fields.Visible = on);
        parent.AddChild(fields);
        return fields;
    }
    private static void Button(VBoxContainer parent, string title, Action action)
    {
        parent.AddChild(UiStyle.Button("Action", title, action));
    }
    private VBoxContainer Section(string title, string path)
    {
        AddChild(new Label { Text = title });
        var box = new VBoxContainer(); AddChild(box);
        box.SetMeta("field_path", path);
        var error = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false }; AddChild(error); _groupErrors[path] = error;
        return box;
    }
    private Func<DisplayText> Display(DisplayText value)
    {
        var name = TextField(this, "名称", "/display/name", value.Name); var description = TextField(this, "说明", "/display/description", value.Description);
        return () => new(name.Text, description.Text);
    }
    private static int Int(FormField f) => Parse(f, s => int.Parse(s, CultureInfo.InvariantCulture));
    private static long Long(FormField f) => Parse(f, s => long.Parse(s, CultureInfo.InvariantCulture));
    private static double Number(FormField f) => Parse(f, s => double.TryParse(s, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : throw new FormatException());
    private static int? OptionalInt(FormField f) => string.IsNullOrWhiteSpace(f.Text) ? null : Int(f);
    private static long? OptionalLong(FormField f) => string.IsNullOrWhiteSpace(f.Text) ? null : Long(f);
    private static double? OptionalNumber(FormField f) => string.IsNullOrWhiteSpace(f.Text) ? null : Number(f);
    private static T Parse<T>(FormField field, Func<string, T> parse)
    {
        try { return parse(field.Text); }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        { throw new FormatException($"{field.TooltipText}：{field.Text} 格式无效。", ex); }
    }
    private static T EnumText<T>(string text, string title) where T : struct, Enum => Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value) ? value : throw new FormatException($"{title}：{text} 无效。");
    private static T EnumValue<T>(FormField f) where T : struct, Enum => EnumText<T>(f.Text, f.TooltipText);
    private static T? OptionalEnum<T>(FormField f) where T : struct, Enum => string.IsNullOrWhiteSpace(f.Text) ? null : EnumValue<T>(f);
    private static string[] Items(FormField f) => f.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static AuthoringAssetPath? Asset(FormField f) => string.IsNullOrWhiteSpace(f.Text) ? null : new(f.Text);
    private static BlueprintId? BlueprintReference(FormField f) => string.IsNullOrWhiteSpace(f.Text) ? null : new(f.Text);

    private Func<DefinitionDraft> SettlementType(SettlementTypeDraft v)
    {
        var id = Identity(v.LocalId); var display = Display(v.Display);
        var symbol = AssetField(this, "地图标记资产", "/display/map_symbol", v.MapSymbol?.Value); var major = Flag(this, "主要聚居地", "/display/major", v.Major);
        var weight = IntegerField(this, "权重（可空）", "/simulation/weight", v.Weight, optional: true); var terrain = MultipleChoice(this, "偏好地形", "/simulation/preferred_terrain", string.Join(',', v.PreferredTerrain.Select(x => x.Value)), () => _choices.Where(c => c.Kind == AuthoringReferenceKind.TerrainType).Select(c => (ReferenceId(c.Reference), c.Display.Name + " · " + ReferenceId(c.Reference))).ToArray(), references: true);
        var minimum = LongField(this, "最少人口（可空）", "/simulation/population_min", v.MinimumPopulation, optional: true, unit: "人"); var maximum = LongField(this, "最多人口（可空）", "/simulation/population_max", v.MaximumPopulation, optional: true, unit: "人");
        return () => new DefinitionDraft.SettlementType(new(id.Text, display(), Asset(symbol), major.ButtonPressed, OptionalInt(weight), Items(terrain).Select(x => new TerrainTypeId(x)).ToImmutableArray(), OptionalLong(minimum), OptionalLong(maximum)));
    }

    private Func<DefinitionDraft> Blueprint(DefinitionDraft.Blueprint v)
    {
        var id = Identity(v.LocalId);
        Func<DesignSpecification> specification;
        switch (v.Value.Specification)
        {
            case DesignSpecification.Assembly x:
                var rotation = BuildRotation(x.Rotation); specification = () => new DesignSpecification.Assembly(rotation()); break;
            case DesignSpecification.FunctionalComposite x:
                var behaviors = Behaviors(this, x.Behaviors); var compositeRotation = BuildRotation(x.Rotation);
                specification = () => new DesignSpecification.FunctionalComposite(behaviors(), compositeRotation()); break;
            case DesignSpecification.Creature x:
                var creature = OptionalFields(this, "生物行为", "/simulation/behaviors/creature", x.Behavior != null, out var creatureEnabled);
                var hp = LongField(creature, "生命", "/simulation/behaviors/creature/hp", x.Behavior?.HitPoints ?? 1); var speed = NumberField(creature, "移动速度", "/simulation/behaviors/creature/speed", x.Behavior?.SpeedMetersPerSecond ?? 1, unit: "米/秒");
                var retreat = NumberField(creature, "撤退阈值", "/simulation/behaviors/creature/retreat_threshold", x.Behavior?.RetreatThreshold ?? 0); var raid = NumberField(creature, "袭击持续时间", "/simulation/behaviors/creature/raid_duration", x.Behavior?.RaidDurationSeconds ?? 1, unit: "秒");
                var weapon = OptionalFields(this, "生物武器", "/simulation/behaviors/weapon", x.Weapon != null, out var weaponEnabled); var damage = LongField(weapon, "伤害", "/simulation/behaviors/weapon/damage", x.Weapon?.Damage ?? 1); var interval = NumberField(weapon, "攻击间隔", "/simulation/behaviors/weapon/interval", x.Weapon?.IntervalSeconds ?? 1, unit: "秒");
                specification = () => new DesignSpecification.Creature(creatureEnabled.ButtonPressed ? new(Long(hp), Number(speed), Number(retreat), Number(raid)) : null, weaponEnabled.ButtonPressed ? new(Long(damage), Number(interval)) : null); break;
            default: specification = () => v.Value.Specification; break;
        }
        Button(this, "编辑体素网格", () => GridRequested?.Invoke());
        return () => new DefinitionDraft.Blueprint(id.Text, v.Value with { Specification = specification() });
    }
    private Func<RotationAppearance?> BuildRotation(RotationAppearance? v)
    {
        var rotation = OptionalFields(this, "转动表现", "/appearance/behaviors/rotation", v != null, out var enabled); var axis = Choice<RotationAxis>(rotation, "转轴 X/Y/Z", "/appearance/behaviors/rotation/axis", v?.Axis ?? RotationAxis.Y, caption: "转轴");
        var x = IntegerField(rotation, "支点 X", "/appearance/behaviors/rotation/pivot/0", v?.Pivot.X ?? 0, unit: "格"); var y = IntegerField(rotation, "支点 Y", "/appearance/behaviors/rotation/pivot/1", v?.Pivot.Y ?? 0, unit: "格"); var z = IntegerField(rotation, "支点 Z", "/appearance/behaviors/rotation/pivot/2", v?.Pivot.Z ?? 0, unit: "格");
        var min = NumberField(rotation, "最小角度", "/appearance/behaviors/rotation/range/0", v?.MinimumDegrees ?? 0, unit: "度"); var max = NumberField(rotation, "最大角度", "/appearance/behaviors/rotation/range/1", v?.MaximumDegrees ?? 0, unit: "度"); var speed = NumberField(rotation, "角速度", "/appearance/behaviors/rotation/speed", v?.SpeedDegreesPerSecond ?? 0, unit: "度/秒");
        return () => enabled.ButtonPressed ? new(EnumValue<RotationAxis>(axis), new(Int(x), Int(y), Int(z)), Number(min), Number(max), Number(speed)) : null;
    }
}
