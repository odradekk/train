using System.Collections.Immutable;
using static Train.Content.Loading.DefinitionJson;
using C = Train.Content.Contracts;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal sealed record ParsedTerrain(C.TerrainAppearanceProjection Appearance, string Path);

internal static class TerrainParser
{
    internal static ParsedTerrain? Parse(byte[] bytes, LoadPackage package, string path)
    {
        using var document = ContentDocument.Parse(bytes, package, path);
        if (document is null) return null;
        C.ContentReference? subject = null;
        try
        {
            var root = document.RootElement;
            Object(root, "", "schema_version", "id", "display", "surface_voxel", "subsurface_voxel", "rock_voxel",
                "local_height_noise", "decorations", "decoration_target_per_square_meter", "railway_models");
            var json = new DefinitionJson(root, package, path, "terrain-type", 1);
            var id = new K.TerrainTypeId(json.Id);
            subject = new C.ContentReference.Core(new K.CoreDefinitionReference.Terrain(id));
            var noise = Items(Required(root, "local_height_noise", ""), "/local_height_noise").Select(item =>
            {
                Object(item.Value, item.Field, "scale_meters", "amplitude");
                return new K.NoiseOctave(Number(Required(item.Value, "scale_meters", item.Field), item.Field + "/scale_meters", positive: true),
                    Number(Required(item.Value, "amplitude", item.Field), item.Field + "/amplitude", 0));
            }).OrderBy(n => n.ScaleMeters).ThenBy(n => n.Amplitude).ToImmutableArray();
            var decorations = Items(Required(root, "decorations", ""), "/decorations").Select(item =>
            {
                Object(item.Value, item.Field, "scene_model", "weight");
                return new C.TerrainDecoration(new(json.Reference(Required(item.Value, "scene_model", item.Field), item.Field + "/scene_model")),
                    Int32(Required(item.Value, "weight", item.Field), item.Field + "/weight", 1));
            }).OrderBy(d => d.SceneModel.Value, StringComparer.Ordinal).ToImmutableArray();
            Unique(decorations.Select(d => d.SceneModel), "/decorations");
            var density = Number(Required(root, "decoration_target_per_square_meter", ""), "/decoration_target_per_square_meter", 0);
            if (decorations.IsEmpty && density != 0) Fail("/decoration_target_per_square_meter", "没有装饰时密度必须为 0。");
            var models = Items(Required(root, "railway_models", ""), "/railway_models").Select(item =>
            {
                Object(item.Value, item.Field, "tracks", "bridge", "tunnel_portal");
                return new C.RailwaySceneModels(Int32(Required(item.Value, "tracks", item.Field), item.Field + "/tracks", 1),
                    new(json.Reference(Required(item.Value, "bridge", item.Field), item.Field + "/bridge")),
                    new(json.Reference(Required(item.Value, "tunnel_portal", item.Field), item.Field + "/tunnel_portal")));
            }).OrderBy(m => m.Tracks).ToImmutableArray();
            Unique(models.Select(m => m.Tracks), "/railway_models");
            return new(new(id, Voxel("surface_voxel"), Voxel("subsurface_voxel"), Voxel("rock_voxel"), noise,
                decorations, density, models, Display(Required(root, "display", ""))), path);
            K.VoxelDefinitionId Voxel(string key) => new(json.Reference(Required(root, key, ""), "/" + key));
        }
        catch (DefinitionFormatException error)
        {
            package.Reject(error.Code, path, error.Field, error.Message, subject);
            return null;
        }
    }
}
