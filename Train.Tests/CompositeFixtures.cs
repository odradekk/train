using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;
using Train.Logging;

namespace Train.Tests;

internal static class CompositeFixtures
{
    public static string Archetype(
        string id = "dev-hollow-shell",
        int contactCount = 2,
        string coreTag = "identity_core") =>
        $"{{\"id\":\"{id}\",\"construction\":{{\"require_nonempty_six_connected\":true,\"require_declared_size\":true,\"contact_anchor_count\":{contactCount},\"contacts_must_be_distinct\":true,\"contacts_must_bind_existing_parts\":true}},\"identity_retention\":{{\"require_core_tag\":\"{coreTag}\"}}}}";

    public static string Contact(double x, double y, double z, int cx, int cy, int cz) =>
        $"{{\"x\":{VoxelDefinition.FormatMass(x)},\"y\":{VoxelDefinition.FormatMass(y)},\"z\":{VoxelDefinition.FormatMass(z)},\"cell\":{{\"x\":{cx},\"y\":{cy},\"z\":{cz}}}}}";

    public static string Voxel(int x, int y, int z, string definition = "vehicle-shell", string orientation = "px_py") =>
        $"{{\"x\":{x},\"y\":{y},\"z\":{z},\"definition\":\"{definition}\",\"orientation\":\"{orientation}\"}}";

    public static string Composite(
        string id = "dev-hollow-shell",
        string name = "开发空心壳",
        string description = "测试车辆",
        string archetype = "dev-hollow-shell",
        string size = "small",
        string? voxels = null,
        string? contacts = null)
    {
        voxels ??= string.Join(",",
            Voxel(0, 0, 0),
            Voxel(1, 0, 0, "identity-core"),
            Voxel(2, 0, 0));
        contacts ??= string.Join(",",
            Contact(1, -0.75, 1.5, 1, 0, 0),
            Contact(2, -0.75, 1.5, 2, 0, 0));
        return $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"description\":\"{description}\",\"archetype\":\"{archetype}\",\"size_class\":\"{size}\",\"voxels\":[{voxels}],\"contacts\":[{contacts}]}}";
    }

    public static string File(string? displayName, string archetypes, string composites)
    {
        var display = displayName is null ? "" : $"\"display_name\":\"{displayName}\",";
        return $"{{\"schema_version\":1,{display}\"archetypes\":[{archetypes}],\"composites\":[{composites}]}}";
    }

    public static MemoryContentSource VehiclePackage()
    {
        var source = ContentFixtures.ValidIron();
        source.AddVoxelFile("core", "voxels/vehicle-shell.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("vehicle-shell", "壳体", "壳体说明", value: 10, tagsJson: "[]")));
        source.AddVoxelFile("core", "voxels/identity-core.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("identity-core", "核心", "核心说明", value: 10, tagsJson: "[\"identity_core\"]")));
        ContentFixtures.AddSquareImages(source, "iron.png");
        source.AddCompositeFile("core", "composites/dev-hollow-shell.composites.json", File(null, Archetype(), Composite()));
        return source;
    }

    public static string CompositeFromFixture()
    {
        var document = System.Text.Json.JsonDocument.Parse(FixtureFileText());
        return document.RootElement.GetProperty("composites")[0].GetRawText();
    }

