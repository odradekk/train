using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class AuthoringDefinitionForm
{
    private sealed class FormField(Control control, Func<string> read, Action<string> write, string title, string path, Label error, Action edited)
    {
        internal Control Control { get; } = control;
        internal string Text { get => read(); set => write(value); }
        internal string TooltipText { get; } = title;
        internal string Path { get; set; } = path;
        internal string ErrorText { get => error.Text; set { error.Text = value; error.Visible = value.Length > 0; } }
        internal Func<string?>? Validate { get; set; }
        internal Action? RefreshChoices { get; set; }
        internal event Action? TextChanged;
        internal void Changed()
        {
            TextChanged?.Invoke();
            ErrorText = Validate?.Invoke() ?? "";
            edited();
        }
    }

    private FormField Register(VBoxContainer parent, Control control, string title, string path,
        Func<string> read, Action<string> write)
    {
        var error = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        parent.AddChild(error);
        var field = new FormField(control, read, write, title, path, error, () => Edited?.Invoke());
        _fields.Add(field);
        return field;
    }

    private FormField TextField(VBoxContainer parent, string title, string path, object? value,
        string? caption = null, bool readOnly = false)
    {
        parent.AddChild(new Label { Text = caption ?? title });
        var edit = new LineEdit { TooltipText = title, Text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "", Editable = !readOnly };
        parent.AddChild(edit);
        var field = Register(parent, edit, title, path, () => edit.Text, text => edit.Text = text);
        edit.TextChanged += _ => field.Changed();
        return field;
    }

    private FormField Identity(string id)
    {
        var field = TextField(this, "本地 ID", "/id", id, readOnly: true);
        AddChild(new Label { Text = "完整 ID：" + _package.Value + ":" + id });
        return field;
    }

    private FormField NumberField(VBoxContainer parent, string title, string path, double? value, bool optional = false, string? unit = null) =>
        Numeric(parent, title, path, value, text => double.TryParse(text, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number), optional, unit);
    private FormField IntegerField(VBoxContainer parent, string title, string path, int? value, bool optional = false, string? unit = null) =>
        Numeric(parent, title, path, value, text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _), optional, unit);
    private FormField LongField(VBoxContainer parent, string title, string path, long? value, bool optional = false, string? unit = null) =>
        Numeric(parent, title, path, value, text => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _), optional, unit);
    private FormField Numeric(VBoxContainer parent, string title, string path, object? value, Func<string, bool> valid, bool optional, string? unit)
    {
        var field = TextField(parent, title, path, value, unit is null ? title : title + "（" + unit + "）");
        if (optional) ((LineEdit)field.Control).PlaceholderText = "使用默认值";
        field.Validate = () => optional && string.IsNullOrWhiteSpace(field.Text) || valid(field.Text) ? null : $"{title}：{field.Text} 格式无效。";
        return field;
    }

    private FormField AssetField(VBoxContainer parent, string title, string path, string? value)
    {
        var field = TextField(parent, title, path, value);
        Button(parent, "选择文件…", () => AssetRequested?.Invoke(selected => { field.Text = selected; field.Changed(); }));
        return field;
    }

    private FormField Choice<T>(VBoxContainer parent, string title, string path, T? value, bool optional = false, string? caption = null) where T : struct, Enum =>
        Choice(parent, title, path, value?.ToString() ?? "", Enum.GetValues<T>().Select(v => (v.ToString(), Chinese(v.ToString()))).ToArray(), optional ? "使用默认值" : null, caption);

    private FormField Choice(VBoxContainer parent, string title, string path, string value,
        (string Value, string Caption)[] choices, string? empty = null, string? caption = null, Func<(string Value, string Caption)[]>? getChoices = null)
    {
        parent.AddChild(new Label { Text = caption ?? title });
        var select = new OptionButton { TooltipText = title, SizeFlagsHorizontal = SizeFlags.ExpandFill }; parent.AddChild(select);
        var entries = choices.ToList();
        var field = Register(parent, select, title, path, () => select.Selected >= 0 ? entries[select.Selected].Value : "", text => select.Select(entries.FindIndex(entry => entry.Value == text)));
        void Populate(string current)
        {
            entries = (getChoices?.Invoke() ?? choices).ToList();
            if (empty is not null) entries.Insert(0, ("", empty));
            if (entries.All(entry => entry.Value != current)) entries.Add((current, current.Length == 0 ? "请选择" : "不可用 · " + current));
            select.Clear(); foreach (var entry in entries) select.AddItem(entry.Caption);
            select.Select(entries.FindIndex(entry => entry.Value == current));
        }
        Populate(value);
        select.ItemSelected += _ => field.Changed();
        if (getChoices is not null) field.RefreshChoices = () => Populate(field.Text);
        return field;
    }

    private FormField Reference(VBoxContainer parent, string title, string path, string? value,
        Func<AuthoringReferenceKind> kind, bool optional = false, string? caption = null)
    {
        (string, string)[] Choices() => _choices.Where(c => c.Kind == kind()).Select(c => (ReferenceId(c.Reference), c.Display.Name + " · " + ReferenceId(c.Reference))).ToArray();
        var field = Choice(parent, title, path, value ?? "", [], optional ? "（不指定）" : null, caption, Choices);
        field.Validate = () => field.Text.Length == 0 ? optional ? null : "请选择引用。" : !Choices().Any(choice => choice.Item1 == field.Text) ? "引用不可用，请重新选择。" : null;
        var refresh = field.RefreshChoices!;
        field.RefreshChoices = () => { refresh(); field.ErrorText = field.Validate() ?? ""; };
        field.ErrorText = field.Validate() ?? "";
        return field;
    }

    private FormField MultipleChoice(VBoxContainer parent, string title, string path, string value,
        Func<(string Value, string Caption)[]> choices, bool references = false, string? caption = null)
    {
        parent.AddChild(new Label { Text = caption ?? title });
        var box = new VBoxContainer(); parent.AddChild(box);
        var selected = value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var field = Register(parent, box, title, path, () => string.Join(',', selected), _ => { });
        if (references) field.Validate = () => selected.Any(id => !choices().Any(entry => entry.Value == id)) ? "引用不可用，请重新选择。" : null;
        void Populate()
        {
            foreach (var child in box.GetChildren()) { box.RemoveChild(child); child.QueueFree(); }
            var entries = choices();
            foreach (var old in selected.Where(old => entries.All(entry => entry.Value != old))) entries = [.. entries, (old, "不可用 · " + old)];
            foreach (var (id, label) in entries)
            {
                var check = new CheckBox { Text = label, ButtonPressed = selected.Contains(id) }; box.AddChild(check);
                check.Toggled += on => { if (on) selected.Add(id); else selected.Remove(id); field.Changed(); };
            }
            field.ErrorText = field.Validate?.Invoke() ?? "";
        }
        Populate(); if (references) field.RefreshChoices = Populate;
        return field;
    }

    private FormField SurfaceMaterials(VBoxContainer parent, ImmutableArray<MaterialCategory> values)
    {
        parent.AddChild(new Label { Text = "表面材质（按模型表面顺序）" });
        var box = new VBoxContainer(); parent.AddChild(box); box.SetMeta("field_path", "/appearance/presentation/surface_materials");
        var rows = new List<FormField>();
        void Add(MaterialCategory material)
        {
            var row = new VBoxContainer(); row.SetMeta("array_row", true); box.AddChild(row);
            var field = Choice<MaterialCategory>(row, "表面材质", "", material); rows.Add(field);
            Button(row, "删除材质", () => { rows.Remove(field); box.RemoveChild(row); row.QueueFree(); Edited?.Invoke(); });
        }
        foreach (var material in values) Add(material);
        Button(parent, "添加表面材质", () => { Add(MaterialCategory.PaintedMetal); Edited?.Invoke(); });
        return Register(parent, box, "表面材质", "/appearance/presentation/surface_materials", () => string.Join(',', rows.Select(row => row.Text)), _ => { });
    }

    private static string ReferenceId(ContentReference value) => value switch
    {
        ContentReference.Blueprint b => b.Address.Id.Value,
        ContentReference.Core { Definition: CoreDefinitionReference.Voxel v } => v.Id.Value,
        ContentReference.Core { Definition: CoreDefinitionReference.Creature v } => v.Id.Value,
        ContentReference.Core { Definition: CoreDefinitionReference.SettlementType v } => v.Id.Value,
        ContentReference.Core { Definition: CoreDefinitionReference.Terrain v } => v.Id.Value,
        ContentReference.Core { Definition: CoreDefinitionReference.Process { Id: ProcessRef.Production v } } => v.Definition.Value,
        ContentReference.Core { Definition: CoreDefinitionReference.Process { Id: ProcessRef.Recovery v } } => v.Definition.Value,
        _ => throw new ArgumentException("不支持的引用候选。")
    };
    private static string Chinese(string value) => value switch
    {
        "None" => "无", "FourWay" => "水平四向", "All24" => "全部 24 向", "Small" => "小型", "Medium" => "中型", "Large" => "大型", "Giant" => "巨型", "ExtraLarge" => "特大型", "Titan" => "泰坦型",
        "SmallStation" => "小型站台", "LargeStation" => "大型站台", "Production" => "生产", "Extraction" => "开采", "Construction" => "施工",
        "PaintedMetal" => "涂漆金属", "BareMetal" => "裸露金属", "Concrete" => "混凝土", "StoneSoil" => "石土", "Glass" => "玻璃", "PolymerRubber" => "聚合物与橡胶", "Organic" => "有机物",
        "X" => "X 轴", "Y" => "Y 轴", "Z" => "Z 轴",
        _ => value.Replace("Px", "右").Replace("Nx", "左").Replace("Py", "上").Replace("Ny", "下").Replace("Pz", "前").Replace("Nz", "后")
    };
}
