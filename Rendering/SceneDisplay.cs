using System.Diagnostics;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed class SceneDisplay : IDisposable
{
    internal sealed record DisplayObject(SceneObject Source, Node3D Root, PreparedVisual[] Visuals);
    private readonly SceneResources _resources;
    private readonly AssetResourceCache _cache;
    private Dictionary<SceneObjectId, DisplayObject> _objects = [];
    private FogGroup? _fog;
    private DebugLineGroup? _debugLines;
    private FogGroup? _pendingFog;
    private DebugLineGroup? _pendingDebugLines;
    private ScenePoint _spaceOrigin;
    private HashSet<SceneObjectId> _posedObjects = [];
    private readonly HashSet<VehicleId> _followedVehicles = [];
    private bool _disposed;
    private readonly ScenePreparation<PreparedVisual> _preparation = new(static (visual, source) => visual.CanUpdate(source));
    private readonly Dictionary<SceneObjectId, Node3D> _pendingRoots = [];
    private static readonly TimeSpan PreparationBudget = TimeSpan.FromMilliseconds(4);
    private static ulong _budgetFrame;
    private static TimeSpan _preparationTime;
    internal Func<long> Timestamp { get; set; } = Stopwatch.GetTimestamp;

    internal Node3D Root { get; } = new() { Name = "SceneDisplay" };
    internal ISceneFrame? DisplayedFrame { get; private set; }
    internal ISceneFrame? PoseFrame { get; private set; }
    internal bool IsPreparing => _preparation.Frame is not null;
    internal IReadOnlyDictionary<SceneObjectId, DisplayObject> Objects => _objects;

    internal SceneDisplay(AssetResourceCache cache, IContentView content)
    {
        _cache = cache;
        _resources = new(cache, content);
    }

    internal void ClearDebugLines()
    {
        _cache.RequireMainThread();
        _debugLines?.Dispose();
        _debugLines = null;
    }

    internal PreparedFrame? Prepare(ISceneFrame frame)
    {
        _cache.RequireMainThread();
        if (frame.Readiness == SceneReadiness.Loading ||
            frame.Update.Base is { } baseline && baseline != DisplayedFrame?.Update.Frame)
        {
            CancelPreparation();
            return null;
        }
        if (!_preparation.SameScope(frame)) CancelPreparation();
        var previousFrame = _preparation.Frame;
        frame = _preparation.Select(frame, Timestamp());
        var spaceChanged = DisplayedFrame is { } displayed &&
            (displayed.Update.Frame.Session != frame.Update.Frame.Session ||
             displayed.Update.Frame.Space != frame.Update.Frame.Space);
        var processFrame = Engine.GetProcessFrames();
        if (_budgetFrame != processFrame)
        {
            _budgetFrame = processFrame;
            _preparationTime = TimeSpan.Zero;
        }
        var started = Timestamp();
        var preparedResource = false;
        var update = frame.Update;
        var next = update.Base is null ? new Dictionary<SceneObjectId, DisplayObject>() : new(_objects);
        // 切换空间时一次提交首个可操作帧，避免列车继续驶出仍显示的旧空间。
        bool BudgetExpired() => !spaceChanged && preparedResource &&
            _preparationTime + Stopwatch.GetElapsedTime(started, Timestamp()) >= PreparationBudget;
        try
        {
            if (!ReferenceEquals(previousFrame, frame)) ReconcilePreparation(frame);
            if (update.Base is not null && update.ShiftExistingRoots != System.Numerics.Vector3.Zero)
                foreach (var item in _objects.Values)
                    if (item.Source.Parent is null)
                        next[item.Source.Id] = item with { Source = item.Source with
                        { Pose = item.Source.Pose with { Position = item.Source.Pose.Position + update.ShiftExistingRoots } } };
            foreach (var id in update.Removed) next.Remove(id);
            foreach (var source in update.Upserts)
            {
                _objects.TryGetValue(source.Id, out var previous);
                if (previous is null && !_pendingRoots.ContainsKey(source.Id))
                    _pendingRoots.Add(source.Id, new Node3D { Name = $"Object{source.Id.Value}" });
                var root = previous?.Root ?? _pendingRoots[source.Id];
                var visuals = new PreparedVisual[source.Visuals.Length];
                for (var i = 0; i < visuals.Length; i++)
                {
                    if (previous is not null && i < previous.Visuals.Length &&
                        previous.Visuals[i].CanUpdate(source.Visuals[i]))
                        visuals[i] = previous.Visuals[i];
                    else
                    {
                        var key = (source.Id, i);
                        if (!_preparation.TryGet(key, out var visual))
                        {
                            if (BudgetExpired())
                                return null;
                            visual = _preparation.GetOrCreate(key, () => _resources.Prepare(source.Visuals[i]));
                            preparedResource = true;
                        }
                        visuals[i] = visual;
                    }
                }
                next[source.Id] = new(source, root, visuals);
            }
            foreach (var item in next.Values)
                if (item.Source.Parent is { } parent && !next.ContainsKey(parent))
                    throw new InvalidOperationException("显示对象的父对象不存在。");
            if (_pendingFog is null && (_fog is null || !_fog.Regions.SequenceEqual(update.Environment.FoggedRegions)))
            {
                if (BudgetExpired()) return null;
                _pendingFog = new(update.Environment.FoggedRegions, _resources.ConstructionFogMaterial());
                preparedResource = true;
            }
            if (_pendingDebugLines is null && !frame.DebugLines.IsEmpty &&
                (_debugLines is null || !_debugLines.Lines.SequenceEqual(frame.DebugLines)))
            {
                if (BudgetExpired()) return null;
                _pendingDebugLines = new(frame.DebugLines);
            }
            var prepared = new PreparedFrame(this, frame, next, _preparation.Take(),
                [.. _pendingRoots.Values], _pendingFog ?? _fog!, _pendingFog is not null,
                frame.DebugLines.IsEmpty ? null : _pendingDebugLines ?? _debugLines, _pendingDebugLines is not null);
            _pendingFog = null;
            _pendingDebugLines = null;
            _pendingRoots.Clear();
            return prepared;
        }
        catch
        {
            CancelPreparation();
            throw;
        }
        finally
        {
            _preparationTime += Stopwatch.GetElapsedTime(started, Timestamp());
        }
    }

    private void ReconcilePreparation(ISceneFrame frame)
    {
        var sources = frame.Update.Upserts.ToDictionary(item => item.Id);
        _preparation.Retain(sources, (id, index, visual) => _objects.TryGetValue(id, out var item) &&
            index < item.Visuals.Length && item.Visuals[index].CanUpdate(visual));
        foreach (var id in _pendingRoots.Keys.Where(id => !sources.ContainsKey(id)).ToArray())
        {
            var root = _pendingRoots[id];
            _pendingRoots.Remove(id);
            root.Free();
        }
        if (_pendingFog is not null && !_pendingFog.Regions.SequenceEqual(frame.Update.Environment.FoggedRegions))
        { _pendingFog.Dispose(); _pendingFog = null; }
        if (_pendingDebugLines is not null && !_pendingDebugLines.Lines.SequenceEqual(frame.DebugLines))
        { _pendingDebugLines.Dispose(); _pendingDebugLines = null; }
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
            foreach (var item in _objects.Values)
                if (item.Source.Parent is null && item.Source.Subject is { } subject &&
                    frame.FindPose(subject) is Train.Core.Contracts.Lookup<ScenePose>.Found found)
                    Apply(item, found.Value with
                    { Position = found.Value.Position - new System.Numerics.Vector3(shift.X, shift.Y, shift.Z) });
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
        return source.Parent == item.Source.Parent && source.Visuals.Select((visual, index) =>
            index < item.Visuals.Length && item.Visuals[index].CanUpdate(visual)).All(compatible => compatible);
    }

    internal void UpdateConstructionFog(System.Collections.Immutable.ImmutableArray<VehicleId> vehicles)
    {
        _cache.RequireMainThread();
        _followedVehicles.Clear();
        foreach (var vehicle in vehicles) _followedVehicles.Add(vehicle);
        foreach (var item in _objects.Values)
        {
            var followed = BelongsToFollowedTrain(item.Source, _objects, _followedVehicles);
            foreach (var visual in item.Visuals) _resources.ExemptConstructionFog(visual, followed);
        }
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
        _preparation.Cancel();
        foreach (var root in _pendingRoots.Values) root.Free();
        _pendingRoots.Clear();
        _pendingFog?.Dispose();
        _pendingFog = null;
        _pendingDebugLines?.Dispose();
        _pendingDebugLines = null;
    }

    private void Commit(PreparedFrame prepared)
    {
        _cache.RequireMainThread();
        var update = prepared.Frame.Update;
        var next = prepared.Objects;
        // 先摘除退役或换父的对象，避免退役父节点释放仍在使用的子对象。
        foreach (var item in _objects.Values)
        {
            var surviving = next.GetValueOrDefault(item.Source.Id);
            if (surviving is null || item.Source.Parent != surviving.Source.Parent)
                item.Root.GetParent()?.RemoveChild(item.Root);
        }
        var upserts = update.Upserts.Select(item => item.Id).ToHashSet();
        if (Root.Position != Vector3.Zero) Root.Position = Vector3.Zero;
        foreach (var item in next.Values)
        {
            if (upserts.Contains(item.Source.Id) || _posedObjects.Contains(item.Source.Id) ||
                item.Source.Parent is null && update.ShiftExistingRoots != System.Numerics.Vector3.Zero)
                item.Root.Transform = ToTransform(item.Source.Pose);
            if (upserts.Contains(item.Source.Id))
            {
                for (var i = 0; i < item.Visuals.Length; i++)
                {
                    var visual = item.Visuals[i];
                    visual.Update(item.Source.Visuals[i]);
                    if (visual.Node.GetParent() is null) item.Root.AddChild(visual.Node);
                }
            }
        }
        foreach (var item in next.Values)
            if (item.Root.GetParent() is null)
                (item.Source.Parent is { } parent ? next[parent].Root : Root).AddChild(item.Root);
        var old = _objects;
        var oldFog = _fog;
        if (!ReferenceEquals(oldFog, prepared.Fog))
        {
            oldFog?.Root.GetParent()?.RemoveChild(oldFog.Root);
            Root.AddChild(prepared.Fog.Root);
        }
        _fog = prepared.Fog;
        _objects = next;
        if (!ReferenceEquals(_debugLines, prepared.DebugLines))
        {
            _debugLines?.Dispose();
            _debugLines = prepared.DebugLines;
            if (_debugLines is not null) Root.AddChild(_debugLines.Root);
        }
        prepared.Transferred = true;
        foreach (var item in old.Values)
        {
            var surviving = next.GetValueOrDefault(item.Source.Id);
            foreach (var visual in item.Visuals)
                if (surviving is null || !surviving.Visuals.Contains(visual)) visual.Dispose();
            if (surviving is null) item.Root.Free();
        }
        if (!ReferenceEquals(oldFog, _fog)) oldFog?.Dispose();
        DisplayedFrame = prepared.Frame;
        PoseFrame = prepared.Frame;
        _spaceOrigin = prepared.Frame.Origin;
        _posedObjects.Clear();
        Root.Visible = true;
    }

    public void Dispose()
    {
        _cache.RequireMainThread();
        if (_disposed) return;
        _disposed = true;
        CancelPreparation();
        foreach (var item in _objects.Values) item.Root.GetParent()?.RemoveChild(item.Root);
        foreach (var item in _objects.Values)
        {
            foreach (var visual in item.Visuals) visual.Dispose();
            item.Root.Free();
        }
        _objects.Clear();
        _fog?.Dispose();
        _fog = null;
        _debugLines?.Dispose();
        _debugLines = null;
        Root.Free();
        _resources.Dispose();
        DisplayedFrame = null;
        PoseFrame = null;
    }

    internal sealed class PreparedFrame(SceneDisplay owner, ISceneFrame frame,
        Dictionary<SceneObjectId, DisplayObject> objects, List<PreparedVisual> visuals,
        List<Node3D> roots, FogGroup fog, bool ownsFog, DebugLineGroup? debugLines, bool ownsDebugLines) : IDisposable
    {
        internal bool Transferred { get; set; }
        private bool _disposed;
        internal ISceneFrame Frame { get; } = frame;
        internal Dictionary<SceneObjectId, DisplayObject> Objects { get; } = objects;
        internal FogGroup Fog { get; } = fog;
        internal DebugLineGroup? DebugLines { get; } = debugLines;
        internal void Commit()
        {
            owner.Commit(this);
        }
        public void Dispose()
        {
            if (Transferred || _disposed) return;
            owner._cache.RequireMainThread();
            _disposed = true;
            foreach (var visual in visuals) visual.Dispose();
            foreach (var root in roots) if (GodotObject.IsInstanceValid(root)) root.Free();
            if (ownsFog) Fog.Dispose();
            if (ownsDebugLines) DebugLines?.Dispose();
        }
    }

    internal sealed class DebugLineGroup : IDisposable
    {
        internal System.Collections.Immutable.ImmutableArray<SceneDebugLine> Lines { get; }
        internal MeshInstance3D Root { get; } = new()
        {
            Name = "DebugLines", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        private readonly ImmediateMesh _mesh = new();
        private readonly StandardMaterial3D _material = new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };

        internal DebugLineGroup(System.Collections.Immutable.ImmutableArray<SceneDebugLine> lines)
        {
            Lines = lines;
            Root.Mesh = _mesh;
            _mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, _material);
            foreach (var line in lines)
            {
                _mesh.SurfaceSetColor(new(line.Color.X, line.Color.Y, line.Color.Z, line.Color.W));
                _mesh.SurfaceAddVertex(ToGodot(line.From));
                _mesh.SurfaceAddVertex(ToGodot(line.To));
            }
            _mesh.SurfaceEnd();
        }

        public void Dispose()
        {
            Root.Free();
            _mesh.Dispose();
            _material.Dispose();
        }
    }

    internal sealed class FogGroup : IDisposable
    {
        internal System.Collections.Immutable.ImmutableArray<SceneBounds> Regions { get; }
        internal Node3D Root { get; } = new() { Name = "LoadingFog" };
        private readonly List<BoxMesh> _meshes = [];
        private bool _disposed;
        private readonly ShaderMaterial _material;
        internal FogGroup(System.Collections.Immutable.ImmutableArray<SceneBounds> regions, ShaderMaterial material)
        {
            _material = material;
            Regions = regions;
            foreach (var region in regions)
            {
                var mesh = new BoxMesh { Size = ToGodot(region.Maximum - region.Minimum) };
                _meshes.Add(mesh);
                Root.AddChild(new MeshInstance3D
                {
                    Mesh = mesh, MaterialOverride = _material,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = ToGodot((region.Minimum + region.Maximum) / 2)
                });
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Root.Free();
            foreach (var mesh in _meshes) mesh.Dispose();
            _material.Dispose();
        }
    }

    private static Vector3 ToGodot(System.Numerics.Vector3 value) => new(value.X, value.Y, value.Z);
    private static Transform3D ToTransform(ScenePose pose) => new(new Basis(new Quaternion(pose.Rotation.X,
        pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W)), ToGodot(pose.Position));
}
