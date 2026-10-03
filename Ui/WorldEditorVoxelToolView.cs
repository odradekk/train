using Godot;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

/// <summary>Controls and visualization for voxel cells, regions and bound batches.</summary>
public partial class WorldEditorVoxelToolView : Node3D
{
    private VoxelDevelopmentSession _session = null!;
    private Control _parameters = null!;
    private Label _preview = null!;
    private Label _title = null!;
    private MeshInstance3D _grid = null!;
    private MultiMeshInstance3D _cells = null!;
    private Label3D _axes = null!;
    private bool _syncing;
    private Action? _afterDraft;
    private int _gridLayer = int.MinValue;
    private int _gridAxis = -1;
    private WorldVoxelCoord _gridCenter;
    private WorldEditorWorldHit? _surface;
    private (VoxelDefinitionId Definition, VoxelOrientation Orientation, VoxelCollision Collision)? _surfaceContent;
    private bool _pointerTargetable;
    private IReadOnlyList<VoxelDefinitionId> _definitions = Array.Empty<VoxelDefinitionId>();
    private IReadOnlyList<VoxelOrientation> _orientations = Array.Empty<VoxelOrientation>();
    private string _lastResult = "";
    private string _inputNotice = "";
    private VoxelRegionPreview? _presentedRegion;

    public WorldEditorVoxelTool State { get; private set; } = null!;
    internal Button SelectToolButton { get; private set; } = null!;
    internal Button VoxelToolButton { get; private set; } = null!;
    internal SpinBox LayerInput { get; private set; } = null!;
    internal OptionButton PlaneInput { get; private set; } = null!;
    internal OptionButton ModeInput { get; private set; } = null!;
    internal SpinBox BrushSizeInput { get; private set; } = null!;
    internal SpinBox BoxHeightInput { get; private set; } = null!;
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

    internal void Initialize(VoxelDevelopmentSession session, VBoxContainer tools, VBoxContainer inspector)
    {
        _session = session;
        State = new WorldEditorVoxelTool(session);
        session.WorldReplaced += ResetTarget;
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
        ModeInput.AddItem("笔刷");
        ModeInput.AddItem("长方体");
        mode.AddChild(ModeInput);
        ModeInput.ItemSelected += index =>
        {
            if (_syncing) return;
            var selected = (WorldEditorVoxelMode)index;
            if (State.SetMode(selected))
                ClearTarget();
            else
                RequestChange(() => { if (State.SetMode(selected)) ClearTarget(); });
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
        PlaneInput = new OptionButton();
        PlaneInput.AddItem("水平 Y");
        PlaneInput.AddItem("竖直 X");
        PlaneInput.AddItem("竖直 Z");
        PlaneInput.AddItem("拾取世界体素表面");
        plane.AddChild(PlaneInput);
        PlaneInput.ItemSelected += index =>
        {
            if (_syncing)
                return;
            var mode = (VoxelWorkPlane)index;
            var layer = Math.Min(State.Layer, PlaneSize(mode) - 1);
            RequestChange(() => { if (State.SetPlane(mode, layer)) ClearTarget(); });
            Present();
        };
        LayerInput = Spin(plane, 0, session.Identity.Bounds.SizeY - 1, 0);
        LayerInput.ValueChanged += value =>
        {
            if (_syncing)
                return;
            RequestChange(() => { if (State.SetLayer((int)value)) ClearTarget(); });
            Present();
        };
        tools.MoveChild(plane, 2);

        var box = new VBoxContainer();
        _parameters = box;
        inspector.AddChild(box);
        inspector.MoveChild(box, 0);
        _title = new Label { Text = "单格体素 · 世界坐标" };
        box.AddChild(_title);
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
            ReportInput(State.SetSettings(_definitions[(int)index], VoxelOrientation.Identity, State.Operation));
            Present();
        };
        OrientationInput.ItemSelected += _ => SettingsInput();
        OperationInput.ItemSelected += _ => SettingsInput();
        _preview = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_preview);
        var actions = new HBoxContainer();
        box.AddChild(actions);
        CommitButton = Button(actions, "提交草稿", Commit);
        DiscardButton = Button(actions, "放弃草稿", Discard);
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

