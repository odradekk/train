using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using System.Collections.ObjectModel;
using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public static class BridgeFixture
{
    public const int CellCount = 55;
    public const string HeavyCoreLocalId = "identity-core-heavy";
    public const string CompositeLocalId = "dev-bridge";
    public const string RightHeavyLocalId = "dev-bridge-right-heavy";

    public static CompositeDefinitionId DefinitionId { get; } = new("core", CompositeLocalId);
    public static CompositeDefinitionId RightHeavyDefinitionId { get; } = new("core", RightHeavyLocalId);
    public static VoxelDefinitionId HeavyCoreDefinitionId { get; } = new("core", HeavyCoreLocalId);
    public static LocalVoxelCoord LeftCoreCell { get; } = new(1, 1, 1);
    public static LocalVoxelCoord RightCoreCell { get; } = new(5, 1, 1);
    public static LocalVoxelCoord BridgeCell { get; } = new(3, 1, 1);
    public static LocalVoxelCoord SecondBridgeCell { get; } = new(3, 0, 1);
    public static LocalVoxelCoord FrontBoundCell { get; } = new(1, 0, 1);
    public static LocalVoxelCoord RearBoundCell { get; } = new(6, 0, 1);
    public static CompositeContactAnchor FrontContact { get; } = new(1, -0.75, 1.5, FrontBoundCell);
    public static CompositeContactAnchor RearContact { get; } = new(7, -0.75, 1.5, RearBoundCell);
    public static CompositeTransform DefaultTransform { get; } = HollowShellFixture.DefaultTransform;

    public static IReadOnlyList<LocalVoxelCoord> Cells { get; } = new ReadOnlyCollection<LocalVoxelCoord>(BuildCells());

    public static bool IsBridgeCell(int x, int y, int z) =>
        (x, y, z) == (3, 1, 1) ||
        (x is >= 0 and <= 2 && y is >= 0 and <= 2 && z is >= 0 and <= 2) ||
        (x is >= 4 and <= 6 && y is >= 0 and <= 2 && z is >= 0 and <= 2);

    public static bool IsLeftCell(LocalVoxelCoord cell) =>
        cell.X is >= 0 and <= 2 && cell.Y is >= 0 and <= 2 && cell.Z is >= 0 and <= 2;

    public static bool IsRightCell(LocalVoxelCoord cell) =>
        cell.X is >= 4 and <= 6 && cell.Y is >= 0 and <= 2 && cell.Z is >= 0 and <= 2;

    public static string VoxelLocalId(LocalVoxelCoord cell, bool rightCoreHeavy)
    {
        if (cell == LeftCoreCell || (cell == RightCoreCell && !rightCoreHeavy))
            return HollowShellFixture.CoreLocalId;
        if (cell == RightCoreCell)
            return HeavyCoreLocalId;
        return HollowShellFixture.ShellLocalId;
    }

    private static LocalVoxelCoord[] BuildCells()
    {
        var cells = new List<LocalVoxelCoord>(CellCount);
        for (var x = 0; x <= 6; x++)
        for (var y = 0; y <= 2; y++)
        for (var z = 0; z <= 2; z++)
        {
            if (IsBridgeCell(x, y, z))
                cells.Add(new LocalVoxelCoord(x, y, z));
        }

        if (cells.Count != CellCount)
            throw new InvalidOperationException($"Bridge fixture must contain {CellCount} cells.");
        return cells.ToArray();
    }
}
