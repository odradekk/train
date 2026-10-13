using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed class SceneDisplay : IDisposable
{
    internal sealed record DisplayObject(SceneObject Source, Node3D Root, PreparedVisual[] Visuals);
    private readonly SceneResources _resources;
    private readonly AssetResourceCache _cache;
    private Dictionary<SceneObjectId, DisplayObject> _objects = [];
    private FogGroup? _fog;
    private bool _disposed;

    internal Node3D Root { get; } = new() { Name = "SceneDisplay" };
    internal ISceneFrame? DisplayedFrame { get; private set; }
    internal IReadOnlyDictionary<SceneObjectId, DisplayObject> Objects => _objects;

    internal SceneDisplay(AssetResourceCache cache, IContentView content)
    {
        _cache = cache;
        _resources = new(cache, content);
    }

    internal PreparedFrame? Prepare(ISceneFrame frame)
    {
        _cache.RequireMainThread();
        if (frame.Readiness == SceneReadiness.Loading ||
            frame.Update.Base is { } baseline && baseline != DisplayedFrame?.Update.Frame)
            return null;
        var update = frame.Update;
        var next = update.Base is null ? new Dictionary<SceneObjectId, DisplayObject>() : new(_objects);
        var created = new List<PreparedVisual>();
        var roots = new List<Node3D>();
        FogGroup? fog = null;
        try
        {
            foreach (var id in update.Removed) next.Remove(id);
            foreach (var source in update.Upserts)
            {
                _objects.TryGetValue(source.Id, out var previous);
                var root = previous?.Root ?? new Node3D { Name = $"Object{source.Id.Value}" };
                if (previous is null) roots.Add(root);
                var visuals = new PreparedVisual[source.Visuals.Length];
                for (var i = 0; i < visuals.Length; i++)
                {
                    if (previous is not null && i < previous.Visuals.Length &&
                        previous.Visuals[i].CanUpdate(source.Visuals[i]))
                        visuals[i] = previous.Visuals[i];
                    else
                    {
                        visuals[i] = _resources.Prepare(source.Visuals[i]);
                        created.Add(visuals[i]);
                    }
                }
                next[source.Id] = new(source, root, visuals);
            }
            foreach (var item in next.Values)
                if (item.Source.Parent is { } parent && !next.ContainsKey(parent))
                    throw new InvalidOperationException("显示对象的父对象不存在。");
            if (_fog is null || !_fog.Regions.SequenceEqual(update.Environment.FoggedRegions))
                fog = new(update.Environment.FoggedRegions);
            return new(this, frame, next, created, roots, fog ?? _fog!, fog is not null);
        }
        catch
        {
            foreach (var visual in created) visual.Dispose();
            foreach (var root in roots) root.Free();
            fog?.Dispose();
            throw;
        }
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
        foreach (var item in next.Values)
        {
            if (upserts.Contains(item.Source.Id))
            {
                var pose = item.Source.Pose;
                item.Root.Transform = new(new Basis(new Quaternion(pose.Rotation.X, pose.Rotation.Y,
                    pose.Rotation.Z, pose.Rotation.W)), ToGodot(pose.Position));
                for (var i = 0; i < item.Visuals.Length; i++)
                {
                    var visual = item.Visuals[i];
                    visual.Update(item.Source.Visuals[i]);
                    if (visual.Node.GetParent() is null) item.Root.AddChild(visual.Node);
                }
            }
            else if (item.Source.Parent is null) item.Root.Position += ToGodot(update.ShiftExistingRoots);
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
        Root.Visible = true;
    }

    public void Dispose()
    {
        _cache.RequireMainThread();
        if (_disposed) return;
        _disposed = true;
        foreach (var item in _objects.Values) item.Root.GetParent()?.RemoveChild(item.Root);
        foreach (var item in _objects.Values)
        {
            foreach (var visual in item.Visuals) visual.Dispose();
            item.Root.Free();
        }
        _objects.Clear();
        _fog?.Dispose();
        _fog = null;
        Root.Free();
        _resources.Dispose();
        DisplayedFrame = null;
    }

    internal sealed class PreparedFrame(SceneDisplay owner, ISceneFrame frame,
        Dictionary<SceneObjectId, DisplayObject> objects, List<PreparedVisual> visuals,
        List<Node3D> roots, FogGroup fog, bool ownsFog) : IDisposable
    {
        internal bool Transferred { get; set; }
        private bool _disposed;
        internal ISceneFrame Frame { get; } = frame;
        internal Dictionary<SceneObjectId, DisplayObject> Objects { get; } = objects;
        internal FogGroup Fog { get; } = fog;
        internal void Commit()
        {
            owner.Commit(this);
        }
        public void Dispose()
        {
            if (Transferred || _disposed) return;
            _disposed = true;
            foreach (var visual in visuals) visual.Dispose();
            foreach (var root in roots) if (GodotObject.IsInstanceValid(root)) root.Free();
            if (ownsFog) Fog.Dispose();
        }
    }

    internal sealed class FogGroup : IDisposable
    {
        internal System.Collections.Immutable.ImmutableArray<SceneBounds> Regions { get; }
        internal Node3D Root { get; } = new() { Name = "LoadingFog" };
        private readonly List<BoxMesh> _meshes = [];
        private bool _disposed;
        private readonly StandardMaterial3D _material = new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color("c9d6e0"), CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };
        internal FogGroup(System.Collections.Immutable.ImmutableArray<SceneBounds> regions)
        {
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
}
