using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneResources
{
    internal PreparedVisual Prepare(SceneVisual visual, PackedSceneMesh? data = null)
    {
        using var prepareInterval = SceneDisplayDiagnostics.Begin("Prepare.Resources");
        _cache.RequireMainThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        PreparedVisual result;
        using (SceneDisplayDiagnostics.Begin("Prepare.RootNode"))
            result = new PreparedVisual(_cache, visual);
        try
        {
            switch (visual)
            {
                case SceneVisual.Geometry geometry:
                    var mesh = result.Own(AcquireGeometry(geometry.Mesh, data));
                    ShaderMaterial material;
                    using (SceneDisplayDiagnostics.Begin("Prepare.GeometryMaterial", mesh: geometry.Mesh))
                        material = GeometryMaterial(result, geometry.Mesh.Material);
                    using (SceneDisplayDiagnostics.Begin("Prepare.SetSurface", mesh: geometry.Mesh))
                        SetSurface(material, geometry.Surface);
                    MeshInstance3D geometryInstance;
                    using (SceneDisplayDiagnostics.Begin("Prepare.GeometryNode", mesh: geometry.Mesh))
                        geometryInstance = new MeshInstance3D { Mesh = mesh.Value, MaterialOverride = material };
                    using (SceneDisplayDiagnostics.Begin("Prepare.GeometryAddChild", mesh: geometry.Mesh))
                        result.Node.AddChild(geometryInstance);
                    break;
                case SceneVisual.Model model:
                    ModelAsset asset;
                    using (SceneDisplayDiagnostics.Begin("Prepare.ModelResources"))
                        asset = result.Own(_cache.AcquireModel(Asset(model.Asset))).Value;
                    if (asset.Surfaces.Length != model.Materials.Length)
                        throw new InvalidOperationException("模型材质类别与资产表面数量不匹配。");
                    MeshInstance3D instance;
                    using (SceneDisplayDiagnostics.Begin("Prepare.ModelNode"))
                        instance = new MeshInstance3D { Mesh = asset.Mesh };
                    using (SceneDisplayDiagnostics.Begin("Prepare.ModelAddChild"))
                        result.Node.AddChild(instance);
                    for (var index = 0; index < asset.Surfaces.Length; index++)
                    {
                        using var materialInterval = SceneDisplayDiagnostics.Begin("Prepare.ModelMaterial");
                        var surface = result.Own(AcquireModelMaterial(model, index, asset.Surfaces[index])).Value;
                        instance.SetSurfaceOverrideMaterial(index, surface);
                    }
                    instance.SetInstanceShaderParameter(SceneResources.RelativeHeightPlane, new Vector4(model.RelativeHeightPlane.X,
                        model.RelativeHeightPlane.Y, model.RelativeHeightPlane.Z, model.RelativeHeightPlane.W));
                    break;
                case SceneVisual.Wheelset wheelset:
                    var wheelMaterial = result.Material(SurfaceMaterial(MaterialCategory.BareMetal, new Color(0.26f, 0.28f, 0.30f)));
                    var wheelMarkerMaterial = result.Material(SurfaceMaterial(MaterialCategory.BareMetal, new Color("#8e9398")));
                    foreach (var side in new[] { -1, 1 })
                    {
                        var cylinder = result.Own(new CylinderMesh
                        {
                            TopRadius = (float)wheelset.RadiusCells,
                            BottomRadius = (float)wheelset.RadiusCells,
                            Height = (float)wheelset.WidthCells,
                            RadialSegments = 24
                        });
                        var wheel = new MeshInstance3D
                        {
                            Mesh = cylinder, MaterialOverride = wheelMaterial,
                            Position = new(side * (float)wheelset.GaugeCells / 2, 0, 0),
                            Rotation = new(0, 0, Mathf.Pi / 2)
                        };
                        result.Node.AddChild(wheel);
                        for (var spoke = 0; spoke < 4; spoke++)
                        {
                            var angle = spoke * Mathf.Pi / 2;
                            wheel.AddChild(new MeshInstance3D
                            {
                                Mesh = result.Own(new BoxMesh { Size = new((float)wheelset.RadiusCells * 0.95f, 0.015f, (float)wheelset.RadiusCells * 0.2f) }),
                                MaterialOverride = wheelMarkerMaterial,
                                Position = new Vector3((float)wheelset.RadiusCells * 0.475f,
                                    -side * ((float)wheelset.WidthCells / 2 + 0.01f), 0).Rotated(Vector3.Up, angle),
                                Rotation = new(0, angle, 0)
                            });
                        }
                    }
                    result.Node.Rotation = new((float)wheelset.RotationRadians, 0, 0);
                    break;
                case SceneVisual.Missing missing:
                    var marker = result.Own(new BoxMesh { Size = Vector3.One * (missing.KnownCell ? 0.85f : 0.45f) });
                    var markerMaterial = result.Own(new StandardMaterial3D
                    {
                        AlbedoColor = Colors.Magenta,
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
                    });
                    result.Node.AddChild(new MeshInstance3D
                    {
                        Mesh = marker, MaterialOverride = markerMaterial,
                        Position = missing.KnownCell ? Vector3.One / 2 : Vector3.Zero
                    });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(visual));
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

}
