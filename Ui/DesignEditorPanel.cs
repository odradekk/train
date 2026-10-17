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
    private PanelContainer _leave = null!;
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
        var top = new PanelContainer(); top.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide); AddChild(top);
        var bar = new HFlowContainer(); top.AddChild(bar);
        _name = new LineEdit { CustomMinimumSize = new(160, 36), PlaceholderText = "名称" }; bar.AddChild(_name);
        _description = new LineEdit { CustomMinimumSize = new(180, 36), PlaceholderText = "说明" }; bar.AddChild(_description);
        Add(bar, "Properties", "更新名称说明", () =>
        {
            if (_service.State is { } state)
                Preview(new EditOperation.Properties(new(_name.Text, _description.Text),
                    (_scene.EnteredDraft ?? state.Snapshot.Draft).Specification));
        });
        Add(bar, "Undo", "撤销", _service.Undo); Add(bar, "Redo", "重做", _service.Redo);
        Add(bar, "SaveDraft", "保存草稿", _service.SaveDraft); Add(bar, "SaveBlueprint", "保存蓝图…", SaveBlueprint);
        Add(bar, "ExitEditor", "退出", () => _service.RequestClose(() => { }));
        var left = Panel("Tools", "体素与工具", new(8, 64), 270, out var tools);
        _voxel = Choice(tools, []); _voxel.ItemSelected += _ => RefreshShapes();
        _shape = Choice(tools, []); _orientation = Choice(tools, []);
        _paint = new ColorPickerButton { Color = Colors.White, CustomMinimumSize = new(240, 32) }; tools.AddChild(_paint);
        var colors = new HBoxContainer(); tools.AddChild(colors);
        foreach (var color in new[] { "#d8d3c4", "#a23d32", "#386b83", "#d5ae4e", "#343b40" })
        { var swatch = new Button { Text = "■", Modulate = new Color(color) }; swatch.Pressed += () => _paint.Color = new Color(color); colors.AddChild(swatch); }
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
        var right = Panel("Inspector", "层级与精确参数", new(1000, 64), 290, out var inspector);
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
        Add(inspector, "EditSpecification", "车辆／建筑规格…", EditSpecification);
        Add(inspector, "Preview", "预览精确参数", () => PreviewCells());
        Add(inspector, "Apply", "应用预览", Apply);
        Add(inspector, "SelectBox", "框选区域", () => _service.Select(new EditSelection.Box(Bounds())));
        Add(inspector, "Move", "移动选区", () => Preview(new EditOperation.MoveSelection(At())));
        Add(inspector, "Copy", "复制选区", () => Preview(new EditOperation.CopySelection(At())));
        Add(inspector, "Rotate", "选区旋转 90°", () => Preview(new EditOperation.RotateSelection((GridAxis)_axis.Selected, At(), 1)));
        Add(inspector, "Remove", "删除选区", () => Preview(new EditOperation.RemoveSelection()));
        Add(inspector, "Paint", "选区涂装", () => Preview(new EditOperation.PaintSelection(Paint())));
        Add(inspector, "Shape", "选区形状", () => { if (Shape() is { } shape) Preview(new EditOperation.ShapeSelection(shape)); });
        var bottom = new PanelContainer(); bottom.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide); bottom.OffsetTop = -220; AddChild(bottom);
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
                + string.Join("；", p.Conflicts.Select(c => $"{c.Cell}：{EditorService.Describe(c.Problem)}"))
            : "预览不可用";
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
        Visible = _service.IsOpen; _scene.Process(delta);
        if (!Visible) return;
        _leave.Visible = _service.Leaving; if (_service.Leaving) _leave.MoveToFront();
        _status.Text = (_service.State is { } active
            ? $"层级 {active.EnteredObject?.Value.ToString() ?? "根"} · 已选 {active.Selection.Length} · " : "")
            + (_service.Message ?? "")
            + (_service.SceneUnavailable ? " · 场景不可用，可保存、退出或重试" : "")
            + (_service.Host.GetSaveStatus()?.Frozen == true ? " · 核心已冻结；本局草稿仅保留到离开本局，无法写入存档；用户库草稿可写盘；有效蓝图暂不可保存" : "")
            + (_service.IsDirty ? " · 未保存修改" : "");
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
        _elements.Clear();
        _elementIds.Clear();
        foreach (var item in state.Elements)
        {
            _elementIds.Add(item.Id);
            var prefix = item.Parent is { } parent ? $"↳ {parent.Value} / " : "";
            _elements.AddItem($"{prefix}{item.Id.Value} · {item.Kind} {item.At}{(item.Locked ? " [锁定]" : "")}");
        }
        ShowEvaluation(state);
    }
    private void RefreshPalette()
    {
        _voxels.Clear(); _voxel.Clear();
        foreach (var item in (_service.FixedContent?.Voxels ?? []).Where(item => item.Good is not null))
        {
            _voxels.Add(item);
            var name = _service.FixedContent?.FindVoxelDisplay(item.Definition) is ContentLookup<VoxelDisplayProjection>.Found display ? display.Value.Display.Name : item.Definition.Value;
            _voxel.AddItem($"{name} · Value {item.Good!.BaseValue.Value} · {item.Good.MassTons:0.##} t");
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
        foreach (var orientation in orientations) { _allowedOrientations.Add(orientation); _orientation.AddItem(orientation.ToString()); }
    }
    private void ShowEvaluation(EditState state)
    {
        foreach (var child in _diagnostics.GetChildren()) { _diagnostics.RemoveChild(child); child.QueueFree(); }
        _shownEvaluation = state.CurrentEvaluation;
        _shownFrameDiagnostics = _service.FrameDiagnostics;
        foreach (var problem in _shownFrameDiagnostics)
            _diagnostics.AddChild(UiStyle.Label(EditorService.Describe(problem)));
        IEnumerable<ContentDiagnostic> diagnostics = state.EvaluationDiagnostics;
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
                row.AddChild(UiStyle.Label($"缺失定义 {locked.Reference} · 所属包 {locked.Package.Value} · 受阻操作：{string.Join("、", locked.BlockedOperations)}"));
                Add(row, "LocateLockedElement", "定位锁定项", () => _service.Locate(locked.Element));
            }
        }
        diagnostics = diagnostics.Concat(state.CurrentEvaluation switch
        {
            DesignEvaluation.Complete complete => complete.Diagnostics,
            DesignEvaluation.Partial partial => partial.Diagnostics,
            DesignEvaluation.Failed failed => failed.Diagnostics,
            _ => []
        });
        foreach (var diagnostic in diagnostics)
        {
            var row = new HBoxContainer(); _diagnostics.AddChild(row); row.AddChild(UiStyle.Label(EditorService.Describe(diagnostic)));
            if (diagnostic.Element is { } path) Add(row, "LocateDiagnostic", "定位", () => _service.Locate(path));
        }
    }
    private static string Computed<T>(ComputedValue<T> value, Func<T, string> format) where T : notnull => value is ComputedValue<T>.Known known ? format(known.Value) : "未知";
    private static string Bom(ImmutableArray<GoodsQuantity> values) => string.Join("、", values.Select(item => $"{item.Good} × {item.Quantity}"));
    private static string BoundsText(GridBounds? bounds) => bounds is null ? "未知" :
        $"X {bounds.Minimum.X}～{bounds.Maximum.X}，Y {bounds.Minimum.Y}～{bounds.Maximum.Y}，Z {bounds.Minimum.Z}～{bounds.Maximum.Z}";
    private static string CompleteInformation(DesignEvaluation.Complete evaluation)
    {
        var projection = evaluation.Core?.Design;
        var scene = evaluation.Scene;
        var text = $"{(evaluation.IsValid ? "完整合法" : "设计不合法")} · 占格范围 {BoundsText(scene.OccupiedBounds)} · 包络 {BoundsText(scene.AllowedEnvelope)}";
        if (projection is not null)
        {
            text += $"\n质量 {projection.MassTons:0.###} t · 结构耐久上限 {projection.MaxStructureDurability} · 总 Value {projection.StructuralBaseValue.Value}";
            text += "\nBOM：" + Bom(projection.BillOfMaterials);
            text += "\n部件：" + string.Join("；", projection.Parts.Select(part => $"{part.Id.Value} {part.Definition}：{string.Join("、", part.Capabilities)}"));
        }
        if (!scene.OccupancyComplete) text += "\n占格未知，不能确认完整冲突检查。";
        return text;
    }
    private static string SweepInformation(DesignSceneProjection scene)
    {
        var text = "";
        foreach (var sweep in scene.RotationSweeps)
        {
            text += $"\n设备路径 [{string.Join(",", sweep.NestedItemIndices)}]"
                + $" · 转动范围 {Computed(sweep.SweptCells, cells => $"{cells.Length} 格")}"
                + $" · 冲突 {Computed(sweep.ConflictCells, cells => cells.IsEmpty ? "无" : string.Join("、", cells))}";
            foreach (var reason in (sweep.SweptCells as ComputedValue<ImmutableArray<GridPosition>>.Unknown)?.Reasons ?? [])
                text += $"\n范围未知：{reason.Code} · 包 {reason.Source?.Package.Value ?? "未知"} · {reason.Message}";
            foreach (var reason in (sweep.ConflictCells as ComputedValue<ImmutableArray<GridPosition>>.Unknown)?.Reasons ?? [])
                text += $"\n冲突未知：{reason.Code} · 包 {reason.Source?.Package.Value ?? "未知"} · {reason.Message}";
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