    public static string FixtureFileText()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var path = Path.Combine(repo, "builtin", "core", "resources", "composites", "dev-hollow-shell.composites.json");
        if (!System.IO.File.Exists(path))
            path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "builtin", "core", "resources", "composites", "dev-hollow-shell.composites.json"));
        return System.IO.File.ReadAllText(path);
    }

    public static MemoryContentSource FormalFixturePackage()
    {
        var source = ContentFixtures.ValidIron();
        source.AddVoxelFile("core", "voxels/vehicle-shell.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("vehicle-shell", "壳体", "壳体说明", value: 10, tagsJson: "[]")));
        source.AddVoxelFile("core", "voxels/identity-core.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("identity-core", "核心", "核心说明", value: 10, tagsJson: "[\"identity_core\"]")));
        ContentFixtures.AddSquareImages(source, "iron.png");
        source.AddCompositeFile("core", "composites/dev-hollow-shell.composites.json", FixtureFileText());
        return source;
    }

    public static MemoryContentSource FormalFixtureWithIllegalDesigns()
    {
        var source = FormalFixturePackage();
        source.AddVoxelFile("core", "voxels/locked-shell.voxels.json", ContentFixtures.GoodsFile(
            ContentFixtures.Good("locked-shell", "锁定壳体", "朝向锁定", value: 10, tagsJson: "[]", orientations: "none")));
        source.AddCompositeFile("core", "composites/disconnected.composites.json", File(null, "",
            Composite(
                "disconnected",
                voxels: string.Join(",", Voxel(0, 0, 0), Voxel(2, 0, 0, "identity-core")),
                contacts: string.Join(",", Contact(0, -0.75, 0.5, 0, 0, 0), Contact(2, -0.75, 0.5, 2, 0, 0)))));
        source.AddCompositeFile("core", "composites/duplicate-cell.composites.json", File(null, "",
            Composite(
                "duplicate-cell",
                voxels: string.Join(",", Voxel(0, 0, 0), Voxel(0, 0, 0), Voxel(1, 0, 0, "identity-core")),
                contacts: string.Join(",", Contact(0, -0.75, 0.5, 0, 0, 0), Contact(1, -0.75, 0.5, 1, 0, 0)))));
        source.AddCompositeFile("core", "composites/missing-contact.composites.json", File(null, "",
            Composite("missing-contact", contacts: "")));
        source.AddCompositeFile("core", "composites/extra-contact.composites.json", File(null, "",
            Composite(
                "extra-contact",
                contacts: string.Join(",",
                    Contact(0, -0.75, 0.5, 0, 0, 0),
                    Contact(1, -0.75, 0.5, 1, 0, 0),
                    Contact(2, -0.75, 0.5, 2, 0, 0)))));
        source.AddCompositeFile("core", "composites/unbound-contact.composites.json", File(null, "",
            Composite(
                "unbound-contact",
                contacts: string.Join(",", Contact(0, -0.75, 0.5, 0, 0, 0), Contact(3, -0.75, 0.5, 3, 0, 0)))));
        source.AddCompositeFile("core", "composites/envelope-overflow.composites.json", File(null, "",
            Composite(
                "envelope-overflow",
                voxels: string.Join(",",
                    Voxel(0, 0, 0),
                    Voxel(1, 0, 0, "identity-core"),
                    Voxel(2, 0, 0),
                    Voxel(3, 0, 0),
                    Voxel(4, 0, 0),
                    Voxel(5, 0, 0),
                    Voxel(6, 0, 0),
                    Voxel(7, 0, 0),
                    Voxel(8, 0, 0)),
                contacts: string.Join(",", Contact(1, -0.75, 1.5, 1, 0, 0), Contact(7, -0.75, 1.5, 7, 0, 0)))));
        source.AddCompositeFile("core", "composites/locked-orientation.composites.json", File(null, "",
            Composite(
                "locked-orientation",
                voxels: string.Join(",", Voxel(0, 0, 0, "locked-shell", "pz_py"), Voxel(1, 0, 0, "identity-core")),
                contacts: string.Join(",", Contact(0, -0.75, 0.5, 0, 0, 0), Contact(1, -0.75, 0.5, 1, 0, 0)))));
        return source;
    }

    public static (ContentReloader Reloader, LoggerSession Logger) Load(MemoryContentSource? source = null)
    {
        var session = LoggerSession.Create();
        var reloader = ContentFixtures.Create(source ?? VehiclePackage(), session.Logger);
        var result = reloader.Reload();
        if (!result.IsCommitted)
            throw new InvalidOperationException("Composite fixture catalog failed to commit.");
        return (reloader, session);
    }
}
