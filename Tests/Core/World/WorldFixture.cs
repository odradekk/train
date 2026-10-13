using Train.Core.Contracts;

namespace Train.Core.Tests.World;

internal static class WorldFixture
{
    public static DesignHash Vehicle { get; } = new(new string('A', 64));
    public static DesignHash Station { get; } = new(new string('B', 64));
    public static TerrainTypeId Plain { get; } = new("fixture:plain");
    public static VoxelDefinitionId Material { get; } = new("fixture:material");
    public static SemanticRevision Revision { get; } = new(new string('C', 64));
    public static RuleSelection Rules { get; } = new(new("fixture:population"), new("fixture:market"), new("fixture:construction"), new("fixture:railway"), new("fixture:combat"));

    public static WorldGenerationInput Input => new(42, "stage1-world-v1", Revision,
        new(10, 10, 10, 1, [], [], [], -1, 0,
            [new(Plain, 0, 1, 0, 1, 1, 1, 2)], 1, 1, 0, 0, [], [],
            new(2, 1, 1, 0), new(1, 1, 100_000, 1_000_000),
            new(false, 0, 0, 0, []), new(0, 0, true, true, 1, Vehicle), Rules),
        new(new(1000), [new([Vehicle], [])]));

    public static CoreContentProjection Content => new(new(new string('D', 64)),
        [new(new GoodId.Voxel(Material), Revision, new(1), 1, true, null)],
        [Design(Vehicle, new DesignKind.Vehicle(SizeClass.Small, 1),
            [new PartCapability.Control(), new PartCapability.Propulsion(100, 10), new PartCapability.Storage(100)]),
         Design(Station, new DesignKind.Building(PlotSize.Station, BuildingKind.Station, []),
            [new PartCapability.Platform()])],
        [], [],
        [new(Rules.Population, Revision, [], [], 0.5, 1, 10, 0, 0, 0)],
        [new(Rules.Market, Revision, 0, 0, 0, new(new(0), new(10000), new(10000), new(0)),
            [], new(10000), new(10000), [], 1, 10, new(0), new(0), new(0), 1)],
        [new(Rules.Construction, Revision, 1, 1, 1, 1, 1, 0, 0)],
        [new(Rules.Railway, Revision, 1, 1, 1, Material, 1, 1, 1, 1)],
        [new(Rules.Combat, Revision, 1, 0, 0, 0, 0, 1)],
        [new(new("fixture:town"), Revision, 1, [Plain], 10, 10)],
        [new(new("fixture:town-template"), new("fixture:town"), 1, [new(1, PlotSize.Station, Station)], [], [], [])],
        [], [], []);

    private static DesignProjection Design(DesignHash hash, DesignKind kind,
        System.Collections.Immutable.ImmutableArray<PartCapability> capabilities) =>
        new(hash, new(new string('E', 64)), kind,
            [new(new(0), new CoreDefinitionReference.Voxel(Material), new GoodId.Voxel(Material),
                100, 1, new(1), capabilities, false)],
            100, 1, 1, new(1), [], [], 0, [], true, []);
}
