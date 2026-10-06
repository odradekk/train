using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Rails;

public static class StaticTrainFixture
{
    public const double Wheelbase = 6;
    public const double Overhang = 1;
    public const double CouplerGap = 1;
    public const double ContactClearance = 0.75;
    public const double LocomotiveFrontContactX = 80;
    public static double CarriageFrontContactX { get; } =
        LocomotiveFrontContactX - Wheelbase - CouplerGap - 2 * Overhang;
    public const double ConstraintTolerance = 1e-8;

    public static CompositeDefinitionId LocomotiveId { get; } = new("core", "dev-locomotive");
    public static CompositeDefinitionId CarriageId { get; } = new("core", "dev-carriage");
    public static VoxelDefinitionId PropulsionId { get; } = new("core", "propulsion");
    public static VoxelDefinitionId ControlId { get; } = new("core", "control");
    public static VoxelDefinitionId TrackContactId { get; } = new("core", "track-contact");
    public static VoxelDefinitionId LocomotiveCoreId { get; } = new("core", "locomotive-core");
    public static VoxelDefinitionId CarriageCoreId { get; } = new("core", "carriage-core");
    public static LocalVoxelCoord LocomotiveRearCouplerCell { get; } = new(7, 1, 1);
    public static LocalVoxelCoord CarriageFrontCouplerCell { get; } = new(0, 1, 1);

    public static CompositeTransform FacingPositiveX(double frontContactX) =>
        CompositeTransform.TryCreate(
            frontContactX + Overhang,
            StraightRailFixture.CenterY + ContactClearance,
            StraightRailFixture.CenterZ + HollowShellFixture.FrontContact.Z,
            -1,
            0,
            0,
            0,
            1,
            0,
            out var transform)
            ? transform
            : throw new InvalidOperationException("Static train facing +X must be a finite rigid transform.");

    public static CompositeTransform LocomotiveTransform { get; } = FacingPositiveX(LocomotiveFrontContactX);
    public static CompositeTransform CarriageTransform { get; } = FacingPositiveX(CarriageFrontContactX);

    public static double AlongTrackDistance(double worldX, HorizontalStraightAlignment alignment) =>
        worldX - alignment.StartX;
}
