using Godot;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

/// <summary>
/// 浏览三个蓝图库及尚未保存为蓝图的草稿。
/// Decision note: 2026-10-31-design-editor-direct-editing
/// </summary>
internal partial class BlueprintLibraryPanel : Control
{
    private const int Gap = 12, ThumbnailSize = 96;
    private static readonly string[] Groups = ["本局蓝图库", "用户内容库", "内容包", "草稿"];
    private static readonly string[] GroupDescriptions =
    ["只在这一局可用，随存档保存。", "所有对局都能用，保存在这台电脑上。", "游戏和模组自带，只读。", "尚未保存成蓝图的设计和改装；这里只列出草稿，不是第四个库。"];
    private static readonly BlueprintLibrary[] Libraries = [BlueprintLibrary.Local, BlueprintLibrary.User, BlueprintLibrary.Packages];
    private sealed class LibraryItem(BlueprintSummary? blueprint, SavedContentDraft? draft, DesignDraft? design, DesignEvaluation? evaluation)
    {
        internal BlueprintSummary? Blueprint { get; } = blueprint;
        internal SavedContentDraft? Draft { get; } = draft;
        internal DesignDraft? Design { get; } = design;
        internal DesignEvaluation? Evaluation { get; set; } = evaluation;
        internal object Key => Blueprint is { } value ? value.Address : Draft!.Id;
        internal string Name => Blueprint?.Display.Name ?? Design?.Display.Name ?? "未命名草稿";
    }
    private EditorService _service = null!;
    private GoodsIconCache _icons = null!;
    private HFlowContainer _cards = null!;
    private VBoxContainer _details = null!, _actions = null!;
    private Label _empty = null!, _feedback = null!;
    private LineEdit _search = null!;
    private ScrollContainer _cardScroll = null!;
    private readonly List<LibraryItem> _items = [];
    private readonly Dictionary<LibraryItem, Task<ContentResult<DesignEvaluation>>> _evaluations = [];
    private readonly Button[] _groups = new Button[4], _filters = new Button[3];
    private readonly Dictionary<object, Button> _cardButtons = [];
    private IContentView? _observedContent;
    private int _group, _filter;
    private object? _selected;
    private bool _refreshing;
    internal event Action? Opened;
    internal void Initialize(EditorService service, GoodsIconCache icons)
    { _service = service; _icons = icons; _service.Changed += ServiceChanged; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Ignore;
        var panel = new PanelContainer { Name = "BlueprintLibrary" }; AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = new MarginContainer(); panel.AddChild(margin);
        foreach (var edge in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, Gap);
        var body = new VBoxContainer(); margin.AddChild(body); body.AddThemeConstantOverride("separation", Gap);
        var heading = new HBoxContainer(); body.AddChild(heading);
        var title = UiStyle.Label("蓝图库", true); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; heading.AddChild(title);
        heading.AddChild(UiStyle.Button("NewDesign", "新建设计…", NewDraft, true));
        heading.AddChild(UiStyle.Button("CloseLibrary", "关闭", Hide));
        var scroll = new ScrollContainer { Name = "ColumnsScroll", VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto, SizeFlagsVertical = SizeFlags.ExpandFill }; body.AddChild(scroll);
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; scroll.AddChild(columns);
        columns.AddThemeConstantOverride("separation", Gap);
        var sideScroll = new ScrollContainer { CustomMinimumSize = new(210, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        columns.AddChild(sideScroll);
        var side = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; sideScroll.AddChild(side);
        side.AddThemeConstantOverride("separation", Gap);
        for (var index = 0; index < Groups.Length; index++)
        {
            var group = index;
            _groups[index] = UiStyle.Button("Group" + index, Groups[index], () => { _group = group; _selected = null; RenderCards(); });
            _groups[index].ToggleMode = true; side.AddChild(_groups[index]); Text(side, GroupDescriptions[index]);
        }
        side.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        Text(side, "要造车或改装，到非敌对聚居地委托。");
        var middle = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; columns.AddChild(middle);
        _search = new LineEdit { Name = "LibrarySearch", PlaceholderText = "搜索设计名称", TooltipText = "搜索设计名称" };
        middle.AddChild(UiStyle.Label("搜索设计")); middle.AddChild(_search); _search.TextChanged += _ => RenderCards();
        var filters = new HBoxContainer(); middle.AddChild(filters);
        for (var index = 0; index < _filters.Length; index++)
        {
            var filter = index;
            _filters[index] = UiStyle.Button("Filter" + index, new[] { "全部", "车辆", "建筑" }[index], () => { _filter = filter; RenderCards(); });
            _filters[index].ToggleMode = true; filters.AddChild(_filters[index]);
        }
        _cardScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; middle.AddChild(_cardScroll);
        var cardBody = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; _cardScroll.AddChild(cardBody);
        _empty = Text(cardBody, "");
        _cards = new HFlowContainer { Name = "Cards", SizeFlagsHorizontal = SizeFlags.ExpandFill }; cardBody.AddChild(_cards);
        var right = new VBoxContainer { CustomMinimumSize = new(320, 0) }; columns.AddChild(right);
        var detailScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; right.AddChild(detailScroll);
        _details = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; detailScroll.AddChild(_details); _details.AddThemeConstantOverride("separation", Gap);
        _actions = new VBoxContainer(); right.AddChild(_actions);
        _feedback = Text(body, ""); Hide();
    }

    internal void Open() { _service.ClearFeedback(); Refresh(); Opened?.Invoke(); }
    private void ServiceChanged() { if (Visible && IsNodeReady() && !_refreshing) Refresh(); }
    private void Refresh()
    {
        _refreshing = true;
        try
        {
            _observedContent = _service.Host.Content; _items.Clear(); _evaluations.Clear(); RefreshVehicleCounts();
            if (_observedContent is { } content)
            {
                foreach (var summary in content.Blueprints)
                {
                    var record = (content.FindBlueprint(summary.Address) as ContentLookup<BlueprintRecord>.Found)?.Value;
                    _items.Add(new(summary, null, record?.Definition, record?.Evaluation));
                }
                foreach (var draft in content.LocalDrafts.Concat(_service.UserDrafts()))
                {
                    if (draft.Payload is not DraftPayload.Design design) continue;
                    var item = new LibraryItem(null, draft, design.Value, null); _items.Add(item);
                    _evaluations[item] = Task.Run(() =>
                    {
                        try { return _service.Host.ContentFactory.EvaluateDesign(content, design.Value); }
                        catch (Exception error)
                        {
                            _service.LogDraftEvaluationFailure(error);
                            return new ContentResult<DesignEvaluation>.Faulted("preview_failed", "草稿评估失败。", null);
                        }
                    });
                }
            }
            RenderCards(); Show(); MoveToFront();
        }
        finally { _refreshing = false; }
    }
    public override void _Process(double delta)
    {
        if (!Visible) return;
        if (!ReferenceEquals(_observedContent, _service.Host.Content)) { Refresh(); return; }
        var updated = false;
        foreach (var (item, task) in _evaluations.ToArray())
        {
            if (!task.IsCompleted) continue;
            item.Evaluation = task.Result is ContentResult<DesignEvaluation>.Success success ? success.Value : new DesignEvaluation.Failed([]);
            _evaluations.Remove(item); updated = true;
        }
        if (updated) RenderCards();
        _feedback.Text = _service.Message ?? "";
    }
    private void RenderCards()
    {
        var focused = _cardButtons.FirstOrDefault(pair => pair.Value.HasFocus()).Key;
        var scroll = _cardScroll.ScrollVertical; Clear(_cards); _cardButtons.Clear();
        for (var index = 0; index < _groups.Length; index++) _groups[index].SetPressedNoSignal(index == _group);
        for (var index = 0; index < _filters.Length; index++) _filters[index].SetPressedNoSignal(index == _filter);
        var query = _search.Text.Trim();
        var visible = _items.Where(item => (_group == 3 ? item.Draft is not null : item.Blueprint?.Address.Library == Libraries[_group]) &&
            item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && (_filter == 0 ||
                _filter == 1 && item.Design?.Specification is DesignSpecification.Vehicle ||
                _filter == 2 && item.Design?.Specification is DesignSpecification.Building)).ToArray();
        var selected = visible.FirstOrDefault(item => Equals(item.Key, _selected)) ?? visible.FirstOrDefault(); _selected = selected?.Key;
        _empty.Visible = visible.Length == 0;
        _empty.Text = _observedContent is null ? "内容尚未加载。" : query.Length != 0 || _filter != 0 ? "没有符合条件的设计。" : _group == 3 ? "这里还没有草稿。" : "这里还没有蓝图。";
        foreach (var item in visible)
        {
            var card = UiStyle.Button("DesignCard", "", () => { _selected = item.Key; SelectCard(item); });
            card.ToggleMode = true; card.CustomMinimumSize = new(230, 144); card.TooltipText = item.Name;
            _cards.AddChild(card); _cardButtons.Add(item.Key, card); card.SetPressedNoSignal(item == selected);
            var box = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; card.AddChild(box);
            box.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); box.OffsetLeft = box.OffsetTop = Gap; box.OffsetRight = box.OffsetBottom = -Gap;
            var thumbnail = Thumbnail(item, ThumbnailSize); thumbnail.MouseFilter = MouseFilterEnum.Ignore; box.AddChild(thumbnail);
            var caption = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill }; box.AddChild(caption);
            var name = Text(caption, item.Name); name.Name = "CardName";
            name.MaxLinesVisible = 2; name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            Text(caption, KindName(item.Design?.Specification));
            if (item.Draft is { } draft)
            {
                var source = Text(caption, DraftDescription(draft)); source.TooltipText = source.Text;
                source.MaxLinesVisible = 1; source.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            }
        }
        ShowDetails(selected);
        Callable.From(() => { if (GodotObject.IsInstanceValid(_cardScroll)) _cardScroll.ScrollVertical = scroll; }).CallDeferred();
        if (focused is not null && _cardButtons.TryGetValue(focused, out var focus)) focus.GrabFocus();
    }
    private void SelectCard(LibraryItem item)
    {
        foreach (var (key, button) in _cardButtons) button.SetPressedNoSignal(Equals(key, item.Key));
        ShowDetails(item);
    }
    private GoodIcon Thumbnail(LibraryItem item, int size) => item.Blueprint?.Design is { } hash ? _icons.CreateDesign(hash, size)
        : item.Evaluation is { } evaluation ? _icons.CreateDesign(evaluation, size) : new GoodIcon(size);
    private static Label Text(Node parent, string text)
    {
        var label = UiStyle.Label(text); label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill; parent.AddChild(label); return label;
    }
    private static void Clear(Node parent) { foreach (var child in parent.GetChildren()) { parent.RemoveChild(child); child.QueueFree(); } }
    private static string KindName(DesignSpecification? specification) => specification switch
    { DesignSpecification.Vehicle => "车辆", DesignSpecification.Building => "建筑", DesignSpecification.Assembly => "大型设备", _ => "设计内容不可用" };
    public override void _ExitTree() => _service.Changed -= ServiceChanged;
}
