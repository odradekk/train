using Godot;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class BlueprintLibraryPanel : Control
{
    private EditorService _service = null!;
    private VBoxContainer _body = null!;
    private IContentView? _observedContent;
    internal event Action? Opened;
    internal void Initialize(EditorService service) => _service = service;
    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Ignore;
        var panel = UiStyle.Panel(this, "BlueprintLibrary", "蓝图库与草稿", new(60, 80), 650, Hide, out var body);
        body.AddChild(UiStyle.Button("NewDesign", "新建设计…", NewDraft));
        var scroll = new ScrollContainer { CustomMinimumSize = new(610, 500) }; body.AddChild(scroll);
        _body = new VBoxContainer(); scroll.AddChild(_body); Hide();
    }
    public override void _Process(double delta)
    { if (Visible && !ReferenceEquals(_observedContent, _service.Host.Content)) Refresh(); }
    internal void Open()
    {
        _service.ClearFeedback();
        Refresh();
        Opened?.Invoke();
    }
    private void Refresh()
    {
        _observedContent = _service.Host.Content;
        foreach (var child in _body.GetChildren()) { _body.RemoveChild(child); child.QueueFree(); }
        if (_service.Host.Content is not { } content) { _body.AddChild(UiStyle.Label("内容尚未加载。")); Show(); return; }
        foreach (var library in Enum.GetValues<BlueprintLibrary>())
        {
            _body.AddChild(UiStyle.Label(library switch { BlueprintLibrary.Packages => "内容包（只读）", BlueprintLibrary.Local => "本局蓝图库", _ => "用户内容库" }, true));
            foreach (var item in content.Blueprints.Where(item => item.Address.Library == library))
            {
                var row = new HFlowContainer(); _body.AddChild(row); row.AddChild(UiStyle.Label(item.Display.Name));
                if (library != BlueprintLibrary.Packages)
                {
                    row.AddChild(UiStyle.Button("OpenBlueprint", "打开／覆盖", () => { Hide(); _service.OpenBlueprint(item.Address); }));
                    row.AddChild(UiStyle.Button("DeleteBlueprint", "删除…", () =>
                    {
                        var confirm = new ConfirmationDialog { Title = "删除蓝图", DialogText = $"删除 {item.Display.Name}？实例不受影响。" };
                        AddChild(confirm); confirm.Confirmed += () => { _service.DeleteBlueprint(item.Address); Refresh(); confirm.QueueFree(); }; confirm.PopupCentered();
                    }));
                }
                foreach (var destination in new[] { BlueprintLibrary.Local, BlueprintLibrary.User })
                    row.AddChild(UiStyle.Button("CopyBlueprint", destination == BlueprintLibrary.Local ? "复制到本局…" : "复制到用户库…", () =>
                    {
                        if (content.FindBlueprint(item.Address) is not ContentLookup<BlueprintRecord>.Found found) { _service.Report("蓝图已不可用。"); return; }
                        Hide(); _service.Open(found.Value.Definition,
                            destination == BlueprintLibrary.Local ? new DraftTarget.LocalBlueprint(null) : new DraftTarget.UserBlueprint(null), new BlueprintSaveTarget.New(destination), true);
                    }));
            }
        }
        _body.AddChild(UiStyle.Label("已保存草稿", true));
        foreach (var draft in content.LocalDrafts.Concat(_service.UserDrafts()))
        {
            var name = draft.Payload is DraftPayload.Design design ? design.Value.Display.Name : draft.Id.Value.ToString();
            _body.AddChild(UiStyle.Button("OpenDraft", $"{name} · {draft.Target}", () => { Hide(); _service.OpenDraft(draft); }));
        }
        Show(); MoveToFront();
    }
    internal void NewDraft()
    {
        var dialog = new AcceptDialog { Title = "新建设计", OkButtonText = "创建" }; AddChild(dialog);
        var body = new VBoxContainer(); dialog.AddChild(body);
        var name = new LineEdit { Text = "新设计", PlaceholderText = "名称" }; body.AddChild(name);
        var kind = new OptionButton(); foreach (var text in new[] { "车辆", "建筑", "大型设备" }) kind.AddItem(text); body.AddChild(kind);
        var size = new OptionButton(); foreach (var label in DesignEditorLabels.VehicleSizes) size.AddItem(label); body.AddChild(size);
        var plot = new OptionButton(); foreach (var label in DesignEditorLabels.PlotSizes) plot.AddItem(label); body.AddChild(plot);
        var building = new OptionButton(); foreach (var label in DesignEditorLabels.BuildingKinds) building.AddItem(label); body.AddChild(building);
        var tags = new LineEdit { PlaceholderText = "设施标签：" + string.Join(",", Enum.GetNames<FacilityTag>()) }; body.AddChild(tags);
        var destination = new OptionButton(); destination.AddItem("本局草稿"); destination.AddItem("用户草稿"); body.AddChild(destination);
        void Refresh() { size.Visible = kind.Selected == 0; plot.Visible = building.Visible = tags.Visible = kind.Selected == 1; }
        kind.ItemSelected += _ => Refresh(); Refresh();
        dialog.Confirmed += () =>
        {
            if (kind.Selected == 1 && tags.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(value => !Enum.TryParse<FacilityTag>(value, true, out _)))
            { _service.Report("设施兼容标签无效。允许：" + string.Join("、", Enum.GetNames<FacilityTag>())); return; }
            DesignSpecification spec = kind.Selected switch
            {
                0 => new DesignSpecification.Vehicle(DesignEditorLabels.VehicleSizeValues[size.Selected]),
                1 => new DesignSpecification.Building((PlotSize)plot.Selected, (BuildingKind)building.Selected, [.. tags.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(value => Enum.Parse<FacilityTag>(value, true))]),
                _ => new DesignSpecification.Assembly(null)
            };
            _service.Open(new(new(name.Text, ""), spec, new([], [], [])), destination.Selected == 0 ? new DraftTarget.LocalBlueprint(null) : new DraftTarget.UserBlueprint(null), null, true);
            dialog.QueueFree(); Hide();
        };
        dialog.PopupCentered(new(460, 390));
    }
}

internal static class DesignEditorLabels
{
    internal static readonly SizeClass[] VehicleSizeValues = Enum.GetValues<SizeClass>();
    internal static readonly string[] VehicleSizes = VehicleSizeValues.Select(SizeLabel).ToArray();
    private static string SizeLabel(SizeClass size) => size switch
    {
        SizeClass.Small => "小型",
        SizeClass.Medium => "中型",
        SizeClass.Large => "大型",
        SizeClass.ExtraLarge => "超大型",
        SizeClass.Titan => "泰坦",
        _ => size.ToString()
    };
    internal static readonly string[] PlotSizes = ["小型地块", "中型地块", "大型地块", "巨型地块", "小型站点地块", "大型站点地块"];
    internal static readonly string[] BuildingKinds = ["生产建筑", "仓库", "供能建筑", "施工建筑", "车站", "防御建筑"];
}
