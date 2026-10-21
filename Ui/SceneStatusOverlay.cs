using Godot;
using Train.App;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal static class SceneStatus
{
    // 与结构冒烟、起火阈值一致。毁坏实例会从核心删除，血条不会停在 0。
    internal const double SmokeRatio = 0.5;
    internal const double FireRatio = 0.25;

    internal abstract record Icon
    {
        private Icon() { }
        internal sealed record Train(TrainActivity Activity) : Icon;
        internal sealed record Relation(SettlementRelation Ownership) : Icon;
    }

    internal readonly record struct Marker(InstanceId Id, double Ratio, Color Bar, Icon Icon);

    internal static double Ratio(DurabilityView structure) =>
        structure.Maximum <= 0 ? 1 : Math.Clamp(structure.EffectiveCurrent / (double)structure.Maximum, 0, 1);

    internal static Color BarColor(double ratio) =>
        ratio >= SmokeRatio ? UiStyle.Success : ratio >= FireRatio ? UiStyle.Warning : UiStyle.Error;

    internal static Icon Choose(InstanceView instance, TrainView? train) =>
        instance.Details is InstanceDetails.Vehicle { Train: not null } && train is not null
            ? new Icon.Train(train.Activity)
            : new Icon.Relation(instance.Ownership);
}

internal partial class SceneStatusOverlay : Control
{
    private const float IconSize = 16;
    private const float IconGap = 4;
    private const float TrackWidth = 48;
    private const float TrackHeight = 6;
    private const float Lift = 28;
    private static readonly Color Track = new("1a1e24");
    private readonly Dictionary<TrainActivity, Texture2D?> _symbols = [];
    private readonly List<Placed> _placed = [];

    internal bool Always { get; set; }
    internal WorldSceneController? Scenes { get; set; }
    internal ICoreReadView? Core { get; set; }
    internal IEnumerable<InstanceId> Selected { get; set; } = [];
    internal IReadOnlyList<SceneStatus.Marker> Markers => _placed.Select(item => item.Marker).ToArray();

    private readonly record struct Placed(SceneStatus.Marker Marker, Vector2 Anchor);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _Process(double delta)
    {
        var scenes = Scenes;
        var core = Core;
        var frame = scenes is { View.Visible: true } ? scenes.InteractionFrame : null;
        Visible = frame is not null && core is not null;
        _placed.Clear();
        if (frame is null || core is null || scenes is null) return;
        var ids = new List<InstanceId>();
        if (Always && scenes.ViewedLocation(core) is { } location &&
            core.FindLocation(location) is Lookup<LocationView>.Found found)
            ids.AddRange(found.Value.Instances);
        if (Hover(scenes) is { } hover && !ids.Contains(hover)) ids.Add(hover);
        foreach (var selected in Selected)
            if (!ids.Contains(selected)) ids.Add(selected);
        foreach (var id in ids) TryAdd(scenes, core, frame, id);
        QueueRedraw();
    }

    public override void _Draw()
    {
        foreach (var placed in _placed)
        {
            var marker = placed.Marker;
            DrawIcon(marker.Icon, new Rect2(placed.Anchor, new Vector2(IconSize, IconSize)));
            var track = new Rect2(placed.Anchor + new Vector2(IconSize + IconGap, (IconSize - TrackHeight) / 2f),
                new Vector2(TrackWidth, TrackHeight));
            DrawRect(track, Track);
            var fill = TrackWidth * (float)marker.Ratio;
            if (fill > 0) DrawRect(new Rect2(track.Position, new Vector2(fill, TrackHeight)), marker.Bar);
        }
    }

    private InstanceId? Hover(WorldSceneController scenes)
    {
        if (scenes.View.Pointer is not { } pointer || GetViewport().GuiGetHoveredControl() is not null) return null;
        return scenes.InspectPick(pointer)?.Resolution is SceneResult<PickSubject>.Success
            { Value: PickSubject.WorldObject { Object: WorldObjectId.Instance instance } } ? instance.Id : null;
    }

    private void TryAdd(WorldSceneController scenes, ICoreReadView core, ISceneFrame frame, InstanceId id)
    {
        if (core.FindInstance(id) is not Lookup<InstanceView>.Found found) return;
        if (frame.FindPose(new WorldObjectId.Instance(id)) is not Lookup<ScenePose>.Found pose) return;
        var camera = scenes.View.CameraRig.Camera;
        var position = pose.Value.Position;
        var global = scenes.View.ToGlobal(new Vector3(position.X, position.Y, position.Z));
        if (camera.IsPositionBehind(global)) return;
        var screen = GetGlobalTransformWithCanvas().AffineInverse() * camera.UnprojectPosition(global);
        var anchor = screen - new Vector2(0, Lift) - new Vector2(IconSize + IconGap + TrackWidth, IconSize) / 2f;
        var instance = found.Value;
        TrainView? train = null;
        if (instance.Details is InstanceDetails.Vehicle { Train: { } trainId } &&
            core.FindTrain(trainId) is Lookup<TrainView>.Found foundTrain)
            train = foundTrain.Value;
        var ratio = SceneStatus.Ratio(instance.Structure);
        _placed.Add(new(new(id, ratio, SceneStatus.BarColor(ratio), SceneStatus.Choose(instance, train)), anchor));
    }

    private void DrawIcon(SceneStatus.Icon icon, Rect2 rect)
    {
        switch (icon)
        {
            case SceneStatus.Icon.Train train:
                if (Symbol(train.Activity) is { } texture)
                    DrawTextureRect(texture, rect, false, UiStyle.TrainActivityColor(train.Activity));
                break;
            case SceneStatus.Icon.Relation relation:
                var color = UiStyle.RelationColor(relation.Ownership);
                var center = rect.GetCenter();
                const float radius = 6f;
                if (relation.Ownership == SettlementRelation.PlayerOwned) DrawCircle(center, radius, color);
                else if (relation.Ownership == SettlementRelation.Hostile)
                    for (var segment = 0; segment < 12; segment++)
                        DrawArc(center, radius, segment * Mathf.Tau / 12f, (segment + .55f) * Mathf.Tau / 12f, 4, color, 2, true);
                else DrawArc(center, radius, 0, Mathf.Tau, 24, color, 2, true);
                break;
        }
    }

    private Texture2D? Symbol(TrainActivity activity)
    {
        var key = UiStyle.TrainSymbolActivity(activity);
        if (_symbols.TryGetValue(key, out var texture)) return texture;
        texture = GD.Load<Texture2D>(UiStyle.TrainSymbolPath(key));
        _symbols[key] = texture;
        return texture;
    }
}