    public override void _ExitTree()
    {
        if (_session is not null)
            _session.WorldReplaced -= ResetTarget;
        State?.Dispose();
    }

    private void ClearTarget()
    {
        _surface = null;
        _surfaceContent = null;
        _pointerTargetable = false;
    }

    private void ResetTarget(WorldReplacement _)
    {
        ClearTarget();
        _gridLayer = int.MinValue;
        _lastResult = "";
        _afterDraft = null;
        DraftPrompt.Hide();
        Present();
    }

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
            Discard();
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
        CheckSurface();
        if (!_pointerTargetable && !State.HasDraft)
            return;
        _inputNotice = "";
        if (!HasContentSelection && State.Operation != VoxelCellOperation.Remove)
            return;
        HandleSubmission(State.Commit());
    }

    private void HandleSubmission(WorldEditSubmission? result)
    {
        if (result is null)
            return;
        _inputNotice = "";
        _lastResult = result.Result is VoxelCellSetResult cells
            ? Describe(cells, State.HasBatchDraft) + (cells.Conflicts.Count == 0 ? "" :
                $" · 冲突 {cells.Conflicts.Count} 格\n" + string.Join('\n',
                    cells.Conflicts.Take(VoxelRegionLimits.ConflictDisplayLimit).Select(Describe)))
            : result.Status;
        if (result.Refusal == WorldEditRefusal.LargeEditConfirmationRequired)
        {
            var preview = result.RegionPreview ?? throw new InvalidOperationException("Large region refusal omitted its preview.");
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

    private void Discard()
    {
        State.Discard();
        _inputNotice = "";
        Present();
    }

    private void ExactInput()
    {
        if (_syncing || (!HasContentSelection && State.Operation != VoxelCellOperation.Remove))
            return;
        var first = new WorldVoxelCoord((int)CoordinateInputs[0].Value, (int)CoordinateInputs[1].Value, (int)CoordinateInputs[2].Value);
        if (State.Mode == WorldEditorVoxelMode.Box && !State.HasBatchDraft)
            ReportInput(State.SetBoxExact(first,
                new WorldVoxelCoord((int)BoxCornerInputs[0].Value, first.Y, (int)BoxCornerInputs[2].Value), State.BoxHeight,
                State.DefinitionId, State.Orientation, State.Operation));
        else
            ReportInput(State.SetExact(first, State.DefinitionId, State.Orientation, State.Operation));
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
        ReportInput(State.SetSettings(operation == VoxelCellOperation.Remove ? State.DefinitionId : _definitions[DefinitionInput.Selected],
            operation == VoxelCellOperation.Remove ? State.Orientation : _orientations[OrientationInput.Selected], operation));
        if (!State.HasDraft && State.Plane == VoxelWorkPlane.Surface && _pointerTargetable &&
            _surface is { } hit)
            State.Hover(State.SurfaceTarget(hit.Cell, hit.AdjacentCell));
        Present();
    }

    internal bool InputNoticeVisible => _inputNotice.Length > 0;

    private void ReportInput(WorldEditorVoxelInputStatus status) => _inputNotice = status switch
    {
        WorldEditorVoxelInputStatus.WorldChanging => "世界变更中，参数输入未应用",
        WorldEditorVoxelInputStatus.BatchTargetsBound => "批量目标已绑定，坐标输入未应用；请放弃草稿后重新建立",
        WorldEditorVoxelInputStatus.BatchOperationBound => "批量操作已绑定，操作输入未应用；定义和朝向已应用；更换操作需放弃草稿后重新建立",
        WorldEditorVoxelInputStatus.StrokeInProgress => "笔刷正在采样，参数输入未应用；请结束笔画后修改",
        _ => ""
    };

    /// <summary>Planes target integer layers; surface addition uses the hit cell's outside neighbor.</summary>
    internal bool HandlePointer(WorldEditorPick pick, WorldPointerAction action, out ObservationVector? worldPoint)
    {
        worldPoint = null;
        if (!State.Active)
            return false;
        if (action == WorldPointerAction.LeftReleased)
        {
            if (State.Mode != WorldEditorVoxelMode.Brush || !State.StrokeInProgress)
                return false;
            HandleSubmission(State.EndStroke());
            Present(pointerInput: true);
            return true;
        }
        if (_session.IsChangingWorld || DraftPrompt.Visible || LargeEditPrompt.Visible)
            return true;
        WorldVoxelCoord target;
        WorldEditorWorldHit? surface = null;
        if (State.Plane == VoxelWorkPlane.Surface)
        {
            if (pick.WorldHit is not { } hit)
            {
                _pointerTargetable = false;
                Present(pointerInput: true);
                return true;
            }
            surface = hit;
            worldPoint = WorldEditorObservationView.From(hit.Position);
            _pointerTargetable = true;
            if (!State.HasDraft)
                RememberSurface(hit);
            target = State.SurfaceTarget(hit.Cell, hit.AdjacentCell);
        }
        else
        {
            var (_, origin, direction) = pick.Ray;
            if (!State.TryPlaneTarget(WorldEditorObservationView.From(origin), WorldEditorObservationView.From(direction), out target, out var point))
            {
                _pointerTargetable = false;
                Present(pointerInput: true);
                return true;
            }
            worldPoint = point;
            _pointerTargetable = true;
        }
        if (action == WorldPointerAction.LeftPressed && State.HasDraft &&
            (State.Mode != WorldEditorVoxelMode.Cell || State.Position != target) &&
            (State.Mode != WorldEditorVoxelMode.Box || !State.BoxCornerPending))
        {
            RequestChange(() =>
            {
                if (State.Mode == WorldEditorVoxelMode.Cell)
                {
                    State.Hover(target);
                    if (surface is not null) RememberSurface(surface);
                }
                else if (State.Mode == WorldEditorVoxelMode.Brush)
                    // The decision dialog is completed after the initiating button was released. Move the
                    // cursor only; a fresh physical press starts the replacement stroke.
                    State.Hover(target);
                else
                    State.BoxPoint(target);
            });
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
            case (WorldEditorVoxelMode.Box, WorldPointerAction.Move):
                State.Hover(target);
                break;
            case (WorldEditorVoxelMode.Box, WorldPointerAction.LeftPressed):
                State.BoxPoint(target);
                break;
        }
        Present(pointerInput: true);
        return true;
    }

    internal void InterruptPointer()
    {
        if (State?.StrokeInProgress == true)
        {
            State.InterruptDraftInput();
            Present();
        }
    }

    internal void Present(bool pointerInput = false)
    {
        if (State is null)
            return;
        CheckSurface();
        // Godot may swallow release when focus changes. EndInput normally interrupts immediately; this
        // frame check is the fallback for a release lost without a focus notification. It preserves the draft.
        if (!pointerInput && State.StrokeInProgress && !Input.IsMouseButtonPressed(MouseButton.Left))
            State.InterruptDraftInput();
        _syncing = true;
        try
        {
            _parameters.Visible = State.Active || State.HasBatchDraft;
            _title.Text = State.HasBatchDraft ? "批量体素 · 世界坐标" : "体素范围 · 世界坐标";
            CommitButton.Text = State.HasBatchDraft ? "提交批量" : "提交草稿";
            SelectToolButton.ButtonPressed = !State.Active;
            VoxelToolButton.ButtonPressed = State.Active;
            LayerInput.SetValueNoSignal(State.Layer);
            LayerInput.MaxValue = PlaneSize(State.Plane) - 1;
            LayerInput.Editable = State.Plane != VoxelWorkPlane.Surface;
            PlaneInput.Select((int)State.Plane);
            ModeInput.Select((int)State.Mode);
            BrushSizeInput.SetValueNoSignal(State.BrushSize);
            BoxHeightInput.SetValueNoSignal(State.BoxHeight);
            BrushSizeInput.Visible = !State.HasBatchDraft && State.Mode == WorldEditorVoxelMode.Brush;
            BoxHeightInput.Visible = !State.HasBatchDraft && State.Mode == WorldEditorVoxelMode.Box;
            PlaneInput.Disabled = State.Mode != WorldEditorVoxelMode.Cell;
            PlaneInput.TooltipText = State.Mode == WorldEditorVoxelMode.Cell ? "选择单格工具的工作平面" : "笔刷和长方体仅支持水平工作平面";
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
            OperationInput.Disabled = State.HasBatchDraft;
            foreach (var input in CoordinateInputs)
                input.Editable = !State.HasBatchDraft;
            CoordinateInputs[0].SetValueNoSignal(State.Position.X);
            CoordinateInputs[1].SetValueNoSignal(State.Position.Y);
            CoordinateInputs[2].SetValueNoSignal(State.Position.Z);
            BoxCornerInputs[0].SetValueNoSignal(State.BoxCorner.X);
            BoxCornerInputs[1].SetValueNoSignal(State.BoxCorner.Y);
            BoxCornerInputs[2].SetValueNoSignal(State.BoxCorner.Z);
            foreach (var input in BoxCornerInputs)
                input.Visible = !State.HasBatchDraft && State.Mode == WorldEditorVoxelMode.Box;
            var canPreview = State.DefinitionId.Namespace is not null || State.Operation == VoxelCellOperation.Remove;
            var region = State.HasBatchDraft || State.Mode == WorldEditorVoxelMode.Cell || !canPreview ? null : State.RegionPreview;
            var preview = !canPreview ? new VoxelCellSetResult(VoxelCellSetStatus.UnknownDefinition, State.Position)
                : region?.Validation ?? State.Preview;
            var planeText = State.Plane == VoxelWorkPlane.Surface ? "拾取表面 · 添加吸附到面外相邻格，移除／替换吸附到命中格"
                : $"{PlaneAxis(State.Plane)} 平面 {State.Layer}";
            var regionText = region is null ? "" :
                $"\n范围：{region.Region.Min} → {region.Region.Max} · 共 {region.Region.Count} 格 · " +
                $"影响 {region.AffectedCount} · 跳过 {region.SkippedCount} · 冲突 {region.Validation.Conflicts.Count}" +
                (region.Validation.Conflicts.Count == 0 ? "" : "\n" + string.Join('\n',
                    region.Validation.Conflicts.Take(VoxelRegionLimits.ConflictDisplayLimit).Select(Describe)));
            _preview.Text = (State.HasBatchDraft ? $"批量草稿 · {State.BatchTargets.Count} 格 · 目标和操作已绑定，选择变化不改写目标；修改坐标或操作需放弃后重新建立\n" :
                $"世界格 {State.Position.X}, {State.Position.Y}, {State.Position.Z} · {planeText}\n") +
                "整数格吸附 · +X 红线 · +Y 绿线 · +Z 蓝线\n" +
                (!_pointerTargetable && !State.HasDraft ? "光标没有可用目标，可填写精确坐标\n" : "") +
                $"{(State.HasDraft ? "草稿（保存不包含）" : "悬停预览")} · {Describe(preview, State.HasBatchDraft)}{regionText}" +
                (InputNoticeVisible ? "\n" + _inputNotice : "") +
                (_lastResult.Length > 0 ? "\n上次提交：" + _lastResult : "");
            DiscardButton.Disabled = !State.HasDraft;
            CommitButton.Disabled = (!HasContentSelection && State.Operation != VoxelCellOperation.Remove) || _session.IsChangingWorld ||
                (!_pointerTargetable && !State.HasDraft);
            UpdateCells(preview, region);
            _cells.Visible = (State.Active || State.HasBatchDraft) && (_pointerTargetable || State.HasDraft || State.HasBatchDraft);
            _grid.Visible = State.Active && (State.Plane != VoxelWorkPlane.Surface || _surface is not null) && (_pointerTargetable || State.HasDraft);
            _axes.Visible = _grid.Visible;
            UpdateGrid();
        }
        finally { _syncing = false; }
    }

    private void CheckSurface()
    {
        if (State.Plane == VoxelWorkPlane.Surface && !State.HasDraft && _surface is { } hit)
        {
            var voxel = _session.Query(hit.Cell).Voxel;
            if (voxel is not null && _surfaceContent is { } content && voxel.DefinitionId == content.Definition &&
                voxel.Orientation == content.Orientation && _session.CurrentVoxels?.TryGet(voxel.DefinitionId, out var entry) == true &&
                ReferenceEquals(content.Collision, entry.Definition.Collision) && content.Collision.Kind != VoxelCollisionKind.None)
                return;
            ClearTarget();
        }
    }

    private void RememberSurface(WorldEditorWorldHit hit)
    {
        _surface = hit;
        var voxel = _session.Query(hit.Cell).Voxel;
        _surfaceContent = voxel is not null && _session.CurrentVoxels?.TryGet(voxel.DefinitionId, out var entry) == true
            ? (voxel.DefinitionId, voxel.Orientation, entry.Definition.Collision) : null;
        CheckSurface();
    }

    internal static string Describe(VoxelCellSetResult result, bool batchDraft = false)
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
            VoxelCellSetStatus.ExpectedContentMismatch => $"目标 ({result.Position.X},{result.Position.Y},{result.Position.Z}) 已消失或身份已改变；" +
                (batchDraft ? "整次拒绝，草稿保留；请放弃草稿后重新选择并建立" : "未建立草稿，请重新选择目标"),
            VoxelCellSetStatus.VoxelDraftExists => "已有草稿，请先提交或放弃",
            VoxelCellSetStatus.WorldChanging => "世界变更中，不能建立草稿",
            VoxelCellSetStatus.UnsupportedOperation => "批量只支持移除或替换",
            VoxelCellSetStatus.NotWorldCells => "批量目标必须全部是世界格",
            VoxelCellSetStatus.NoTargets => "范围内没有适用的格",
            VoxelCellSetStatus.RegionTooLarge => $"覆盖格超过暂定上限 {VoxelRegionLimits.MaxCells}，请缩小范围",
            VoxelCellSetStatus.InvalidRegion => "范围参数无效，高度须为正整数且不能溢出坐标",
            VoxelCellSetStatus.RailOccupied => result.OwnerKind == VoxelCellOwnerKind.None ? "铁路体素（未登记轨段）" : "属于铁路",
            VoxelCellSetStatus.VehicleOccupied => "车辆实际占用",
            _ => result.Status.ToString()
        };
        return reason + (result.OwnerKind != VoxelCellOwnerKind.None ? $" · 对象 {result.OwnerId.Value}" : "") +
            (result.OwnerKind == VoxelCellOwnerKind.VehiclePart ? $" · 零件 {result.PartId.Value}" : "");
    }

    private static string Describe(VoxelCellConflict conflict) =>
        $"({conflict.Position.X}, {conflict.Position.Y}, {conflict.Position.Z}) · " +
        Describe(conflict.ToResult());

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
        var axis = VoxelWorkPlaneCoordinates.Axis(State.Plane);
        var layer = State.Layer;
        if (State.Plane == VoxelWorkPlane.Surface)
        {
            if (_surface is not { } hit)
                return;
            axis = hit.Normal.X != 0 ? 0 : hit.Normal.Y != 0 ? 1 : 2;
            layer = VoxelWorkPlaneCoordinates.Component(hit.Cell, axis.Value) + (hit.Normal[axis.Value] > 0 ? 1 : 0);
        }
        if (axis is not { } normalAxis || (_gridLayer == layer && _gridAxis == normalAxis && _gridCenter == State.Position))
            return;
        _gridLayer = layer;
        _gridAxis = normalAxis;
        _gridCenter = State.Position;
        var bounds = _session.Identity.Bounds;
        var sizes = new Vector3I(bounds.SizeX, bounds.SizeY, bounds.SizeZ);
        // Draw at most 64 cells around the preview rather than building a mesh for the entire finite world.
        var (uAxis, vAxis) = normalAxis switch { 0 => (2, 1), 1 => (0, 2), _ => (0, 1) };
        var u = VoxelWorkPlaneCoordinates.Component(State.Position, uAxis);
        var v = VoxelWorkPlaneCoordinates.Component(State.Position, vAxis);
        var sizeU = sizes[uAxis];
        var sizeV = sizes[vAxis];
        var u0 = Math.Clamp(u - 32, 0, Math.Max(0, sizeU - 64));
        var v0 = Math.Clamp(v - 32, 0, Math.Max(0, sizeV - 64));
        var u1 = Math.Min(sizeU, u0 + 64);
        var v1 = Math.Min(sizeV, v0 + 64);
        Vector3 Point(float a, float b)
        {
            var point = Vector3.Zero;
            point[normalAxis] = layer + 0.005f;
            point[uAxis] = a;
            point[vAxis] = b;
            return point;
        }
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
        for (var a = u0; a <= u1; a++)
            Line(Point(a, v0), Point(a, v1), new Color(0.85f, 0.85f, 0.9f, 0.45f));
        for (var b = v0; b <= v1; b++)
            Line(Point(u0, b), Point(u1, b), new Color(0.85f, 0.85f, 0.9f, 0.45f));
        var colors = new[] { Colors.Red, Colors.LimeGreen, Colors.DodgerBlue };
        Line(Point(u0, v0), Point(u1, v0), colors[uAxis]);
        Line(Point(u0, v0), Point(u0, v1), colors[vAxis]);
        mesh.SurfaceEnd();
        _grid.Mesh = mesh;
        _axes.Position = Point(u0, v0) + Vector3.Up * 0.4f;
        var start = Point(u0, v0);
        _axes.Text = $"世界网格 ({start.X:0}, {start.Y:0}, {start.Z:0})\n+{"XYZ"[uAxis]} → · +{"XYZ"[vAxis]} {(vAxis == 1 ? "↑" : "↓")}";
    }

    private int PlaneSize(VoxelWorkPlane plane)
    {
        var bounds = _session.Identity.Bounds;
        return new Vector3I(bounds.SizeX, bounds.SizeY, bounds.SizeZ)[VoxelWorkPlaneCoordinates.Axis(plane) ?? 1];
    }

    private static char PlaneAxis(VoxelWorkPlane plane) => "XYZ"[VoxelWorkPlaneCoordinates.Axis(plane) ?? 1];

    private void UpdateCells(VoxelCellSetResult cellPreview, VoxelRegionPreview? region)
    {
        if (region is not null && ReferenceEquals(region, _presentedRegion))
            return;
        _presentedRegion = region;
        var positions = State.HasBatchDraft ? State.BatchTargets : region?.Region.Cells ?? new[] { State.Position };
        var conflicts = region?.Validation.Conflicts.Select(item => item.Position).ToHashSet() ??
            (cellPreview.Succeeded ? new HashSet<WorldVoxelCoord>() : positions.ToHashSet());
        var affected = region?.Edit.Targets.Select(target => target.Position).ToHashSet() ?? positions.ToHashSet();
        var multimesh = _cells.Multimesh;
        multimesh.InstanceCount = positions.Count;
        for (var index = 0; index < positions.Count; index++)
        {
            var position = positions[index];
            multimesh.SetInstanceTransform(index, new Transform3D(Basis.Identity,
                new Vector3(position.X + 0.5f, position.Y + 0.5f, position.Z + 0.5f)));
            var color = conflicts.Contains(position) ? new Color(1, 0.2f, 0.15f, 0.4f)
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
