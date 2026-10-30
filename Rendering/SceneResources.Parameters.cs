using Godot;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed partial class SceneResources
{
    internal static readonly StringName RelativeHeightPlane = "relative_height_plane";
    private static readonly StringName _alphaCutout = "alpha_cutout";
    private static readonly StringName _alphaCutoff = "alpha_cutoff";
    private static readonly StringName _voxelPaintMask = "voxel_paint_mask";
    private static readonly StringName _railSteel = "rail_steel";
    private static readonly StringName _railHeadColor = "rail_head_color";
    private static readonly StringName _baseColor = "base_color";
    private static readonly StringName _hasTexture = "has_texture";
    private static readonly StringName _baseTexture = "base_texture";
    private static readonly StringName _materialRoughness = "material_roughness";
    private static readonly StringName _materialMetallic = "material_metallic";
    private static readonly StringName _materialSpecular = "material_specular";
    private static readonly StringName _materialCategory = "material_category";
    private static readonly StringName _damageMaskParameter = "damage_mask";
    private static readonly StringName _hasPaint = "has_paint";
    private static readonly StringName _paintColor = "paint_color";
    private static readonly StringName _wearEnabled = "wear_enabled";
    private static readonly StringName _wearSeed = "wear_seed";
    private static readonly StringName _surfaceDamage = "surface_damage";
    private static readonly StringName _scorched = "scorched";

    internal static void SetPaint(ShaderMaterial material, PaintColor? paint)
    {
        material.SetShaderParameter(_hasPaint, paint is not null);
        material.SetShaderParameter(_paintColor, paint is { } value ? Color.FromHtml(value.Value) : Colors.White);
    }

    internal static void SetSurface(ShaderMaterial material, SurfaceState surface)
    {
        material.SetShaderParameter(_wearEnabled, surface.WearEnabled);
        material.SetShaderParameter(_wearSeed, new Vector2I(unchecked((int)(uint)surface.WearSeed), unchecked((int)(uint)(surface.WearSeed >> 32))));
        material.SetShaderParameter(_surfaceDamage, surface.Damage);
        material.SetShaderParameter(_scorched, surface.Scorched);
    }

}
