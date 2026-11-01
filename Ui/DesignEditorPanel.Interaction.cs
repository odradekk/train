using Godot;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal partial class DesignEditorPanel
{
    private SpinBox _height = null!;
    private readonly List<GridPosition> _stroke = [];
    private GridPosition? _boxStart;
    private Vector2? _hoverPosition;
    private bool _hoverDirty;
    private Transform3D _hoverCamera;
    private (EditOperation.Cells Operation, EditState State)? _hoverQuery;

    private void Execute(EditOperation operation)
    {
        _service.Apply(operation);
        InvalidateHover();
    }

    private void ExecuteCells(EditFootprint? footprint = null)
    {
        if (Action() is not { } action) return;
        footprint ??= _tool.Selected == 2 ? new EditFootprint.Box(Bounds()) : new EditFootprint.Cell(At());
        Execute(new EditOperation.Cells(footprint, action));
    }

    private void CancelGesture() { _stroke.Clear(); _boxStart = null; }
    private void ClearHover()
    {
        _hoverQuery = null;
        _scene.ShowPreview(null);
        _previewText.Text = "";
    }
    private void InvalidateHover() => _hoverDirty = true;

    private void Pick(ScenePick pick)
    {
        if (_tool.Selected == 3)
        {
            if (pick is ScenePick.Hit { Value.Subject: PickSubject.Editing element })
                _service.Select(new EditSelection.Elements([element.Element]));
            return;
        }
        var position = GetViewport().GetMousePosition();
        if (Target(pick, position) is not { } cell) return;
        _x.Value = cell.X; _y.Value = cell.Y; _z.Value = cell.Z;
        if (_tool.Selected == 1)
        {
            _stroke.Clear(); _stroke.Add(cell);
            ShowHover(cell);
        }
        else if (_tool.Selected == 2 && _boxStart is null)
        {
            _boxStart = cell;
            ShowHover(cell);
        }
        else if (Footprint(cell) is { } footprint)
        {
            ExecuteCells(footprint);
            _boxStart = null;
        }
    }

    private GridPosition? Target(ScenePick? pick, Vector2 position)
    {
        if (pick is ScenePick.Hit { Value: var hit })
        {
            _lastHit = hit;
            return _scene.CellFor(hit, _action.Selected == 0);
        }
        return pick is ScenePick.Miss ? _scene.PickPlane(position) : null;
    }

    private EditFootprint? Footprint(GridPosition cell)
    {
        if (_tool.Selected == 1)
            return new EditFootprint.Stroke(_stroke.Count > 0 ? [.. _stroke] : [cell], (int)_width.Value);
        if (_tool.Selected != 2) return new EditFootprint.Cell(cell);
        var start = _boxStart ?? cell;
        var top = Math.Max(start.Y, cell.Y) + (_action.Selected == 0 ? (long)_height.Value - 1 : 0);
        if (top > int.MaxValue)
        {
            ClearHover(); _previewText.Text = "长方体超出坐标范围，请降低高度。";
            return null;
        }
        return new EditFootprint.Box(new(
            new(Math.Min(start.X, cell.X), Math.Min(start.Y, cell.Y), Math.Min(start.Z, cell.Z)),
            new(Math.Max(start.X, cell.X), (int)top, Math.Max(start.Z, cell.Z))));
    }

    private void Hover(Vector2 position, bool dragging)
    {
        _hoverPosition = position;
        _hoverDirty = false;
        _hoverCamera = _scene.View.CameraRig.Camera.GlobalTransform;
        if (_tool.Selected == 3 || !FramingArea().HasPoint(position)
            || GetViewport().GuiGetHoveredControl() is not null || _service.Leaving)
        { ClearHover(); return; }
        if (Target(_scene.Pick(position), position) is not { } cell)
        { ClearHover(); return; }
        if (dragging && _stroke.Count > 0 && !_stroke.Contains(cell)) _stroke.Add(cell);
        ShowHover(cell);
    }

    private void ShowHover(GridPosition cell)
    {
        if (_service.State is not { } state || Action() is not { } action) { ClearHover(); return; }
        if (Footprint(cell) is not { } footprint) return;
        var operation = new EditOperation.Cells(footprint, action);
        if (_hoverQuery is { } previous && SameHoverState(previous.State, state)
            && previous.Operation.Action == action && SameFootprint(previous.Operation.Footprint, footprint))
        { ShowBoxHint(); return; }
        _hoverQuery = (operation, state);
        var result = _service.Preview(operation);
        var preview = (result as SceneResult<IEditPreview>.Success)?.Value;
        if (preview is { } current && current.Basis != _service.State?.Snapshot.Stamp)
        { ClearHover(); return; }
        _scene.ShowPreview(preview, result is SceneResult<IEditPreview>.Failure ? footprint : null);
        if (preview is not null)
        {
            var reason = preview.Conflicts.IsEmpty && preview.AffectedCells == 0 ? NoChange(action)
                : string.Join("；", preview.Conflicts.GroupBy(conflict => conflict.Problem.Code).Select(group =>
                {
                    var first = group.First();
                    var explanation = EditorService.Explain(first.Problem, _service.Host.Content);
                    return first.Cell is { } at ? $"{group.Count()} 格{explanation.TrimEnd('。')}（首格 {PositionText(at)}）。" : explanation;
                }));
            _previewText.Text = $"覆盖 {preview.CoveredCells} · 影响 {preview.AffectedCells} · 跳过 {preview.SkippedCells} · 占格{(preview.OccupancyComplete ? "完整" : "未知")}\n{reason}";
        }
        else _previewText.Text = result switch
        {
            SceneResult<IEditPreview>.Failure failure => string.Join("；", failure.Problems.Select(problem => EditorService.Explain(problem, _service.Host.Content))),
            SceneResult<IEditPreview>.Faulted fault => EditorService.Explain(fault.Problem, _service.Host.Content),
            _ => "预览不可用"
        };
        ShowBoxHint();
    }

    private void ShowBoxHint()
    {
        const string hint = "\n再点一下确定对角，Esc 取消。";
        var shown = _previewText.Text.EndsWith(hint);
        if (_boxStart is not null && !shown) _previewText.Text += hint;
        else if (_boxStart is null && shown) _previewText.Text = _previewText.Text[..^hint.Length];
    }

    private static bool SameHoverState(EditState left, EditState right) =>
        left.Snapshot.Stamp == right.Snapshot.Stamp && left.EnteredObject == right.EnteredObject
        && ReferenceEquals(left.CurrentEvaluation, right.CurrentEvaluation) && left.Selection.SequenceEqual(right.Selection);

    private static bool SameFootprint(EditFootprint left, EditFootprint right) => left == right
        || left is EditFootprint.Stroke a && right is EditFootprint.Stroke b
        && a.BrushWidth == b.BrushWidth && a.Samples.SequenceEqual(b.Samples);

    private static string NoChange(CellEdit action) => action switch
    {
        CellEdit.Add => "已有体素。",
        CellEdit.Remove => "没有可移除的体素。",
        CellEdit.Paint => "没有可涂装的体素，或颜色未改变。",
        CellEdit.Replace => "没有可替换的体素，或体素未改变。",
        _ => "没有可修改的体素，或形状未改变。"
    };

    private void ProcessHover()
    {
        if (_service.SceneUnavailable || _service.Leaving || GetViewport().GuiGetHoveredControl() is not null)
        { ClearHover(); _hoverDirty = true; return; }
        if (_scene.View.Pointer is null && _hoverPosition is not null)
        { ClearHover(); _hoverPosition = null; return; }
        if (_hoverPosition is { } position && (_hoverDirty || _hoverCamera != _scene.View.CameraRig.Camera.GlobalTransform
            || _hoverQuery is not { } query || _service.State is { } state && !SameHoverState(query.State, state)))
            Hover(position, false);
    }

    public override void _Input(InputEvent input)
    {
        if (!Visible || _stroke.Count == 0 || input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }) return;
        ExecuteCells(new EditFootprint.Stroke([.. _stroke], (int)_width.Value));
        _stroke.Clear();
        GetViewport().SetInputAsHandled();
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (!Visible) return;
        if (input.IsActionPressed(UndoAction)) { CancelGesture(); _service.Undo(); }
        else if (input.IsActionPressed(RedoAction)) { CancelGesture(); _service.Redo(); }
        else if (input.IsActionPressed(SaveAction)) _service.SaveDraft();
        else if (input.IsActionPressed(CloseAction))
        {
            if (_boxStart is not null) { _boxStart = null; InvalidateHover(); }
            else _service.RequestClose(() => { });
        }
        else if (input is InputEventMouseMotion motion)
        {
            Hover(motion.Position, motion.ButtonMask.HasFlag(MouseButtonMask.Left));
            return;
        }
        else return;
        GetViewport().SetInputAsHandled();
    }
}
