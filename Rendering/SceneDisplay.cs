using System.Collections.Immutable;
using System.Diagnostics;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneDisplay : IDisposable
{
    internal sealed record DisplayObject(SceneObject Source, Node3D Root, PreparedVisual[] Visuals);
    private readonly SceneResources _resources;
    private readonly CombatEffects _effects;
    private readonly AssetResourceCache _cache;
    private readonly bool _completeInitialFrame;
    private ImmutableDictionary<SceneObjectId, DisplayObject> _objects = ImmutableDictionary<SceneObjectId, DisplayObject>.Empty;
    private readonly Dictionary<SceneObjectId, HashSet<SceneObjectId>> _children = [];
    private readonly HashSet<SceneObjectId> _rootObjects = [];
    private readonly HashSet<SceneObjectId> _poseRoots = [];
    private readonly Dictionary<WorldObjectId, HashSet<SceneObjectId>> _subjectRoots = [];
    private readonly Dictionary<SceneObjectId, DisplayObject> _effectObjects = [];
    private readonly HashSet<SceneObjectId> _fogDirty = [];
    private readonly HashSet<VehicleId> _fogVehicles = [];
    private bool _resetFog;
    private FogGroup? _fog;
    private DebugLineGroup? _debugLines;
    private FogGroup? _pendingFog;
    private DebugLineGroup? _pendingDebugLines;
    private Preparation? _preparation;
    private long _candidate;
    internal Action<SceneDisplayEvent>? Observe { get; set; }
    internal Action<SceneDisplayWork, TimeSpan, TimeSpan>? Measure { get; set; }
    private ScenePoint _spaceOrigin;
    private HashSet<SceneObjectId> _posedObjects = [];
    private bool _disposed;
    private List<PreparedVisual> _pendingVisuals = [];
    private Dictionary<SceneObjectId, Node3D> _pendingRoots = [];
    private static readonly TimeSpan PreparationBudget = TimeSpan.FromMilliseconds(4);
    private static ulong _budgetFrame;
    private static TimeSpan _preparationTime;
    internal Func<long> Timestamp { get; set; } = Stopwatch.GetTimestamp;
    internal Func<SceneMesh, CancellationToken, PackedSceneMesh> PackMesh { get; set; } = PackedSceneMesh.Create;
    private Task? _preparationPackingTail;
    // 活动与候选沿用同一份最近成本，准备完成或取消后仍保留。
    private readonly ScenePackingPreparation.Costs _packingCosts = new();
    internal Task PreparationPacking => _preparation?.Packing.Job?.Completion ?? Task.CompletedTask;

    internal Node3D Root { get; } = new() { Name = "SceneDisplay" };
    internal ISceneFrame? DisplayedFrame { get; private set; }
    internal ISceneFrame? PoseFrame { get; private set; }
    internal bool IsPreparing => _preparation is not null;
    internal IReadOnlyDictionary<SceneObjectId, DisplayObject> Objects => _objects;
    internal DisplayObject? FindRoot(WorldObjectId subject) => _subjectRoots.TryGetValue(subject, out var roots)
        ? _objects[roots.First()] : null;

    // 显示坐标 = 显示根局部坐标 + 根节点平移，与 Pick 射线和镜头焦点一致。
    internal float? ObjectTopY(SceneObjectId id)
    {
        _cache.RequireMainThread();
        if (!_objects.TryGetValue(id, out var item)) return null;
        while (item.Source.Parent is { } parent)
        {
            if (!_objects.TryGetValue(parent, out item)) return null;
        }
        float? top = null;
        Visit(item.Root);
        return top;

        void Visit(Node node)
        {
            if (node is Node3D spatial && !spatial.IsVisibleInTree()) return;
            if (node is MeshInstance3D mesh && mesh.Mesh is not null)
            {
                var bounds = Root.GlobalTransform.AffineInverse() * mesh.GlobalTransform * mesh.GetAabb();
                var y = bounds.End.Y + Root.Position.Y;
                top = top is { } current ? MathF.Max(current, y) : y;
            }
            foreach (var child in node.GetChildren()) Visit(child);
        }
    }

    internal SceneDisplay(AssetResourceCache cache, IContentView content, bool completeInitialFrame = false)
    {
        _cache = cache;
        _completeInitialFrame = completeInitialFrame;
        _resources = new(cache, content);
        _effects = new(cache);
        Root.AddChild(_effects.Root);
    }

    internal void SetFollowedTrain(ImmutableArray<VehicleId> vehicles) => _effects.SetFollowedTrain(vehicles);

    internal Vector3 AdvanceEffects(ISceneFrame frame, double realDelta, Vector3? camera = null) =>
        _effects.Advance(frame, realDelta, camera);

    internal double FlickerSeconds => _effects.FlickerSeconds;

    internal void ClearDebugLines()
    {
        _cache.RequireMainThread();
        _debugLines?.Dispose();
        _debugLines = null;
    }

    internal Vector3 UpdatePoses(ISceneFrame frame)
    {
        _cache.RequireMainThread();
        if (DisplayedFrame is not { } displayed) return Vector3.Zero;
        if (PoseFrame is null || PoseFrame.Update.Frame.Space != frame.Update.Frame.Space ||
            PoseFrame.Update.Frame.Request != frame.Update.Frame.Request)
            _spaceOrigin = frame.Origin;
        var origin = displayed.Update.Frame.Space == frame.Update.Frame.Space ? displayed.Origin : _spaceOrigin;
        var incremental = frame.Update.Base == displayed.Update.Frame;
        var shift = incremental ? ToGodot(frame.Update.ShiftExistingRoots) :
            new Vector3((float)(origin.X - frame.Origin.X), (float)(origin.Y - frame.Origin.Y),
                (float)(origin.Z - frame.Origin.Z));
        var delta = shift - Root.Position;
        if (delta != Vector3.Zero) Root.Position = shift;
        var posed = new HashSet<SceneObjectId>();
        if (incremental)
        {
            foreach (var source in frame.Update.Upserts)
                if (_objects.TryGetValue(source.Id, out var item) && item.Source.Parent == source.Parent)
                    Apply(item, source.Parent is null ? source.Pose with
                    { Position = source.Pose.Position - new System.Numerics.Vector3(shift.X, shift.Y, shift.Z) } : source.Pose);
        }
        else
            foreach (var id in _poseRoots)
            {
                var item = _objects[id];
                if (item.Source.Subject is { } subject &&
                    frame.FindPose(subject) is Train.Core.Contracts.Lookup<ScenePose>.Found found)
                    Apply(item, found.Value with
                    { Position = found.Value.Position - new System.Numerics.Vector3(shift.X, shift.Y, shift.Z) });
            }
        foreach (var id in _posedObjects)
            _objects[id].Root.Transform = ToTransform(_objects[id].Source.Pose);
        _posedObjects = posed;
        PoseFrame = frame;
        return delta;

        void Apply(DisplayObject item, ScenePose pose)
        {
            if (pose == item.Source.Pose) return;
            item.Root.Transform = ToTransform(pose);
            posed.Add(item.Source.Id);
            _posedObjects.Remove(item.Source.Id);
        }
    }

    internal bool CanSelect(SceneHit hit)
    {
        if (PoseFrame is not { } frame || hit.Object is not { } id || !_objects.TryGetValue(id, out var item))
            return false;
        if (ReferenceEquals(frame, DisplayedFrame)) return true;
        var displayed = DisplayedFrame!;
        // 较早完整候选刚提交时P仍无Base，同一范围内已就绪的对象无需再等一帧。
        if (frame.Update.Base is null && (IsPreparing ||
            frame.Update.Frame.Session != displayed.Update.Frame.Session ||
            frame.Update.Frame.Request != displayed.Update.Frame.Request ||
            frame.Update.Frame.Space != displayed.Update.Frame.Space)) return false;
        if (frame.Update.Base != displayed.Update.Frame && frame.Update.Base != displayed.Update.Base ||
            frame.Update.Removed.Contains(id)) return false;
        var source = frame.Update.Upserts.FirstOrDefault(source => source.Id == id);
        // 较早候选刚提交时P仍引用旧D；两帧都未更新的对象才可继承同一描述。
        if (source is null) return frame.Update.Base == displayed.Update.Frame ||
            !displayed.Update.Upserts.Any(source => source.Id == id);
        return source.Parent == item.Source.Parent && MatchVisuals(item, source.Visuals).All(visual => visual is not null);
    }

    private static PreparedVisual?[] MatchVisuals(DisplayObject? displayed, ImmutableArray<SceneVisual> visuals)
    {
        var matching = new VisualMatching(displayed, visuals);
        while (!matching.Complete) matching.Advance();
        return matching.Matched;
    }

    internal void UpdateConstructionFog(System.Collections.Immutable.ImmutableArray<VehicleId> vehicles)
    {
        _cache.RequireMainThread();
        var reset = _resetFog || !_fogVehicles.SetEquals(vehicles);
        if (reset)
        {
            _fogVehicles.Clear();
            _fogVehicles.UnionWith(vehicles);
        }
        foreach (var id in reset ? _objects.Keys : _fogDirty)
        {
            if (!_objects.TryGetValue(id, out var item)) continue;
            var followed = BelongsToFollowedTrain(item.Source, _objects, _fogVehicles);
            foreach (var visual in item.Visuals) _resources.ExemptConstructionFog(visual, followed);
        }
        _fogDirty.Clear();
        _resetFog = false;
    }

    internal static bool BelongsToFollowedTrain(SceneObject source,
        IReadOnlyDictionary<SceneObjectId, DisplayObject> objects, IReadOnlySet<VehicleId> vehicles)
    {
        while (true)
        {
            if (source.Subject is WorldObjectId.Instance { Id: InstanceId.Vehicle vehicle } && vehicles.Contains(vehicle.Id)) return true;
            if (source.Parent is not { } parent || !objects.TryGetValue(parent, out var ancestor)) return false;
            source = ancestor.Source;
        }
    }

    internal void CancelPreparation()
    {
        _cache.RequireMainThread();
        var cancelled = _preparation?.Frame;
        _preparation?.Packing.Dispose();
        _preparation?.ShiftRoots?.Dispose();
        _preparation?.ValidationChildren?.Dispose();
        foreach (var visual in _pendingVisuals) visual.Dispose();
        _pendingVisuals.Clear();
        _preparation = null;
        foreach (var root in _pendingRoots.Values) root.Free();
        _pendingRoots.Clear();
        _pendingFog?.Dispose();
        _pendingFog = null;
        _pendingDebugLines?.Dispose();
        _pendingDebugLines = null;
        if (cancelled is not null)
            Observe?.Invoke(new(_candidate, cancelled.Update.Frame, null, SceneDisplayStage.HostCancelled));
    }

    private void Commit(PreparedFrame prepared)
    {
        _cache.RequireMainThread();
        var previous = DisplayedFrame;
        var update = prepared.Frame.Update;
        var deferRetirement = ChangesSpace(update.Frame);
        var next = prepared.Objects;
        var upserts = update.Upserts.Select(item => item.Id).ToHashSet();
        var changed = update.Base is null ? _objects.Keys.Concat(next.Keys).ToHashSet() :
            update.Removed.Concat(upserts).ToHashSet();
        // 先摘除退役或换父的对象，避免退役父节点释放仍在使用的子对象。
        foreach (var id in changed)
        {
            if (!_objects.TryGetValue(id, out var item)) continue;
            var surviving = next.GetValueOrDefault(item.Source.Id);
            if (surviving is null || item.Source.Parent != surviving.Source.Parent)
                item.Root.GetParent()?.RemoveChild(item.Root);
        }
        if (Root.Position != Vector3.Zero) Root.Position = Vector3.Zero;
        var transformed = upserts.Concat(_posedObjects).ToHashSet();
        if (update.ShiftExistingRoots != System.Numerics.Vector3.Zero) transformed.UnionWith(_rootObjects);
        foreach (var id in transformed)
            if (next.TryGetValue(id, out var item)) item.Root.Transform = ToTransform(item.Source.Pose);
        foreach (var id in upserts)
        {
            var item = next[id];
            for (var i = 0; i < item.Visuals.Length; i++)
            {
                var visual = item.Visuals[i];
                visual.Update(item.Source.Visuals[i]);
                if (visual.Node.GetParent() is null) item.Root.AddChild(visual.Node);
            }
        }
        foreach (var id in upserts)
        {
            var item = next[id];
            if (item.Root.GetParent() is null)
                (item.Source.Parent is { } parent ? next[parent].Root : Root).AddChild(item.Root);
        }
        var old = _objects;
        var oldFog = _fog;
        prepared.Fog.Activate();
        if (!ReferenceEquals(oldFog?.Root, prepared.Fog.Root))
        {
            oldFog?.Root.GetParent()?.RemoveChild(oldFog.Root);
            Root.AddChild(prepared.Fog.Root);
        }
        _fog = prepared.Fog;
        var changedAncestry = new List<SceneObjectId>();
        foreach (var id in changed)
        {
            if (_objects.TryGetValue(id, out var oldItem) && oldItem.Source.Parent is { } oldParent &&
                _children.TryGetValue(oldParent, out var siblings))
            {
                siblings.Remove(id);
                if (siblings.Count == 0) _children.Remove(oldParent);
            }
            _rootObjects.Remove(id);
            _poseRoots.Remove(id);
            if (oldItem?.Source is { Parent: null, Subject: { } oldSubject } && _subjectRoots.TryGetValue(oldSubject, out var oldRoots))
            {
                oldRoots.Remove(id);
                if (oldRoots.Count == 0) _subjectRoots.Remove(oldSubject);
            }
            _effectObjects.Remove(id);
            if (!next.TryGetValue(id, out var item)) { _fogDirty.Remove(id); continue; }
            if (item.Source.Parent is { } parent)
            {
                if (!_children.TryGetValue(parent, out var children)) _children.Add(parent, children = []);
                children.Add(id);
            }
            else
            {
                _rootObjects.Add(id);
                if (item.Source.Subject is { } subject)
                {
                    _poseRoots.Add(id);
                    if (!_subjectRoots.TryGetValue(subject, out var roots)) _subjectRoots.Add(subject, roots = []);
                    roots.Add(id);
                }
            }
            if (!item.Source.Lights.IsEmpty || !item.Source.Effects.IsEmpty) _effectObjects[id] = item;
            _fogDirty.Add(id);
            if (oldItem is null || oldItem.Source.Parent != item.Source.Parent || oldItem.Source.Subject != item.Source.Subject)
                changedAncestry.Add(id);
        }
        foreach (var id in changedAncestry) MarkFogSubtree(id);
        _resetFog |= update.Base is null;
        _objects = next;
        if (!ReferenceEquals(_debugLines, prepared.DebugLines))
        {
            _debugLines?.Dispose();
            _debugLines = prepared.DebugLines;
            if (_debugLines is not null) Root.AddChild(_debugLines.Root);
        }
        prepared.Transferred = true;
        if (HasCandidateFor(update.Frame))
        {
            _candidatePromoted = true;
            CancelCandidatePacking();
        }
        DisplayedFrame = prepared.Frame;
        PoseFrame = prepared.Frame;
        _spaceOrigin = prepared.Frame.Origin;
        _posedObjects.Clear();
        Root.Visible = true;
        _effects.Apply(previous, prepared.Frame, _effectObjects);
        ReleasePrevious(old, next, changed, oldFog, deferRetirement);
        Observe?.Invoke(new(prepared.Candidate, update.Frame, null, SceneDisplayStage.FrameCommitted));
    }

    private void MarkFogSubtree(SceneObjectId id)
    {
        _fogDirty.Add(id);
        if (_children.TryGetValue(id, out var children))
            foreach (var child in children) MarkFogSubtree(child);
    }

    public void Dispose()
    {
        _cache.RequireMainThread();
        if (_disposed) return;
        _disposed = true;
        CancelPreparation();
        CancelCandidate();
        _retirement?.Dispose();
        _retirement = null;
        _effects.Dispose();
        foreach (var item in _objects.Values) item.Root.GetParent()?.RemoveChild(item.Root);
        foreach (var item in _objects.Values)
        {
            foreach (var visual in item.Visuals) visual.Dispose();
            item.Root.Free();
        }
        _objects = _objects.Clear();
        _children.Clear();
        _rootObjects.Clear();
        _poseRoots.Clear();
        _subjectRoots.Clear();
        _effectObjects.Clear();
        _fogDirty.Clear();
        _fogVehicles.Clear();
        _posedObjects.Clear();
        _fog?.Dispose();
        _fog = null;
        _debugLines?.Dispose();
        _debugLines = null;
        Root.Free();
        _resources.Dispose();
        DisplayedFrame = null;
        PoseFrame = null;
    }

    internal sealed class PreparedFrame(SceneDisplay owner, long candidate, ISceneFrame frame,
        ImmutableDictionary<SceneObjectId, DisplayObject> objects, List<PreparedVisual> visuals,
        Dictionary<SceneObjectId, Node3D> roots, FogGroup fog, bool ownsFog, DebugLineGroup? debugLines, bool ownsDebugLines) : IDisposable
    {
        internal long Candidate { get; } = candidate;
        internal bool Transferred { get; set; }
        private bool _disposed;
        internal ISceneFrame Frame { get; } = frame;
        internal ImmutableDictionary<SceneObjectId, DisplayObject> Objects { get; } = objects;
        internal FogGroup Fog { get; } = fog;
        internal DebugLineGroup? DebugLines { get; } = debugLines;
        internal void Commit()
        {
            var measure = owner.Measure;
            var started = measure is null ? 0 : owner.Timestamp();
            try { owner.Commit(this); }
            finally
            {
                measure?.Invoke(SceneDisplayWork.Commit, Stopwatch.GetElapsedTime(started, owner.Timestamp()), TimeSpan.Zero);
            }
        }
        public void Dispose()
        {
            if (Transferred || _disposed) return;
            owner._cache.RequireMainThread();
            _disposed = true;
            foreach (var visual in visuals) visual.Dispose();
            foreach (var root in roots.Values) if (GodotObject.IsInstanceValid(root)) root.Free();
            if (ownsFog) Fog.Dispose();
            if (ownsDebugLines) DebugLines?.Dispose();
            owner.Observe?.Invoke(new(Candidate, Frame.Update.Frame, null, SceneDisplayStage.HostCancelled));
        }
    }

    private static Vector3 ToGodot(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
    private static Transform3D ToTransform(ScenePose pose) => new(new Basis(new Quaternion(pose.Rotation.X,
        pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W)), ToGodot(pose.Position));
}
