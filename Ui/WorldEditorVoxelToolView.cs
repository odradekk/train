using Godot;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

/// <summary>Controls and visualization for voxel cells, brush strokes and boxes.</summary>
public partial class WorldEditorVoxelToolView : Node3D
{
    private VoxelDevelopmentSession _session = null!;
    private Camera3D _camera = null!;
    private Control _parameters = null!;
    private Label _preview = null!;
    private MeshInstance3D _grid = null!;
    private MultiMeshInstance3D _cells = null!;
    private Label3D _axes = null!;
    private bool _syncing;
    private Action? _afterDraft;
    private int _gridHeight = int.MinValue;
    private WorldVoxelCoord _gridCenter;
    private IReadOnlyList<VoxelDefinitionId> _definitions = Array.Empty<VoxelDefinitionId>();
    private IReadOnlyList<VoxelOrientation> _orientations = Array.Empty<VoxelOrientation>();
    private string _lastResult = "";
    private VoxelRegionPreview? _presentedRegion;

    public WorldEditorVoxelTool State { get; private set; } = null!;
    internal Button SelectToolButton { get; private set; } = null!;
    internal Button VoxelToolButton { get; private set; } = null!;
    internal OptionButton ModeInput { get; private set; } = null!;
    internal SpinBox BrushSizeInput { get; private set; } = null!;
    internal SpinBox BoxHeightInput { get; private set; } = null!;
    internal SpinBox HeightInput { get; private set; } = null!;
    internal OptionButton DefinitionInput { get; private set; } = null!;
    internal OptionButton OrientationInput { get; private set; } = null!;
    internal OptionButton OperationInput { get; private set; } = null!;
    internal SpinBox[] CoordinateInputs { get; private set; } = null!;
    internal SpinBox[] BoxCornerInputs { get; private set; } = null!;
    internal Button CommitButton { get; private set; } = null!;
    internal Button DiscardButton { get; private set; } = null!;
    internal ConfirmationDialog DraftPrompt { get; private set; } = null!;
    internal ConfirmationDialog LargeEditPrompt { get; private set; } = null!;
    internal Button DraftCommitButton { get; private set; } = null!;
    internal Button DraftDiscardButton { get; private set; } = null!;

