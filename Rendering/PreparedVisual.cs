using Godot;
using Train.App.Assets;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed class PreparedVisual : IDisposable
{
    private readonly AssetResourceCache _cache;
    private readonly List<IDisposable> _owned = [];
    private readonly List<ShaderMaterial> _materials = [];
    private bool _disposed;
    private bool _nodeReleased;
    private bool _constructionFogExempt;

    internal PreparedVisual(AssetResourceCache cache, SceneVisual visual)
    {
        _cache = cache;
        Visual = visual;
    }

    internal Node3D Node { get; } = new();
    internal SceneVisual Visual { get; private set; }
    internal void ExemptConstructionFog(bool exempt, Shader normal, Shader followed, Shader normalModel, Shader followedModel)
    {
        if (_constructionFogExempt == exempt) return;
        if (exempt && Visual is SceneVisual.Model model && _materials.Count == 0)
        {
            // 普通模型共享材质；跟随豁免只切换本实例的副本，保留原租约到释放。
            var instance = Node.GetChild<MeshInstance3D>(0);
            for (var index = 0; index < model.Materials.Length; index++)
                instance.SetSurfaceOverrideMaterial(index,
                    Material((ShaderMaterial)instance.GetSurfaceOverrideMaterial(index).Duplicate()));
        }
        _constructionFogExempt = exempt;
        foreach (var material in _materials)
        {
            if (material.Shader == normal || material.Shader == followed)
                material.Shader = exempt ? followed : normal;
            else if (material.Shader == normalModel || material.Shader == followedModel)
                material.Shader = exempt ? followedModel : normalModel;
        }
        foreach (var material in _owned.OfType<StandardMaterial3D>())
        {
            material.StencilMode = exempt ? BaseMaterial3D.StencilModeEnum.Custom : BaseMaterial3D.StencilModeEnum.Disabled;
            material.StencilFlags = (int)BaseMaterial3D.StencilFlagsEnum.Write;
            material.StencilReference = 1;
        }
    }
    internal T Own<T>(T value) where T : IDisposable { _owned.Add(value); return value; }
    internal ShaderMaterial Material(ShaderMaterial value) { _materials.Add(value); return Own(value); }

    internal bool CanUpdate(SceneVisual visual) => (Visual, visual) switch
    {
        (SceneVisual.Geometry old, SceneVisual.Geometry next) => old.Mesh.Vertices == next.Mesh.Vertices &&
            old.Mesh.TriangleIndices == next.Mesh.TriangleIndices && ((old.Mesh.Material, next.Mesh.Material) switch
            {
                (SceneMaterial.Voxel left, SceneMaterial.Voxel right) => left.Texture == right.Texture && left.Category == right.Category,
                (SceneMaterial.BuiltIn left, SceneMaterial.BuiltIn right) => left.Kind == right.Kind,
                _ => false
            }),
        (SceneVisual.Model old, SceneVisual.Model next) => old.Asset == next.Asset && old.Paint == next.Paint &&
            old.Surface == next.Surface && old.Materials.SequenceEqual(next.Materials),
        (SceneVisual.Wheelset old, SceneVisual.Wheelset next) => old.GaugeCells == next.GaugeCells &&
            old.RadiusCells == next.RadiusCells && old.WidthCells == next.WidthCells,
        (SceneVisual.Missing old, SceneVisual.Missing next) => old == next,
        _ => false
    };

    // 显示器先准备完整候选，再在整帧接入阶段调用；准备期间不能改变旧帧。
    internal void Update(SceneVisual visual)
    {
        _cache.RequireMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!CanUpdate(visual)) throw new ArgumentException("表现资源身份已改变。", nameof(visual));
        if (Visual == visual) return;
        switch (visual)
        {
            case SceneVisual.Geometry geometry:
                foreach (var material in _materials)
                {
                    SceneResources.SetSurface(material, geometry.Surface);
                    if (geometry.Mesh.Material is SceneMaterial.Voxel voxel) SceneResources.SetPaint(material, voxel.Paint);
                }
                break;
            case SceneVisual.Model model:
                Node.GetChild<MeshInstance3D>(0).SetInstanceShaderParameter(SceneResources.RelativeHeightPlane,
                    new Vector4(model.RelativeHeightPlane.X, model.RelativeHeightPlane.Y,
                        model.RelativeHeightPlane.Z, model.RelativeHeightPlane.W));
                break;
            case SceneVisual.Wheelset wheelset:
                Node.Rotation = new((float)wheelset.RotationRadians, 0, 0);
                break;
        }
        Visual = visual;
    }

    internal bool Release(SceneDisplay.PreparationSlice slice, SceneDisplay.RetirementCosts costs, bool singleStep)
    {
        _cache.RequireMainThread();
        if (_disposed) return true;
        if (!_nodeReleased)
        {
            if (!costs.Free(slice, Node)) return false;
            _nodeReleased = true;
            if (singleStep && _owned.Count > 0) return false;
        }
        while (_owned.Count > 0)
        {
            if (!costs.Release(slice, _owned[^1])) return false;
            _owned.RemoveAt(_owned.Count - 1);
            if (singleStep && _owned.Count > 0) return false;
        }
        _disposed = true;
        return true;
    }

    public void Dispose()
    {
        _cache.RequireMainThread();
        if (_disposed) return;
        _disposed = true;
        if (!_nodeReleased) Node.Free();
        for (var index = _owned.Count - 1; index >= 0; index--) _owned[index].Dispose();
        _owned.Clear();
    }
}
