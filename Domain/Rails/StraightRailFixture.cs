using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public static class StraightRailFixture
{
    public static VoxelDefinitionId RailDefinitionId { get; } = new("core", "rail");

    public const string GeneratorVersion = "slice-4-straight-rail-v1";
    public const ulong Seed = 4;
    public const int WorldSizeX = 320;
    public const int WorldSizeY = 32;
    public const int WorldSizeZ = 32;
    public const int ChunkSize = 16;
    public const double Gauge = 1.5;
    public const double RailWidth = 0.12;
    public const double LeftRailCenterZ = 15.25;
    public const double RightRailCenterZ = 16.75;
    public const double RailMinY = 1.85;
    public const double RailMaxY = 2.0;
    public const double SleeperLength = 0.2;
    public const double SleeperWidth = 2.5;
    public const double SleeperCenterZ = 16.0;
    public const double SleeperMinY = 1.65;
    public const double SleeperMaxY = 1.85;
    public const double CenterY = 2.0;
    public const double CenterZ = 16.0;
    public const double LineStartX = 16.0;
    public const double SeamX = 144.0;
    public const double LineEndX = 272.0;
    public const int FirstSleeperIndex = 0;
    public const int LastSleeperIndex = 256;
    public const int SleeperCount = 257;
    public const int RailCellDurability = 100;
    public const double ContactClearance = 0.75;

    public static readonly WorldVoxelCoord EdgeCell = new(80, 1, 14);
    public static readonly WorldVoxelCoord LeftRailSample = new(80, 1, 15);
    public static readonly WorldVoxelCoord RightRailSample = new(80, 1, 16);
    public static readonly WorldVoxelCoord FirstSegmentDamageCell = new(110, 1, 15);
    public static readonly WorldVoxelCoord HighSpeedGapCell = new(84, 1, 15);
    public static readonly WorldVoxelCoord CrossSegmentGapCell = new(147, 1, 15);
    public static readonly WorldVoxelCoord SupportLossCell = new(88, 1, 15);
    public static readonly WorldVoxelCoord SharedSeamCell = new(144, 1, 14);
    public static readonly WorldVoxelCoord ChunkSeamCell = new(16, 1, 15);

    public static HorizontalStraightAlignment FirstAlignment { get; } = new(
        LineStartX, CenterY, CenterZ, SeamX, CenterY, CenterZ);

    public static HorizontalStraightAlignment SecondAlignment { get; } = new(
        SeamX, CenterY, CenterZ, LineEndX, CenterY, CenterZ);

    public static RailVolumeBox LeftRail(double startX, double endX) =>
        LeftRail(startX, endX, CenterY, CenterZ);

    public static RailVolumeBox RightRail(double startX, double endX) =>
        RightRail(startX, endX, CenterY, CenterZ);

    public static RailVolumeBox LeftRail(double startX, double endX, double centerY, double centerZ) =>
        RailAlong(startX, endX, centerY, centerZ - Gauge / 2.0);

    public static RailVolumeBox RightRail(double startX, double endX, double centerY, double centerZ) =>
        RailAlong(startX, endX, centerY, centerZ + Gauge / 2.0);

    private static RailVolumeBox RailAlong(double startX, double endX, double centerY, double railCenterZ) =>
        new(
            startX,
            centerY + (RailMinY - CenterY),
            railCenterZ - RailWidth / 2.0,
            endX,
            centerY + (RailMaxY - CenterY),
            railCenterZ + RailWidth / 2.0);

    public static RailVolumeBox Sleeper(int index)
    {
        var centerX = LineStartX + index;
        return new RailVolumeBox(
            centerX - SleeperLength / 2.0,
            SleeperMinY,
            SleeperCenterZ - SleeperWidth / 2.0,
            centerX + SleeperLength / 2.0,
            SleeperMaxY,
            SleeperCenterZ + SleeperWidth / 2.0);
    }

    public static IReadOnlyList<RailVolumeBox> SleepersFor(HorizontalStraightAlignment alignment, bool includeSharedSeam)
    {
        var sleepers = new List<RailVolumeBox>();
        for (var index = FirstSleeperIndex; index <= LastSleeperIndex; index++)
        {
            var centerX = LineStartX + index;
            var onInterior = centerX > alignment.StartX && centerX < alignment.EndX;
            var onExclusiveStart = centerX == alignment.StartX && alignment.StartX != SeamX;
            var onExclusiveEnd = centerX == alignment.EndX && alignment.EndX != SeamX;
            var shared = includeSharedSeam && centerX == SeamX;
            if (onInterior || onExclusiveStart || onExclusiveEnd || shared)
                sleepers.Add(Sleeper(index));
        }

        return sleepers;
    }

    public static RailConstructionPlan CreateFixturePlan()
    {
        var firstSleepers = SleepersFor(FirstAlignment, includeSharedSeam: true);
        var secondSleepers = SleepersFor(SecondAlignment, includeSharedSeam: true);
        var first = new PlannedRailSegment(
            FirstAlignment,
            new[] { LeftRail(LineStartX, SeamX), RightRail(LineStartX, SeamX) },
            firstSleepers);
        var second = new PlannedRailSegment(
            SecondAlignment,
            new[] { LeftRail(SeamX, LineEndX), RightRail(SeamX, LineEndX) },
            secondSleepers);
        return new RailConstructionPlan(
            new[] { first, second },
            new[] { new PlannedRailConnection(0, RailEnd.End, 1, RailEnd.Start) });
    }

    public static RailConstructionPlan CreateUnconnectedFixturePlan()
    {
        var firstSleepers = SleepersFor(FirstAlignment, includeSharedSeam: false);
        var secondSleepers = SleepersFor(SecondAlignment, includeSharedSeam: false);
        return new RailConstructionPlan(
            new[]
            {
                new PlannedRailSegment(
                    FirstAlignment,
                    new[] { LeftRail(LineStartX, SeamX), RightRail(LineStartX, SeamX) },
                    firstSleepers),
                new PlannedRailSegment(
                    SecondAlignment,
                    new[] { LeftRail(SeamX, LineEndX), RightRail(SeamX, LineEndX) },
                    secondSleepers)
            },
            Array.Empty<PlannedRailConnection>());
    }

    public static RailConstructionPlan CreateIndependentOverlapPlan()
    {
        var alignment = new HorizontalStraightAlignment(64, CenterY, CenterZ, 96, CenterY, CenterZ);
        return new RailConstructionPlan(
            new[]
            {
                new PlannedRailSegment(
                    alignment,
                    new[] { LeftRail(64, 96), RightRail(64, 96) },
                    new[] { Sleeper(64) })
            },
            Array.Empty<PlannedRailConnection>());
    }

    public static RailVolumeBox SweptClearanceFor(HorizontalStraightAlignment alignment)
    {
        var dimensions = VehicleSize.Small;
        var bodyBottom = alignment.StartY + ContactClearance;
        return new RailVolumeBox(
            alignment.StartX - 1.0,
            bodyBottom,
            alignment.StartZ - dimensions.Width / 2.0,
            alignment.EndX + 1.0,
            bodyBottom + dimensions.Height,
            alignment.StartZ + dimensions.Width / 2.0);
    }

    public static RailVolumeBox SmallVehicleSweptClearance() =>
        SweptClearanceFor(new HorizontalStraightAlignment(LineStartX, CenterY, CenterZ, LineEndX, CenterY, CenterZ));
}
