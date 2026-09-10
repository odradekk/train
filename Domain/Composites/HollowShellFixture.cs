using System.Collections.ObjectModel;

namespace Train.Domain.Composites;

public static class HollowShellFixture
{
    public const int CellCount = 168;
    public const string ShellLocalId = "vehicle-shell";
    public const string CoreLocalId = "identity-core";
    public const string ArchetypeLocalId = "dev-hollow-shell";
    public const string CompositeLocalId = "dev-hollow-shell";

    public static CompositeDefinitionId DefinitionId { get; } = new("core", CompositeLocalId);
    public static CompositeArchetypeId ArchetypeId { get; } = new("core", ArchetypeLocalId);
    public static LocalVoxelCoord CoreCell { get; } = new(1, 0, 1);
    public static LocalVoxelCoord RearBoundCell { get; } = new(7, 0, 1);
    public static CompositeContactAnchor FrontContact { get; } = new(1, -0.75, 1.5, CoreCell);
    public static CompositeContactAnchor RearContact { get; } = new(7, -0.75, 1.5, RearBoundCell);
    public static CompositeTransform DefaultTransform { get; } = CompositeTransform.Translation(8, 8, 8);
    public static VehicleEnvelopeBox Envelope { get; } = new(0, 0, -1, 8, 6, 4);

    public static IReadOnlyList<LocalVoxelCoord> Cells { get; } = new ReadOnlyCollection<LocalVoxelCoord>(BuildCells());

    public static bool IsShellCell(int x, int y, int z) =>
        x is >= 0 and <= 7 &&
        y is >= 0 and <= 5 &&
        z is >= -1 and <= 3 &&
        (x is 0 or 7 || y is 0 or 5 || z is -1 or 3);

    public static CompositeArchetype CreateArchetype() =>
        new(
            ArchetypeId,
            new ConstructionRules(
                requireNonEmptySixConnected: true,
                requireDeclaredSize: true,
                contactAnchorCount: 2,
                contactsMustBeDistinct: true,
                contactsMustBindExistingParts: true),
            new IdentityRetentionRules(Train.Domain.Voxels.VoxelTags.IdentityCore));

    private static LocalVoxelCoord[] BuildCells()
    {
        var cells = new List<LocalVoxelCoord>(CellCount);
        for (var x = 0; x <= 7; x++)
        for (var y = 0; y <= 5; y++)
        for (var z = -1; z <= 3; z++)
        {
            if (IsShellCell(x, y, z))
                cells.Add(new LocalVoxelCoord(x, y, z));
        }

        if (cells.Count != CellCount)
            throw new InvalidOperationException($"Hollow shell fixture must contain {CellCount} cells.");
        return cells.ToArray();
    }
}