    internal void Initialize(VoxelDevelopmentSession session, Camera3D camera, VBoxContainer tools, VBoxContainer inspector)
    {
        _session = session;
        _camera = camera;
        State = new WorldEditorVoxelTool(session);
        var switches = new HBoxContainer();
        tools.AddChild(switches);
        SelectToolButton = Button(switches, "选择", () => RequestChange(() => State.SetActive(false)));
        VoxelToolButton = Button(switches, "体素", () => RequestChange(() => State.SetActive(true)));
        tools.MoveChild(switches, 1);
        var mode = new HBoxContainer();
        tools.AddChild(mode);
        mode.AddChild(new Label { Text = "体素工具" });
        ModeInput = new OptionButton();
        ModeInput.AddItem("单格");
        ModeInput.AddItem("刷子");
        ModeInput.AddItem("盒选");
        mode.AddChild(ModeInput);
        ModeInput.ItemSelected += index =>
        {
            if (_syncing) return;
            if (!State.SetMode((WorldEditorVoxelMode)index))
                RequestChange(() => State.SetMode((WorldEditorVoxelMode)index));
            Present();
        };
        BrushSizeInput = Spin(mode, 1, VoxelRegionLimits.MaxBrushSize, 1);
        BrushSizeInput.Prefix = "N ";
        BrushSizeInput.ValueChanged += value =>
        {
            if (_syncing || State.SetBrushSize((int)value)) return;
            RequestChange(() => State.SetBrushSize((int)value));
        };
        BoxHeightInput = Spin(mode, 1, VoxelRegionLimits.MaxCells, 1);
        BoxHeightInput.Prefix = "高 ";
        BoxHeightInput.ValueChanged += value =>
        {
            if (_syncing) return;
            State.SetBoxHeight((int)value);
            Present();
        };
        tools.MoveChild(mode, 2);
        var plane = new HBoxContainer();
        tools.AddChild(plane);
        plane.AddChild(new Label { Text = "水平工作平面 Y" });
        HeightInput = Spin(plane, 0, session.Identity.Bounds.SizeY - 1, 0);
        HeightInput.ValueChanged += value =>
        {
            if (_syncing)
                return;
            if (!State.SetHeight((int)value))
                RequestChange(() => State.SetHeight((int)value));
            Present();
        };
        tools.MoveChild(plane, 2);

        var box = new VBoxContainer();
        _parameters = box;
        inspector.AddChild(box);
        inspector.MoveChild(box, 0);
        box.AddChild(new Label { Text = "体素范围 · 世界坐标" });
        var coordinates = new HBoxContainer();
        box.AddChild(coordinates);
        CoordinateInputs = Enumerable.Range(0, 3).Select(_ => Spin(coordinates, -100000, 100000, 0)).ToArray();
        foreach (var input in CoordinateInputs)
            input.ValueChanged += _ => ExactInput();
        var secondCorner = new HBoxContainer();
        box.AddChild(secondCorner);
        BoxCornerInputs = Enumerable.Range(0, 3).Select(_ => Spin(secondCorner, -100000, 100000, 0)).ToArray();
        BoxCornerInputs[1].Editable = false;
        foreach (var input in BoxCornerInputs)
            input.ValueChanged += _ => ExactInput();
        DefinitionInput = new OptionButton();
        OrientationInput = new OptionButton();
        OperationInput = new OptionButton();
        OperationInput.AddItem("添加");
        OperationInput.AddItem("移除");
        OperationInput.AddItem("替换");
        box.AddChild(OperationInput);
        box.AddChild(DefinitionInput);
        box.AddChild(OrientationInput);
        DefinitionInput.ItemSelected += index =>
        {
            if (_syncing || _session.IsChangingWorld || index < 0 || index >= _definitions.Count)
                return;
            State.SetSettings(_definitions[(int)index], VoxelOrientation.Identity, State.Operation);
            Present();
        };
        OrientationInput.ItemSelected += _ => SettingsInput();
        OperationInput.ItemSelected += _ => SettingsInput();
        _preview = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_preview);
        var actions = new HBoxContainer();
        box.AddChild(actions);
        CommitButton = Button(actions, "提交草稿", Commit);
        DiscardButton = Button(actions, "放弃草稿", () => { State.Discard(); Present(); });
        DraftPrompt = new ConfirmationDialog { Title = "先处理体素草稿", DialogText = "切换工具、层级或目标前，请提交或放弃当前草稿。", OkButtonText = "提交草稿", CancelButtonText = "继续编辑" };
        AddChild(DraftPrompt);
        DraftCommitButton = DraftPrompt.GetOkButton();
        DraftDiscardButton = DraftPrompt.AddButton("放弃草稿", true, "discard");
        DraftPrompt.Confirmed += () => CompleteDraft(discard: false);
        DraftPrompt.CustomAction += action => { if (action == "discard") CompleteDraft(discard: true); };
        DraftPrompt.Canceled += () => _afterDraft = null;
        LargeEditPrompt = new ConfirmationDialog { Title = "确认大范围编辑", OkButtonText = "确认提交", CancelButtonText = "继续编辑" };
        AddChild(LargeEditPrompt);
        LargeEditPrompt.Confirmed += () =>
        {
            LargeEditPrompt.Hide();
            HandleSubmission(State.Commit(confirmLargeEdit: true));
        };
        LargeEditPrompt.Canceled += () => _afterDraft = null;
        CreateMeshes();
        Present();
    }

    public override void _ExitTree() => State?.Dispose();

    internal void RequestChange(Action action)
    {
        if (_session.IsChangingWorld)
            return;
        if (!State.HasDraft)
        {
            action();
            Present();
            return;
        }
        _afterDraft = action;
        DraftPrompt.PopupCentered(new Vector2I(480, 150));
    }

    private void CompleteDraft(bool discard)
    {
        DraftPrompt.Hide();
        if (discard)
            State.Discard();
        else
            Commit();
        if (State.HasDraft)
        {
            if (!LargeEditPrompt.Visible)
                DraftPrompt.PopupCentered();
            return;
        }
        var next = _afterDraft;
        _afterDraft = null;
        next?.Invoke();
        Present();
    }

    internal void Commit()
    {
        if (!HasContentSelection && State.Operation != VoxelCellOperation.Remove)
            return;
        HandleSubmission(State.Commit());
    }

    private void HandleSubmission(WorldEditSubmission? result)
    {
        if (result is null)
            return;
        _lastResult = result.Result is VoxelCellSetResult cells
            ? Describe(cells) + (cells.Conflicts.Count == 0 ? "" :
                $" · 冲突 {cells.Conflicts.Count} 格\n" + string.Join('\n', cells.Conflicts.Take(6).Select(Describe)))
            : result.Status;
        if (result.Refusal == WorldEditRefusal.LargeEditConfirmationRequired)
        {
            var preview = State.RegionPreview;
            LargeEditPrompt.DialogText = $"将一次提交 {preview.AffectedCount} 格（跳过 {preview.SkippedCount} 格）。确认继续？";
            LargeEditPrompt.PopupCentered(new Vector2I(520, 170));
        }
        if (!State.HasDraft && _afterDraft is { } next)
        {
            _afterDraft = null;
            next();
        }
        Present();
    }

    private void ExactInput()
    {
        if (_syncing || _session.IsChangingWorld || (!HasContentSelection && State.Operation != VoxelCellOperation.Remove))
            return;
        var first = new WorldVoxelCoord((int)CoordinateInputs[0].Value, (int)CoordinateInputs[1].Value, (int)CoordinateInputs[2].Value);
        if (State.Mode == WorldEditorVoxelMode.Box)
            State.SetBoxExact(first,
                new WorldVoxelCoord((int)BoxCornerInputs[0].Value, first.Y, (int)BoxCornerInputs[2].Value), State.BoxHeight,
                State.DefinitionId, State.Orientation, State.Operation);
        else
            State.SetExact(first, State.DefinitionId, State.Orientation, State.Operation);
        Present();
    }

    private bool HasContentSelection => DefinitionInput.Selected >= 0 && DefinitionInput.Selected < _definitions.Count &&
        OrientationInput.Selected >= 0 && OrientationInput.Selected < _orientations.Count;

    private void SettingsInput()
    {
        if (_syncing || _session.IsChangingWorld || OperationInput.Selected < 0)
            return;
        var operation = (VoxelCellOperation)OperationInput.Selected;
        if (!HasContentSelection && operation != VoxelCellOperation.Remove)
            return;
        State.SetSettings(operation == VoxelCellOperation.Remove ? State.DefinitionId : _definitions[DefinitionInput.Selected],
            operation == VoxelCellOperation.Remove ? State.Orientation : _orientations[OrientationInput.Selected], operation);
        Present();
    }

    /// <summary>The chosen integer Y plane is targetable even when no world collider exists.</summary>
    internal bool HandlePointer(Vector2 screen, WorldPointerAction action)
    {
        if (!State.Active)
            return false;
        if (_session.IsChangingWorld || DraftPrompt.Visible || LargeEditPrompt.Visible)
            return true;
        var origin = _camera.ProjectRayOrigin(screen);
        var direction = _camera.ProjectRayNormal(screen);
        if (Math.Abs(direction.Y) < 1e-7)
            return true;
        var distance = (State.Height - origin.Y) / direction.Y;
        if (distance < 0)
            return true;
        var point = origin + direction * distance;
        var target = new WorldVoxelCoord((int)Math.Floor(point.X), State.Height, (int)Math.Floor(point.Z));
        if (State.Mode == WorldEditorVoxelMode.Cell && action == WorldPointerAction.LeftPressed &&
            State.HasDraft && State.Position != target)
        {
            RequestChange(() => State.Hover(target));
            return true;
        }
        switch (State.Mode, action)
        {
            case (WorldEditorVoxelMode.Cell, WorldPointerAction.Move):
                State.Hover(target);
                break;
            case (WorldEditorVoxelMode.Cell, WorldPointerAction.LeftPressed):
                State.Hover(target);
                Commit();
                break;
            case (WorldEditorVoxelMode.Brush, WorldPointerAction.LeftPressed):
                State.BeginStroke(target);
                break;
            case (WorldEditorVoxelMode.Brush, WorldPointerAction.Move) when State.StrokeInProgress:
                State.ExtendStroke(target);
                break;
            case (WorldEditorVoxelMode.Brush, WorldPointerAction.Move):
                State.Hover(target);
                break;
            case (WorldEditorVoxelMode.Brush, WorldPointerAction.LeftReleased):
                HandleSubmission(State.EndStroke());
                break;
            case (WorldEditorVoxelMode.Box, WorldPointerAction.Move):
                State.Hover(target);
                break;
            case (WorldEditorVoxelMode.Box, WorldPointerAction.LeftPressed):
                State.BoxPoint(target);
                break;
        }
        Present();
        return true;
    }

    internal void InterruptPointer()
    {
        if (State?.StrokeInProgress == true)
        {
            State.InterruptStroke();
            Present();
        }
    }

    internal void Present()
    {
        if (State is null)
            return;
        if (State.StrokeInProgress && !Input.IsMouseButtonPressed(MouseButton.Left))
            State.InterruptStroke();
        _syncing = true;
        try
        {
            _parameters.Visible = State.Active;
            SelectToolButton.ButtonPressed = !State.Active;
            VoxelToolButton.ButtonPressed = State.Active;
            ModeInput.Select((int)State.Mode);
            BrushSizeInput.SetValueNoSignal(State.BrushSize);
            BoxHeightInput.SetValueNoSignal(State.BoxHeight);
            BrushSizeInput.Visible = State.Mode == WorldEditorVoxelMode.Brush;
            BoxHeightInput.Visible = State.Mode == WorldEditorVoxelMode.Box;
            HeightInput.SetValueNoSignal(State.Height);
            HeightInput.MaxValue = _session.Identity.Bounds.SizeY - 1;
            var ids = State.Definitions;
            if (!ids.SequenceEqual(_definitions))
            {
                _definitions = ids;
                DefinitionInput.Clear();
                foreach (var id in ids)
                    DefinitionInput.AddItem(id.Value);
            }
            var selected = _definitions.ToList().IndexOf(State.DefinitionId);
            DefinitionInput.Select(selected);
            var directions = State.Orientations;
            if (!directions.SequenceEqual(_orientations))
            {
                _orientations = directions;
                OrientationInput.Clear();
                foreach (var direction in directions)
                    OrientationInput.AddItem(direction.ToString());
            }
            OrientationInput.Select(_orientations.ToList().IndexOf(State.Orientation));
            OperationInput.Select((int)State.Operation);
            CoordinateInputs[0].SetValueNoSignal(State.Position.X);
            CoordinateInputs[1].SetValueNoSignal(State.Position.Y);
            CoordinateInputs[2].SetValueNoSignal(State.Position.Z);
            BoxCornerInputs[0].SetValueNoSignal(State.BoxCorner.X);
            BoxCornerInputs[1].SetValueNoSignal(State.BoxCorner.Y);
            BoxCornerInputs[2].SetValueNoSignal(State.BoxCorner.Z);
            foreach (var input in BoxCornerInputs)
                input.Visible = State.Mode == WorldEditorVoxelMode.Box;
            var canPreview = State.DefinitionId.Namespace is not null || State.Operation == VoxelCellOperation.Remove;
            var region = State.Mode == WorldEditorVoxelMode.Cell || !canPreview ? null : State.RegionPreview;
            var preview = !canPreview ? new VoxelCellSetResult(VoxelCellSetStatus.UnknownDefinition, State.Position)
                : region?.Validation ?? State.Preview;
            var regionText = region is null ? "" :
                $"\n范围：{region.Range.Min} → {region.Range.Max} · 共 {region.Range.Count} 格 · " +
                $"影响 {region.AffectedCount} · 跳过 {region.SkippedCount} · 冲突 {region.Validation.Conflicts.Count}" +
                (region.Validation.Conflicts.Count == 0 ? "" : "\n" + string.Join('\n', region.Validation.Conflicts.Select(Describe)));
            _preview.Text = $"世界格 {State.Position.X}, {State.Position.Y}, {State.Position.Z} · Y 平面 {State.Height}\n" +
                "轴向：+X 红线 · +Z 蓝线 · +Y 向上\n" +
                $"{(State.HasDraft ? "草稿（保存不包含）" : "悬停预览")} · {Describe(preview)}{regionText}" +
                (_lastResult.Length > 0 ? "\n上次提交：" + _lastResult : "");
            DiscardButton.Disabled = !State.HasDraft;
            CommitButton.Disabled = (!HasContentSelection && State.Operation != VoxelCellOperation.Remove) || _session.IsChangingWorld;
            UpdateCells(preview, region);
            _cells.Visible = State.Active;
            _grid.Visible = State.Active;
            _axes.Visible = State.Active;
            UpdateGrid();
        }
        finally { _syncing = false; }
    }

    internal static string Describe(VoxelCellSetResult result)
    {
        var reason = result.Status switch
        {
            VoxelCellSetStatus.Valid => "可提交",
            VoxelCellSetStatus.Applied => "已提交",
            VoxelCellSetStatus.Occupied => "添加目标已有体素",
            VoxelCellSetStatus.Empty => "移除或替换目标为空",
            VoxelCellSetStatus.OutOfBounds => "超出世界范围",
            VoxelCellSetStatus.UnknownDefinition => "定义不在当前有效内容中",
            VoxelCellSetStatus.DisallowedOrientation => "定义不允许该朝向",
            VoxelCellSetStatus.NoChange => "定义和朝向未变化",
            VoxelCellSetStatus.NoTargets => "范围内没有适用的格",
            VoxelCellSetStatus.RangeTooLarge => $"覆盖格超过暂定上限 {VoxelRegionLimits.MaxCells}，请缩小范围",
            VoxelCellSetStatus.InvalidRange => "范围参数无效，高度须为正整数且不能溢出坐标",
            VoxelCellSetStatus.RailOccupied => result.OwnerKind == VoxelCellOwnerKind.None ? "铁路体素（未登记轨段）" : "属于铁路",
            VoxelCellSetStatus.VehicleOccupied => "车辆实际占用",
            _ => result.Status.ToString()
        };
        return reason + (result.OwnerKind != VoxelCellOwnerKind.None ? $" · 对象 {result.OwnerId.Value}" : "") +
            (result.OwnerKind == VoxelCellOwnerKind.VehiclePart ? $" · 零件 {result.PartId.Value}" : "");
    }

    private static string Describe(VoxelCellConflict conflict) =>
        $"({conflict.Position.X}, {conflict.Position.Y}, {conflict.Position.Z}) · " +
        Describe(new VoxelCellSetResult(conflict.Status, conflict.Position, conflict.OwnerKind, conflict.OwnerId, conflict.PartId));

    private void CreateMeshes()
    {
        _grid = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        var cellMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            VertexColorUseAsAlbedo = true
        };
        _cells = new MultiMeshInstance3D
        {
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = true,
                Mesh = new BoxMesh { Size = Vector3.One * 1.02f, Material = cellMaterial }
            }
        };
        _axes = new Label3D { Text = "X →  ·  Z ↓  ·  Y ↑", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 32 };
        AddChild(_grid);
        AddChild(_cells);
        AddChild(_axes);
    }

    private void UpdateGrid()
    {
        if (_gridHeight == State.Height && _gridCenter == State.Position)
            return;
        _gridHeight = State.Height;
        _gridCenter = State.Position;
        var bounds = _session.Identity.Bounds;
        // Draw at most 64 cells around the preview rather than building a mesh for the entire finite world.
        var x0 = Math.Clamp(State.Position.X - 32, 0, Math.Max(0, bounds.SizeX - 64));
        var z0 = Math.Clamp(State.Position.Z - 32, 0, Math.Max(0, bounds.SizeZ - 64));
        var x1 = Math.Min(bounds.SizeX, x0 + 64);
        var z1 = Math.Min(bounds.SizeZ, z0 + 64);
        var mesh = new ImmediateMesh();
        var material = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, material);
        void Line(Vector3 from, Vector3 to, Color color)
        {
            mesh.SurfaceSetColor(color);
            mesh.SurfaceAddVertex(from);
            mesh.SurfaceAddVertex(to);
        }
        var y = State.Height + 0.005f;
        for (var x = x0; x <= x1; x++)
            Line(new Vector3(x, y, z0), new Vector3(x, y, z1), new Color(0.85f, 0.85f, 0.9f, 0.45f));
        for (var z = z0; z <= z1; z++)
            Line(new Vector3(x0, y, z), new Vector3(x1, y, z), new Color(0.85f, 0.85f, 0.9f, 0.45f));
        Line(new Vector3(x0, y, z0), new Vector3(x1, y, z0), Colors.Red);
        Line(new Vector3(x0, y, z0), new Vector3(x0, y, z1), Colors.DodgerBlue);
        mesh.SurfaceEnd();
        _grid.Mesh = mesh;
        _axes.Position = new Vector3(x0, y + 0.4f, z0);
    }

    private void UpdateCells(VoxelCellSetResult cellPreview, VoxelRegionPreview? region)
    {
        if (region is not null && ReferenceEquals(region, _presentedRegion))
            return;
        _presentedRegion = region;
        var positions = region?.Range.Cells ?? new[] { State.Position };
        var conflicts = region?.Validation.Conflicts.ToDictionary(item => item.Position) ??
            (cellPreview.Succeeded ? new Dictionary<WorldVoxelCoord, VoxelCellConflict>() :
                new Dictionary<WorldVoxelCoord, VoxelCellConflict>
                {
                    [State.Position] = new(cellPreview.Status, cellPreview.Position, cellPreview.OwnerKind, cellPreview.OwnerId, cellPreview.PartId)
                });
        var affected = region?.Edit.Targets.Select(target => target.Position).ToHashSet() ?? new HashSet<WorldVoxelCoord> { State.Position };
        var multimesh = _cells.Multimesh;
        multimesh.InstanceCount = positions.Count;
        for (var index = 0; index < positions.Count; index++)
        {
            var position = positions[index];
            multimesh.SetInstanceTransform(index, new Transform3D(Basis.Identity,
                new Vector3(position.X + 0.5f, position.Y + 0.5f, position.Z + 0.5f)));
            var color = conflicts.ContainsKey(position) ? new Color(1, 0.2f, 0.15f, 0.4f)
                : affected.Contains(position) ? new Color(0.2f, 1, 0.4f, 0.35f)
                : new Color(0.6f, 0.65f, 0.7f, 0.25f);
            multimesh.SetInstanceColor(index, color);
        }
    }

    private static Button Button(Container box, string text, Action action)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.Pressed += action;
        box.AddChild(button);
        return button;
    }

    private static SpinBox Spin(Container box, double min, double max, double value)
    {
        var input = new SpinBox { MinValue = min, MaxValue = max, Value = value, Rounded = true,
            CustomMinimumSize = new Vector2(76, 0) };
        box.AddChild(input);
        return input;
    }
}
