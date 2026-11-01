using Godot;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneDisplay
{
    private void Commit(PreparedFrame prepared)
    {
        using var diagnostic = SceneDisplayDiagnostics.Call(Diagnose, prepared.Candidate,
            prepared.Frame.Update.Frame, SceneDisplayWork.Commit, Engine.GetProcessFrames());
        var indexStarted = SceneDisplayDiagnostics.Start();
        _cache.RequireMainThread();
        var previous = DisplayedFrame;
        var update = prepared.Frame.Update;
        var next = prepared.Objects;
        var upserts = update.Upserts.Select(item => item.Id).ToHashSet();
        var changed = update.Base is null ? _objects.Keys.Concat(next.Keys).ToHashSet() :
            update.Removed.Concat(upserts).ToHashSet();
        SceneDisplayDiagnostics.Finish(indexStarted, "Commit.CollectChanges");
        // 先摘除退役或换父的对象，避免退役父节点释放仍在使用的子对象。
        foreach (var id in changed)
        {
            if (!_objects.TryGetValue(id, out var item)) continue;
            var surviving = next.GetValueOrDefault(item.Source.Id);
            if (surviving is null || item.Source.Parent != surviving.Source.Parent)
            {
                using var interval = SceneDisplayDiagnostics.Begin("Commit.DetachObject", id);
                item.Root.GetParent()?.RemoveChild(item.Root);
            }
        }
        if (Root.Position != Vector3.Zero) Root.Position = Vector3.Zero;
        var transformed = upserts.Concat(_posedObjects).ToHashSet();
        if (update.ShiftExistingRoots != System.Numerics.Vector3.Zero) transformed.UnionWith(_rootObjects);
        foreach (var id in transformed)
            if (next.TryGetValue(id, out var item))
            {
                using var interval = SceneDisplayDiagnostics.Begin("Commit.UpdateTransform", id);
                item.Root.Transform = ToTransform(item.Source.Pose);
            }
        foreach (var id in upserts)
        {
            var item = next[id];
            for (var i = 0; i < item.Visuals.Length; i++)
            {
                var visual = item.Visuals[i];
                using (SceneDisplayDiagnostics.Begin("Commit.UpdateVisual", id, i,
                    item.Source.Visuals[i] is SceneVisual.Geometry geometry ? geometry.Mesh : null))
                    visual.Update(item.Source.Visuals[i]);
                if (visual.Node.GetParent() is null)
                {
                    using var interval = SceneDisplayDiagnostics.Begin("Commit.AttachVisual", id, i,
                        item.Source.Visuals[i] is SceneVisual.Geometry attached ? attached.Mesh : null);
                    item.Root.AddChild(visual.Node);
                }
            }
        }
        foreach (var id in upserts)
        {
            var item = next[id];
            if (item.Root.GetParent() is null)
            {
                using var interval = SceneDisplayDiagnostics.Begin("Commit.AttachObject", id);
                (item.Source.Parent is { } parent ? next[parent].Root : Root).AddChild(item.Root);
            }
        }
        var old = _objects;
        var oldFog = _fog;
        using (SceneDisplayDiagnostics.Begin("Commit.ActivateFog")) prepared.Fog.Activate();
        if (!ReferenceEquals(oldFog?.Root, prepared.Fog.Root))
        {
            using var interval = SceneDisplayDiagnostics.Begin("Commit.ReplaceFogRoot");
            oldFog?.Root.GetParent()?.RemoveChild(oldFog.Root);
            Root.AddChild(prepared.Fog.Root);
        }
        _fog = prepared.Fog;
        var changedAncestry = new List<SceneObjectId>();
        foreach (var id in changed)
        {
            using var interval = SceneDisplayDiagnostics.Begin("Commit.UpdateIndexes", id);
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
        foreach (var id in changedAncestry)
        {
            using var interval = SceneDisplayDiagnostics.Begin("Commit.MarkFogSubtree", id);
            MarkFogSubtree(id);
        }
        var publishStarted = SceneDisplayDiagnostics.Start();
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
        SceneDisplayDiagnostics.Finish(publishStarted, "Commit.PublishFrame");
        using (SceneDisplayDiagnostics.Begin("Commit.ApplyEffects"))
            _effects.Apply(previous, prepared.Frame, _effectObjects);
        foreach (var id in changed)
        {
            if (!old.TryGetValue(id, out var item)) continue;
            var surviving = next.GetValueOrDefault(item.Source.Id);
            var visualIndex = 0;
            foreach (var visual in item.Visuals)
            {
                if (surviving is null || !surviving.Visuals.Contains(visual))
                {
                    using var interval = SceneDisplayDiagnostics.Begin("Commit.DisposeVisual", id, visualIndex,
                        visual.Visual is SceneVisual.Geometry geometry ? geometry.Mesh : null);
                    visual.Dispose();
                }
                visualIndex++;
            }
            if (surviving is null)
            {
                using var interval = SceneDisplayDiagnostics.Begin("Commit.FreeRoot", id);
                item.Root.Free();
            }
        }
        if (!ReferenceEquals(oldFog, _fog))
        {
            using var interval = SceneDisplayDiagnostics.Begin("Commit.DisposeFog");
            oldFog?.Dispose();
        }
        Observe?.Invoke(new(prepared.Candidate, update.Frame, null, SceneDisplayStage.FrameCommitted));
    }

}
