using Godot;
using Train.Domain.Commands;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

/// <summary>Controls and visualization for single cells and bound batches; edits always enter the session.</summary>
public partial class WorldEditorVoxelToolView : Node3D
{
    private VoxelDevelopmentSession _session = null!;
    private Camera3D _camera = null!;
    private Control _parameters = null!;
    private Label _preview = null!;
    private Label _title = null!;
    private MeshInstance3D _grid = null!;
    private MultiMeshInstance3D _cell = null!;
    private Label3D _axes = null!;
    private bool _syncing;
    private Action? _afterDraft;
    private int _gridHeight = int.MinValue;
    private WorldVoxelCoord _gridCenter;
    private IReadOnlyList<VoxelDefinitionId> _definitions = Array.Empty<VoxelDefinitionId>();
    private IReadOnlyList<VoxelOrientation> _orientations = Array.Empty<VoxelOrientation>();
    private string _lastResult = "";

    public WorldEditorVoxelTool State { get; private set; } = null!;
    internal Button SelectToolButton { get; private set; } = null!;
    internal Button VoxelToolButton { get; private set; } = null!;
    internal SpinBox HeightInput { get; private set; } = null!;
    internal OptionButton DefinitionInput { get; private set; } = null!;
    internal OptionButton OrientationInput { get; private set; } = null!;
    internal OptionButton OperationInput { get; private set; } = null!;
    internal SpinBox[] CoordinateInputs { get; private set; } = null!;
    internal Button CommitButton { get; private set; } = null!;
    internal Button DiscardButton { get; private set; } = null!;
    internal ConfirmationDialog DraftPrompt { get; private set; } = null!;
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
        VoxelToolButton = Button(switches, "单格体素", () => RequestChange(() => State.SetActive(true)));
        tools.MoveChild(switches, 1);
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
        _title = new Label { Text = "单格体素 · 世界坐标" };
        box.AddChild(_title);
        var coordinates = new HBoxContainer();
        box.AddChild(coordinates);
        CoordinateInputs = Enumerable.Range(0, 3).Select(_ => Spin(coordinates, -100000, 100000, 0)).ToArray();
        foreach (var input in CoordinateInputs)
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
        CommitButton = Button(actions, "提交单格", Commit);
        DiscardButton = Button(actions, "放弃草稿", () => { State.Discard(); Present(); });
        DraftPrompt = new ConfirmationDialog { Title = "先处理体素草稿", DialogText = "切换工具、层级或目标前，请提交或放弃当前草稿。", OkButtonText = "提交草稿", CancelButtonText = "继续编辑" };
        AddChild(DraftPrompt);
        DraftCommitButton = DraftPrompt.GetOkButton();
        DraftDiscardButton = DraftPrompt.AddButton("放弃草稿", true, "discard");
        DraftPrompt.Confirmed += () => CompleteDraft(discard: false);
        DraftPrompt.CustomAction += action => { if (action == "discard") CompleteDraft(discard: true); };
        DraftPrompt.Canceled += () => _afterDraft = null;
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
        var result = State.Commit();
        _lastResult = result.Result is VoxelCellSetResult cells ? Describe(cells) : result.Status;
        Present();
    }

    private void ExactInput()
    {
        if (_syncing || _session.IsChangingWorld || State.HasBatchDraft || (!HasContentSelection && State.Operation != VoxelCellOperation.Remove))
            return;
        State.SetExact(new WorldVoxelCoord((int)CoordinateInputs[0].Value, (int)CoordinateInputs[1].Value, (int)CoordinateInputs[2].Value),
            State.DefinitionId, State.Orientation, State.Operation);
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
    internal bool HandlePointer(Vector2 screen, bool commit)
    {
        if (!State.Active)
            return false;
        if (_session.IsChangingWorld || DraftPrompt.Visible)
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
        if (commit && State.HasDraft && State.Position != target)
        {
            RequestChange(() => State.Hover(target));
            return true;
        }
        State.Hover(target);
        // Precise parameters pin the same preview until they are explicitly committed or discarded.
        if (commit)
            Commit();
        Present();
        return true;
    }

    internal void Present()
    {
        if (State is null)
            return;
        _syncing = true;
        try
        {
            _parameters.Visible = State.Active || State.HasBatchDraft;
            _title.Text = State.HasBatchDraft ? "批量体素 · 世界坐标" : "单格体素 · 世界坐标";
            CommitButton.Text = State.HasBatchDraft ? "提交批量" : "提交单格";
            SelectToolButton.ButtonPressed = !State.Active;
            VoxelToolButton.ButtonPressed = State.Active;
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
            OperationInput.Disabled = State.HasBatchDraft;
            foreach (var input in CoordinateInputs)
                input.Editable = !State.HasBatchDraft;
            CoordinateInputs[0].SetValueNoSignal(State.Position.X);
            CoordinateInputs[1].SetValueNoSignal(State.Position.Y);
            CoordinateInputs[2].SetValueNoSignal(State.Position.Z);
            var canPreview = State.DefinitionId.Namespace is not null || State.Operation == VoxelCellOperation.Remove;
            var preview = canPreview ? State.Preview : new VoxelCellSetResult(VoxelCellSetStatus.UnknownDefinition, State.Position);
            _preview.Text = (State.HasBatchDraft ? $"批量草稿 · {State.BatchTargets.Count} 格 · 目标已绑定，选择变化不改写目标\n" :
                $"世界格 {State.Position.X}, {State.Position.Y}, {State.Position.Z} · Y 平面 {State.Height}\n") +
                "轴向：+X 红线 · +Z 蓝线 · +Y 向上\n" +
                $"{(State.HasDraft ? "草稿（保存不包含）" : "悬停预览")} · {Describe(preview)}" + (_lastResult.Length > 0 ? "\n上次提交：" + _lastResult : "");
            DiscardButton.Disabled = !State.HasDraft;
            CommitButton.Disabled = (!HasContentSelection && State.Operation != VoxelCellOperation.Remove) || _session.IsChangingWorld;
            var material = (StandardMaterial3D)_cell.MaterialOverride;
            material.AlbedoColor = preview.Succeeded ? new Color(0.2f, 1, 0.4f, 0.35f) : new Color(1, 0.2f, 0.15f, 0.4f);
            var cells = State.HasBatchDraft ? State.BatchTargets : new[] { State.Position };
            _cell.Multimesh.InstanceCount = cells.Count;
            for (var index = 0; index < cells.Count; index++)
                _cell.Multimesh.SetInstanceTransform(index, new Transform3D(Basis.Identity,
                    new Vector3(cells[index].X + 0.5f, cells[index].Y + 0.5f, cells[index].Z + 0.5f)));
            _cell.Visible = State.Active || State.HasBatchDraft;
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
            VoxelCellSetStatus.ExpectedContentMismatch => $"绑定目标 ({result.Position.X},{result.Position.Y},{result.Position.Z}) 已消失或身份已改变；整次拒绝，草稿保留",
            VoxelCellSetStatus.RailOccupied => result.OwnerKind == VoxelCellOwnerKind.None ? "铁路体素（未登记轨段）" : "属于铁路",
            VoxelCellSetStatus.VehicleOccupied => "车辆实际占用",
            _ => result.Status.ToString()
        };
        return reason + (result.OwnerKind != VoxelCellOwnerKind.None ? $" · 对象 {result.OwnerId.Value}" : "") +
            (result.OwnerKind == VoxelCellOwnerKind.VehiclePart ? $" · 零件 {result.PartId.Value}" : "");
    }

    private void CreateMeshes()
    {
        _grid = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _cell = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = new BoxMesh { Size = Vector3.One * 1.02f } },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha, NoDepthTest = true }
        };
        _axes = new Label3D { Text = "X →  ·  Z ↓  ·  Y ↑", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, FontSize = 32 };
        AddChild(_grid);
        AddChild(_cell);
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
