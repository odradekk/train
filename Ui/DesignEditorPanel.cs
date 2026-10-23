using System.Collections.Immutable;
using Godot;
using Train.App.Editing;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal partial class DesignEditorPanel : Control
{
    internal const string UndoAction = "editor_undo";
    internal const string RedoAction = "editor_redo";
    internal const string SaveAction = "editor_save";
    internal const string CloseAction = "editor_close";
    private EditorService _service = null!;
    private EditorSceneController _scene = null!;
    private LineEdit _name = null!, _description = null!;
    private Label _status = null!, _previewText = null!, _information = null!;
    private VBoxContainer _diagnostics = null!;
    private ItemList _elements = null!;
    private OptionButton _voxel = null!, _shape = null!, _orientation = null!, _tool = null!, _action = null!, _axis = null!;
    private ColorPickerButton _paint = null!;
    private readonly List<VoxelCatalogEntry> _voxels = [];
    private VBoxContainer _equipment = null!;
    private readonly List<VoxelOrientation> _allowedOrientations = [];
    private readonly List<EditElementId> _elementIds = [];
    private readonly List<(EditElementId Id, string Text)> _elementRows = [];
    private SpinBox _x = null!, _y = null!, _z = null!, _endX = null!, _endY = null!, _endZ = null!, _width = null!, _plane = null!, _cut = null!;
    private IEditPreview? _preview;
    internal event Action? LibraryRequested;
    private EditStamp? _shown;
    private EditElementId? _shownEntered;
    private DesignEvaluation? _shownEvaluation;
    private ImmutableArray<SceneProblem> _shownFrameDiagnostics;
    private SceneHit? _lastHit;
    private EditSessionId? _paletteSession;
    private readonly List<GridPosition> _stroke = [];
    private ConfirmationDialog _confirm = null!;
    private PanelContainer _leave = null!, _topBar = null!, _toolsPanel = null!, _inspectorPanel = null!, _bottomPanel = null!;
    private Button _undo = null!, _redo = null!, _saveBlueprint = null!;
    private Button _returnServices = null!;
    internal event Action<InstanceId>? ServiceRequested;
    internal EditorSceneController Scene => _scene;
    internal void Initialize(EditorService service)
    {
        _service = service;
        _scene = new(service);
        _service.BlueprintSaved += ReturnToLibrary;
    }
    private void ReturnToLibrary() => LibraryRequested?.Invoke();
    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_scene.View); _scene.Picked += Pick;
        var top = _topBar = new PanelContainer(); top.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide); AddChild(top);
        var bar = new HFlowContainer(); top.AddChild(bar);
        _name = new LineEdit { CustomMinimumSize = new(160, 36), PlaceholderText = "名称" }; bar.AddChild(_name);
        _description = new LineEdit { CustomMinimumSize = new(180, 36), PlaceholderText = "说明" }; bar.AddChild(_description);
        Add(bar, "Properties", "更新名称说明", () =>
        {
            if (_service.State is { } state)
                Preview(new EditOperation.Properties(new(_name.Text, _description.Text),
                    (_scene.EnteredDraft ?? state.Snapshot.Draft).Specification));
        });
        _undo = UiStyle.Button("Undo", "撤销", _service.Undo); bar.AddChild(_undo);
        _redo = UiStyle.Button("Redo", "重做", _service.Redo); bar.AddChild(_redo);
        Add(bar, "SaveDraft", "保存草稿", _service.SaveDraft); _saveBlueprint = UiStyle.Button("SaveBlueprint", "保存蓝图…", SaveBlueprint); bar.AddChild(_saveBlueprint);
        Add(bar, "ExitEditor", "退出", () => _service.RequestClose(() => { }));
        _returnServices = UiStyle.Button("ReturnToVehicleServices", "返回车辆服务", () =>
        {
            if (_service.Target is DraftTarget.Instance { Id: InstanceId.Vehicle vehicle })
                _service.RequestClose(() => ServiceRequested?.Invoke(vehicle));
        });
        bar.AddChild(_returnServices);
        var left = _toolsPanel = Panel("Tools", "体素与工具", new(8, 64), 270, out var tools);
        _voxel = Choice(tools, []); _voxel.ItemSelected += _ => RefreshShapes();
        _shape = Choice(tools, []); _orientation = Choice(tools, []);
        _paint = new ColorPickerButton { Color = Colors.White, CustomMinimumSize = new(240, 32) }; tools.AddChild(_paint);
        var colors = new GridContainer { Columns = 2 }; tools.AddChild(colors);
        foreach (var (name, color) in new[]
                 {
                     ("民用蓝灰", "#5a6b78"), ("货运暗红", "#7b3a2c"), ("工程黄", "#d9a62b"),
                     ("军用橄榄", "#626750"), ("军用石墨", "#33373b"), ("工业钢灰", "#7e858b")
                 })
        {
            var swatch = new Button { Text = name, TooltipText = color, CustomMinimumSize = new(116, 32) };
            swatch.AddChild(new ColorRect { Position = new(4, 9), Size = new(14, 14), Color = Colors.LightGray, MouseFilter = MouseFilterEnum.Ignore });
            swatch.AddChild(new ColorRect { Position = new(5, 10), Size = new(12, 12), Color = new(color), MouseFilter = MouseFilterEnum.Ignore });
            swatch.Pressed += () => { _paint.Color = new Color(color); InvalidatePreview(); }; colors.AddChild(swatch);
        }
        _tool = Choice(tools, ["单格", "笔刷（拖动松开一笔）", "长方体", "选区"]);
        _action = Choice(tools, ["添加", "移除", "替换", "涂装", "形状"]);
        _width = Number(tools, "笔刷宽度", 1, 16, 1);
        _axis = Choice(tools, ["工作平面 X", "工作平面 Y", "工作平面 Z"]); _axis.Select(1);
        _plane = Number(tools, "平面坐标", -4096, 4096);
        Add(tools, "SurfacePlane", "使用拾取表面平面", () =>
        {
            if (_lastHit is not { } hit) { _service.Report("请先拾取设计表面。"); return; }
            var axis = _scene.FaceAxis(hit); var cell = _scene.CellFor(hit, false);
            _axis.Select((int)axis); _plane.Value = axis == GridAxis.X ? cell.X : axis == GridAxis.Y ? cell.Y : cell.Z; Plane();
        });
        var clipping = new CheckButton { Text = "启用高度剖切" }; tools.AddChild(clipping); _cut = Number(tools, "最高可见 Y", -4096, 4096);
        clipping.Toggled += enabled => _scene.Options = _scene.Options with { HighestVisibleY = enabled ? (int)_cut.Value : null };
        _cut.ValueChanged += value => { if (clipping.ButtonPressed) _scene.Options = _scene.Options with { HighestVisibleY = (int)value }; };
        _axis.ItemSelected += _ => Plane(); _plane.ValueChanged += _ => Plane();
        Add(tools, "RetryScene", "重试场景", () => _scene.Process(0, true));
        _equipment = new VBoxContainer(); tools.AddChild(_equipment);
        var right = _inspectorPanel = Panel("Inspector", "层级与精确参数", new(1000, 64), 290, out var inspector);
        right.SetAnchorsAndOffsetsPreset(LayoutPreset.TopRight);
        right.OffsetLeft = -310; right.OffsetRight = -8; right.OffsetTop = 64;
        _elements = new ItemList { CustomMinimumSize = new(260, 160), SelectMode = ItemList.SelectModeEnum.Multi }; inspector.AddChild(_elements);
        _elements.MultiSelected += (_, _) => _service.Select(new EditSelection.Elements(
            [.. _elements.GetSelectedItems().Select(index => _elementIds[index])]));
        Add(inspector, "EnterObject", "进入所选设备", () =>
        {
            var index = _elements.GetSelectedItems().FirstOrDefault(-1);
            if (index >= 0) _service.Enter(_elementIds[index]);
        });
        Add(inspector, "ExitObject", "返回上层", () => _service.Enter(null));
        Add(inspector, "ExtractObject", "将所选设备另存为蓝图…", () =>
        {
            if (_elements.GetSelectedItems().Length == 0) return;
            if (_service.CaptureObject(_elementIds[_elements.GetSelectedItems()[0]]) is not { } captured) return;
            _service.RequestClose(() => _service.Open(captured, new DraftTarget.UserBlueprint(null),
                new BlueprintSaveTarget.New(BlueprintLibrary.User), true));
        });
        _x = Number(inspector, "X／移动量 X", -4096, 4096); _y = Number(inspector, "Y／移动量 Y", -4096, 4096); _z = Number(inspector, "Z／移动量 Z", -4096, 4096);
        _endX = Number(inspector, "区域终点 X", -4096, 4096); _endY = Number(inspector, "区域终点 Y", -4096, 4096); _endZ = Number(inspector, "区域终点 Z", -4096, 4096);
        Add(inspector, "EditSpecification", "设计规格…", EditSpecification);
        Add(inspector, "Preview", "预览精确参数", () => PreviewCells());
        Add(inspector, "Apply", "应用预览", Apply);
        Add(inspector, "SelectBox", "框选区域", () => _service.Select(new EditSelection.Box(Bounds())));
        Add(inspector, "Move", "移动选区", () => Preview(new EditOperation.MoveSelection(At())));
        Add(inspector, "Copy", "复制选区", () => Preview(new EditOperation.CopySelection(At())));
        Add(inspector, "Rotate", "选区旋转 90°", () => Preview(new EditOperation.RotateSelection((GridAxis)_axis.Selected, At(), 1)));
        Add(inspector, "Remove", "删除选区", () => Preview(new EditOperation.RemoveSelection()));
        Add(inspector, "Paint", "选区涂装", () => Preview(new EditOperation.PaintSelection(Paint())));
        Add(inspector, "Shape", "选区形状", () => { if (Shape() is { } shape) Preview(new EditOperation.ShapeSelection(shape)); });
        var bottom = _bottomPanel = new PanelContainer(); bottom.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide); bottom.OffsetTop = -220; AddChild(bottom);
        var scroll = new ScrollContainer { CustomMinimumSize = new(0, 210) }; bottom.AddChild(scroll);
        var information = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(information);
        _status = UiStyle.Label(""); _previewText = UiStyle.Label(""); _information = UiStyle.Label("");
        foreach (var label in new[] { _status, _previewText, _information }) { label.AutowrapMode = TextServer.AutowrapMode.WordSmart; label.SizeFlagsHorizontal = SizeFlags.ExpandFill; }
        information.AddChild(_status); information.AddChild(_previewText); information.AddChild(_information);
        _diagnostics = new VBoxContainer(); information.AddChild(_diagnostics);
        _confirm = new ConfirmationDialog { Title = "确认大范围修改", OkButtonText = "确认应用" };
        AddChild(_confirm);
        _confirm.Confirmed += () =>
        {
            if (_preview is { } preview) _service.Apply(preview, true);
        };
        _leave = UiStyle.Panel(this, "LeaveEditor", "存在未保存修改", new(440, 220), 410, () => _service.ResolveClose(LeaveChoice.Cancel), out var leave);
        Add(leave, "SaveAndLeave", "保存草稿并退出", () => _service.ResolveClose(LeaveChoice.Save));
        Add(leave, "DiscardAndLeave", "放弃本次修改", () => _service.ResolveClose(LeaveChoice.Discard));
        Add(leave, "CancelLeave", "继续编辑", () => _service.ResolveClose(LeaveChoice.Cancel)); _leave.Hide(); Hide();
        void InvalidatePreview() { _preview = null; _scene.ShowPreview(null); _previewText.Text = "参数已更改，请重新预览。"; }
        foreach (var number in new[] { _x, _y, _z, _endX, _endY, _endZ, _width }) number.ValueChanged += _ => InvalidatePreview();
        foreach (var choice in new[] { _voxel, _shape, _orientation, _tool, _action }) choice.ItemSelected += _ => InvalidatePreview();
        _paint.ColorChanged += _ => InvalidatePreview();
    }
    private Rect2 FramingArea()
    {
        var left = _toolsPanel.GetGlobalRect().End.X;
        var right = _inspectorPanel.GetGlobalRect().Position.X;
        var top = _topBar.GetGlobalRect().End.Y;
        var bottom = _bottomPanel.GetGlobalRect().Position.Y;
        return new(left, top, Mathf.Max(0f, right - left), Mathf.Max(0f, bottom - top));
    }
    private PanelContainer Panel(string name, string title, Vector2 position, float width, out VBoxContainer body)
    {
        var panel = new PanelContainer { Name = name, Position = position, CustomMinimumSize = new(width, 0) }; AddChild(panel);
        var box = new VBoxContainer(); panel.AddChild(box); box.AddChild(UiStyle.Label(title, true));
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll); body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(body);
        void Fit() => scroll.CustomMinimumSize = new(width - 24, Math.Max(150, Size.Y - 340));
        Resized += Fit; Fit();
        return panel;
    }
    private static void Add(Node parent, string name, string text, Action action) => parent.AddChild(UiStyle.Button(name, text, action));
    private static OptionButton Choice(Node parent, string[] values)
    {
        var value = new OptionButton();
        foreach (var text in values) value.AddItem(text);
        parent.AddChild(value);
        return value;
    }
    private static SpinBox Number(Node parent, string name, int minimum, int maximum, int value = 0)
    {
        var row = new HBoxContainer();
        parent.AddChild(row);
        row.AddChild(UiStyle.Label(name));
        var input = new SpinBox
        {
            MinValue = minimum, MaxValue = maximum, Value = value, Step = 1,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        row.AddChild(input);
        return input;
    }
    private GridPosition At() => new((int)_x.Value, (int)_y.Value, (int)_z.Value);
    private GridBounds Bounds() => new(At(), new((int)_endX.Value, (int)_endY.Value, (int)_endZ.Value));
    private PaintColor Paint() => new("#" + _paint.Color.ToHtml(false));
    private VoxelShape? Shape() => _shape.Selected < 0 ? null : new(_shape.GetItemText(_shape.Selected));
    private void Plane() => _scene.Options = _scene.Options with { WorkPlane = new((GridAxis)_axis.Selected, (int)_plane.Value) };
    private CellEdit? Action()
    {
        if (_action.Selected == 1) return new CellEdit.Remove();
        if (_action.Selected == 3) return new CellEdit.Paint(Paint());
        if (_action.Selected == 4) return Shape() is { } shape ? new CellEdit.Shape(shape) : null;
        if (_voxel.Selected < 0 || _voxel.Selected >= _voxels.Count) { _service.Report("请选择体素。 "); return null; }
        var orientation = _allowedOrientations.Count > 0
            ? _allowedOrientations[Math.Max(0, _orientation.Selected)] : VoxelOrientation.PxPy;
        var value = new VoxelPaletteEntry(_voxels[_voxel.Selected].Definition, orientation, Shape(), Paint());
        return _action.Selected == 0 ? new CellEdit.Add(value) : new CellEdit.Replace(value);
    }
    private void PreviewCells(EditFootprint? footprint = null)
    {
        if (Action() is not { } action) return;
        footprint ??= _tool.Selected == 2 ? new EditFootprint.Box(Bounds()) : new EditFootprint.Cell(At());
        Preview(new EditOperation.Cells(footprint, action));
    }
    private void Preview(EditOperation operation)
    {
        _preview = (_service.Preview(operation) as SceneResult<IEditPreview>.Success)?.Value;
        _scene.ShowPreview(_preview);
        if (_preview is { } current) _service.ReportProblems(current.Conflicts.Select(conflict => conflict.Problem));
        _previewText.Text = _preview is { } p
            ? $"覆盖 {p.CoveredCells} · 影响 {p.AffectedCells} · 跳过 {p.SkippedCells} · 占格{(p.OccupancyComplete ? "完整" : "未知")}\n"
                + string.Join("；", p.Conflicts.Select(ConflictText))
            : "预览不可用";
        string ConflictText(EditConflict conflict)
        {
            var text = EditorService.Explain(conflict.Problem, _service.Host.Content);
            return conflict.Cell is { } cell ? $"({cell.X}, {cell.Y}, {cell.Z})：{text}" : text;
        }
    }
    private void Apply()
    {
        if (_preview is not { CanApply: true } preview) return;
        if (preview.RequiresConfirmation)
        {
            _confirm.DialogText = $"将影响 {preview.AffectedCells} 格。确认应用？";
            _confirm.PopupCentered();
        }
        else _service.Apply(preview, false);
    }
    private void Pick(ScenePick pick)
    {
        if (pick is ScenePick.Miss)
        {
            if (_scene.PickPlane(GetViewport().GetMousePosition()) is not { } planeCell) { _service.Report("射线未命中设计或工作平面。"); return; }
            _x.Value = planeCell.X; _y.Value = planeCell.Y; _z.Value = planeCell.Z;
            if (_tool.Selected == 1) { _stroke.Clear(); _stroke.Add(planeCell); } else PreviewCells();
            return;
        }
        if (pick is not ScenePick.Hit { Value: var hit }) return;
        if (_tool.Selected == 3 && hit.Subject is PickSubject.Editing element) { _service.Select(new EditSelection.Elements([element.Element])); return; }
        _lastHit = hit;
        var cell = _scene.CellFor(hit, _action.Selected == 0);
        _x.Value = cell.X; _y.Value = cell.Y; _z.Value = cell.Z;
        if (_tool.Selected == 1) { _stroke.Clear(); _stroke.Add(cell); return; }
        PreviewCells();
    }
    public override void _UnhandledInput(InputEvent input)
    {
        if (!Visible) return;
        if (input.IsActionPressed(UndoAction)) _service.Undo();
        else if (input.IsActionPressed(RedoAction)) _service.Redo();
        else if (input.IsActionPressed(SaveAction)) _service.SaveDraft();
        else if (input.IsActionPressed(CloseAction)) _service.RequestClose(() => { });
        else if (_stroke.Count > 0 && input is InputEventMouseMotion motion && motion.ButtonMask.HasFlag(MouseButtonMask.Left))
        {
            if (GetViewport().GuiGetHoveredControl() is not null) return;
            var pick = _scene.Pick(motion.Position);
            var cell = pick switch
            {
                ScenePick.Hit hit => _scene.CellFor(hit.Value, _action.Selected == 0),
                ScenePick.Miss => _scene.PickPlane(motion.Position),
                _ => null
            };
            if (cell is { } sample && !_stroke.Contains(sample)) _stroke.Add(sample);
        }
        else if (_stroke.Count > 0 && input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            PreviewCells(new EditFootprint.Stroke([.. _stroke], (int)_width.Value));
            _stroke.Clear();
            Apply();
        }
        else return;
        GetViewport().SetInputAsHandled();
    }
    public override void _Process(double delta)
    {
        Visible = _service.IsOpen; _scene.VisibleArea = FramingArea(); _scene.Process(delta);
        if (!Visible) return;
        _undo.Disabled = _service.State?.CanUndo != true;
        _redo.Disabled = _service.State?.CanRedo != true;
        _saveBlueprint.Visible = _service.Target is not DraftTarget.PackageDefinition;
        _returnServices.Visible = _service.Target is DraftTarget.Instance { Id: InstanceId.Vehicle };
        _leave.Visible = _service.Leaving; if (_service.Leaving) _leave.MoveToFront();
        var active = _service.State;
        var segments = new[]
        {
            active is null ? "" : $"层级 {active.EnteredObject?.Value.ToString() ?? "根"}",
            active is null ? "" : $"已选 {active.Selection.Length}",
            _service.Message ?? "",
            _service.SceneUnavailable ? "场景不可用，可保存、退出或重试" : "",
            _service.Host.GetSaveStatus()?.Frozen == true ? "核心已冻结；本局草稿仅保留到离开本局，无法写入存档；用户库草稿可写盘；有效蓝图暂不可保存" : "",
            _service.IsDirty ? "未保存修改" : ""
        };
        _status.Text = string.Join(" · ", segments.Where(segment => !string.IsNullOrWhiteSpace(segment)));
        if (_service.State is not { } state) return;
        if (_paletteSession != state.Snapshot.Stamp.Session) { _paletteSession = state.Snapshot.Stamp.Session; RefreshPalette(); }
        for (var index = 0; index < _elementIds.Count; index++)
        {
            if (state.Selection.Contains(_elementIds[index])) _elements.Select(index, false);
            else _elements.Deselect(index);
        }
        if (_shown == state.Snapshot.Stamp && _shownEntered == state.EnteredObject)
        {
            if (!ReferenceEquals(_shownEvaluation, state.CurrentEvaluation) ||
                _shownFrameDiagnostics != _service.FrameDiagnostics) ShowEvaluation(state);
            return;
        }
        _shown = state.Snapshot.Stamp;
        _shownEntered = state.EnteredObject;
        _preview = null;
        _previewText.Text = "";
        var current = _scene.EnteredDraft ?? state.Snapshot.Draft;
        _name.Text = current.Display.Name; _description.Text = current.Display.Description;
        ShowElements([.. state.Elements.Select(item => (item.Id, Text:
            $"{(item.Parent is { } parent ? $"↳ {parent.Value} / " : "")}{item.Id.Value} · {ElementKindText(item.Kind)} {PositionText(item.At)}{(item.Locked ? " [锁定]" : "")}"))]);
        ShowEvaluation(state);
    }
    // 泰坦约 2.8 万行；整表重建时引擎要重新排版全部行，一帧约 1.7 秒。只改变化的区间，变化太大才整表重建。
    private void ShowElements((EditElementId Id, string Text)[] rows)
    {
        var change = RowChange.Between(_elementRows, rows);
        if (change.Removed + change.Inserted > 256)
        {
            _elements.Clear();
            foreach (var row in rows) _elements.AddItem(row.Text);
        }
        else
        {
            var replaced = Math.Min(change.Removed, change.Inserted);
            for (var index = change.Start; index < change.Start + replaced; index++) _elements.SetItemText(index, rows[index].Text);
            for (var index = change.Start + change.Removed - 1; index >= change.Start + replaced; index--) _elements.RemoveItem(index);
            for (var index = change.Start + replaced; index < change.Start + change.Inserted; index++)
            {
                _elements.AddItem(rows[index].Text);
                _elements.MoveItem(_elements.ItemCount - 1, index);
            }
        }
        _elementRows.Clear();
        _elementRows.AddRange(rows);
        _elementIds.Clear();
        _elementIds.AddRange(rows.Select(row => row.Id));
    }
    private void RefreshPalette()
    {
        _voxels.Clear(); _voxel.Clear();
        var sceneVoxels = _service.State?.Snapshot.Draft.Specification is DesignSpecification.SceneModel or DesignSpecification.Creature;
        foreach (var item in (_service.FixedContent?.Voxels ?? []).Where(item => sceneVoxels ? item.Good is null : item.Good is not null))
        {
            _voxels.Add(item);
            var name = _service.FixedContent?.FindVoxelDisplay(item.Definition) is ContentLookup<VoxelDisplayProjection>.Found display ? display.Value.Display.Name : item.Definition.Value;
            _voxel.AddItem(item.Good is { } good ? $"{name} · Value {good.BaseValue.Value} · {good.MassTons:0.##} t" : name);
        }
        RefreshShapes();
        RefreshEquipment();
    }
    private void RefreshShapes()
    {
        _shape.Clear(); _orientation.Clear(); _allowedOrientations.Clear(); if (_voxel.Selected < 0 || _voxel.Selected >= _voxels.Count) return;
        if (_service.FixedContent?.FindVoxelAppearance(_voxels[_voxel.Selected].Definition) is not ContentLookup<VoxelAppearanceProjection>.Found found) return;
        if (found.Value.Presentation is VoxelPresentation.Cube cube) foreach (var shape in cube.Shapes) _shape.AddItem(shape.Name);
        var orientations = found.Value.AllowedOrientations switch
        {
            AllowedVoxelOrientations.None => new[] { VoxelOrientation.PxPy },
            AllowedVoxelOrientations.FourWay => new[] { VoxelOrientation.PxPy, VoxelOrientation.PzPy, VoxelOrientation.NxPy, VoxelOrientation.NzPy },
            _ => Enum.GetValues<VoxelOrientation>()
        };
        foreach (var orientation in orientations) { _allowedOrientations.Add(orientation); _orientation.AddItem(OrientationText(orientation)); }
    }
    private static string OrientationText(VoxelOrientation orientation)
    {
        var name = orientation.ToString();
        return $"右 {Axis(name[..2])} · 上 {Axis(name[2..])}";
        static string Axis(string axis) => $"{(axis[0] == 'P' ? "+" : "-")}{char.ToUpperInvariant(axis[1])}";
    }
    private static string ElementKindText(EditElementKind kind) => kind switch
    {
        EditElementKind.Cell => "体素", EditElementKind.Assembly => "组装体",
        EditElementKind.FunctionalComposite => "功能复合体", EditElementKind.SceneModel => "场景模型",
        _ => "未知元素"
    };
    private static string PositionText(GridPosition position) => $"({position.X}, {position.Y}, {position.Z})";
    private void ShowEvaluation(EditState state)
    {
        foreach (var child in _diagnostics.GetChildren()) { _diagnostics.RemoveChild(child); child.QueueFree(); }
        _shownEvaluation = state.CurrentEvaluation;
        _shownFrameDiagnostics = _service.FrameDiagnostics;
        foreach (var problem in _shownFrameDiagnostics)
            _diagnostics.AddChild(UiStyle.Label(EditorService.Explain(problem, _service.Host.Content)));
        _information.Text = state.CurrentEvaluation switch
        {
            null => "评估中；合法性、BOM 和转动范围尚未确认。",
            DesignEvaluation.Complete complete => CompleteInformation(complete),
            DesignEvaluation.Partial partial =>
                $"信息不完整 · 质量：{Computed(partial.Metrics.MassTons, mass => $"{mass:0.###} t")}"
                + $" · Value：{Computed(partial.Metrics.StructuralBaseValue, value => value.Value.ToString())}"
                + $" · 结构耐久：{Computed(partial.Metrics.MaxStructureDurability, value => value.ToString())}"
                + $"\nBOM：{Computed(partial.Metrics.BillOfMaterials, Bom)}"
                + $"\n已知小计：质量 {partial.KnownSubtotal.MassTons:0.###} t · Value {partial.KnownSubtotal.StructuralBaseValue.Value} · 结构耐久 {partial.KnownSubtotal.MaxStructureDurability}",
            _ => "评估失败，不能保存有效蓝图。"
        };
        var scene = state.CurrentEvaluation switch { DesignEvaluation.Complete complete => complete.Scene, DesignEvaluation.Partial partial => partial.Scene, _ => null };
        if (scene is not null)
        {
            _information.Text += SweepInformation(scene);
            foreach (var locked in scene.EditLocks)
            {
                var row = new HBoxContainer(); _diagnostics.AddChild(row);
                row.AddChild(UiStyle.Label(EditorService.Explain(locked, _service.Host.Content)));
                Add(row, "LocateLockedElement", "定位锁定项", () => _service.Locate(locked.Element));
            }
        }
        foreach (var diagnostic in state.CurrentEvaluation switch
        {
            DesignEvaluation.Complete complete => complete.Diagnostics,
            DesignEvaluation.Partial partial => partial.Diagnostics,
            DesignEvaluation.Failed failed => failed.Diagnostics,
            _ => []
        })
        {
            var row = new HBoxContainer(); _diagnostics.AddChild(row); row.AddChild(UiStyle.Label(diagnostic.Message));
            if (diagnostic.Element is { } path) Add(row, "LocateDiagnostic", "定位", () => _service.Locate(path));
        }
    }
    private static string Computed<T>(ComputedValue<T> value, Func<T, string> format) where T : notnull => value is ComputedValue<T>.Known known ? format(known.Value) : "未知";
    private string Bom(ImmutableArray<GoodsQuantity> values)
    {
        if (values.IsEmpty) return "无";
        return string.Join("、", values.Select(item =>
        {
            var name = item.Good is GoodId.Assembly &&
                _service.FixedContent!.FindGood(item.Good) is not ContentLookup<GoodPresentation>.Found
                    ? "组装体（名称不可用）" : WorldPanels.GoodName(item.Good, _service.FixedContent!);
            return $"{name} × {item.Quantity}";
        }));
    }
    private static string BoundsText(GridBounds? bounds) => bounds is null ? "未知" :
        $"X {bounds.Minimum.X}～{bounds.Maximum.X}，Y {bounds.Minimum.Y}～{bounds.Maximum.Y}，Z {bounds.Minimum.Z}～{bounds.Maximum.Z}";
    private string CompleteInformation(DesignEvaluation.Complete evaluation)
    {
        var projection = evaluation.Core?.Design;
        var scene = evaluation.Scene;
        var text = $"{(evaluation.IsValid ? "完整合法" : "设计不合法")} · 占格范围 {BoundsText(scene.OccupiedBounds)} · 包络 {BoundsText(scene.AllowedEnvelope)}";
        if (projection is not null)
        {
            text += $"\n质量 {projection.MassTons:0.###} t · 结构耐久上限 {projection.MaxStructureDurability} · 总 Value {projection.StructuralBaseValue.Value}";
            text += "\nBOM：" + Bom(projection.BillOfMaterials);
            text += "\n部件：" + (projection.Parts.IsEmpty ? "无" : string.Join("；", projection.Parts.Select(part =>
                $"{part.Id.Value} {PartName(part.Definition)}：{string.Join("、", part.Capabilities.Select(CapabilityText))}")));
        }
        if (!scene.OccupancyComplete) text += "\n占格未知，不能确认完整冲突检查。";
        return text;
    }
    private string PartName(CoreDefinitionReference definition) =>
        _service.FixedContent!.FindDefinitionDisplay(definition) is ContentLookup<ContentDisplay>.Found found
            ? found.Value.Text.Name : "部件定义不可用";
    private static string CapabilityText(PartCapability capability) => capability switch
    {
        PartCapability.Propulsion value => $"牵引力 {value.TractionKilonewtons:0.##} kN，额定速度 {value.RatedSpeedMetersPerSecond:0.##} m/s",
        PartCapability.Control => "控制",
        PartCapability.Storage value => $"库存容量 {value.Capacity}",
        PartCapability.Production value => $"生产工作量 {value.WorkPerSecond:0.##}/秒",
        PartCapability.Extraction value => $"开采工作量 {value.WorkPerSecond:0.##}/秒",
        PartCapability.Construction value => $"施工工作量 {value.WorkPerSecond:0.##}/秒，最大建筑地块尺寸 {EconomyPanels.PlotSizeName(value.MaximumBuildingPlotSize)}"
            + $"，最大车辆尺寸 {(value.MaximumVehicleSize is { } size ? EconomyPanels.SizeName(size) : "无")}",
        PartCapability.Power value => $"能量 {value.EnergyPerSecond:0.##}/秒",
        PartCapability.Weapon value => $"武器伤害 {value.Damage}，间隔 {value.IntervalSeconds:0.##} 秒",
        PartCapability.Platform => "站台",
        _ => "未知能力"
    };
    private static string SweepInformation(DesignSceneProjection scene)
    {
        var text = "";
        foreach (var sweep in scene.RotationSweeps)
        {
            text += $"\n设备路径 [{string.Join(",", sweep.NestedItemIndices)}]"
                + $" · 转动范围 {Computed(sweep.SweptCells, cells => $"{cells.Length} 格")}"
                + $" · 冲突 {Computed(sweep.ConflictCells, cells => cells.IsEmpty ? "无" : string.Join("、", cells.Select(PositionText)))}";
            foreach (var reason in (sweep.SweptCells as ComputedValue<ImmutableArray<GridPosition>>.Unknown)?.Reasons ?? [])
                text += $"\n范围未知：{reason.Message}";
            foreach (var reason in (sweep.ConflictCells as ComputedValue<ImmutableArray<GridPosition>>.Unknown)?.Reasons ?? [])
                text += $"\n冲突未知：{reason.Message}";
        }
        return text;
    }
    private void SaveBlueprint()
    {
        var dialog = new AcceptDialog { Title = "保存有效蓝图", OkButtonText = "保存并退出" }; AddChild(dialog); var body = new VBoxContainer(); dialog.AddChild(body);
        var target = Choice(body, _service.SaveTarget is BlueprintSaveTarget.Overwrite ? ["覆盖当前蓝图", "另存到本局", "另存到用户库"] : ["另存到本局", "另存到用户库"]);
        dialog.Confirmed += () =>
        {
            var overwrite = _service.SaveTarget is BlueprintSaveTarget.Overwrite;
            var selectedTarget = overwrite && target.Selected == 0
                ? _service.SaveTarget!
                : new BlueprintSaveTarget.New(target.Selected == (overwrite ? 1 : 0) ? BlueprintLibrary.Local : BlueprintLibrary.User);
            _service.SaveBlueprint(selectedTarget);
            dialog.QueueFree();
        };
        dialog.PopupCentered(new(380, 160));
    }
    public override void _ExitTree()
    {
        _service.BlueprintSaved -= ReturnToLibrary;
        _scene.Dispose();
    }
}
