using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class RailwayVehicles
{
    internal static int? Length(DesignEvaluation? evaluation) => evaluation switch
    {
        DesignEvaluation.Complete { Core.Design.Kind: DesignKind.Vehicle vehicle } => vehicle.LengthCells,
        DesignEvaluation.Partial { Metrics.VehicleLengthCells: ComputedValue<int>.Known length } => length.Value,
        _ => null
    };
    // Provisional wheel sizes are calibrated with the vehicle content slice.
    internal static (double Gauge, double Radius, double Width) Wheels(SizeClass size) => size switch
    {
        SizeClass.Small => (RailwayCoordinates.GaugeFor(size), 0.5, 0.25),
        SizeClass.Medium or SizeClass.Large => (RailwayCoordinates.GaugeFor(size), 1, 0.5),
        SizeClass.ExtraLarge or SizeClass.Titan => (RailwayCoordinates.GaugeFor(size), 2, 1),
        _ => throw new ArgumentException("Invalid vehicle size.", nameof(size))
    };
    internal static BuiltObject Build(IContentView content, InstanceView instance)
    {
        var built = WorldSceneSession.BuildObject(content, instance, new(Vector3.Zero, Quaternion.Identity));
        var geometry = built.Geometry.Geometry;
        var keys = built.Geometry.Keys;
        if (content.FindDesign(instance.Design) is not ContentLookup<DesignEvaluation>.Found found ||
            found.Value is DesignEvaluation.Failed || instance.Details is not InstanceDetails.Vehicle vehicle) return built;
        var projection = DesignGeometry.Projection(found.Value);
        var destroyed = built.Destroyed;
        var removedPaths = projection.NestedObjects.Where(item => item.Details is NestedObjectDetails.FunctionalComposite composite &&
            destroyed.Contains(composite.Number)).Select(item => item.NestedItemIndices).ToArray();
        var rows = projection.Voxels.Where(voxel => !voxel.Missing && !destroyed.Contains(voxel.Number) &&
            !removedPaths.Any(path => voxel.ObjectPath.Length >= path.Length && voxel.ObjectPath.Take(path.Length).SequenceEqual(path)) &&
            content.FindVoxelAppearance(voxel.Voxel) is ContentLookup<VoxelAppearanceProjection>.Found
            { Value.Behaviors.Wheelset: true }).Select(voxel => voxel.At.Z).Distinct().Order().ToArray();
        var (gauge, radius, width) = Wheels(vehicle.Size);
        var objects = geometry.Objects.ToBuilder();
        var next = geometry.Objects.Max(item => item.Id.Value);
        var parent = geometry.Objects.First(item => item.Parent is null).Id;
        foreach (var row in rows)
        {
            var id = new SceneObjectId(++next);
            keys = keys.Add(id, "wheelset:" + row);
            objects.Add(new(id, parent, new(new(0, (float)radius, row + 0.5f), Quaternion.Identity), null,
                [new SceneVisual.Wheelset(gauge, radius, width, 0)], [], []));
        }
        return built with { Geometry = new(geometry with { Objects = objects.ToImmutable() }, keys) };
    }
    internal static RenderedDesign Place(RenderedDesign geometry, ScenePose pose, double distanceCells)
    {
        var placed = BuiltGeometry.Place(geometry, pose);
        return placed with
        {
            Objects = placed.Objects.Select(item => item with
            {
                Visuals = item.Visuals.Any(visual => visual is SceneVisual.Wheelset)
                    ? item.Visuals.Select(visual => visual is SceneVisual.Wheelset wheel
                        ? wheel with { RotationRadians = (distanceCells / wheel.RadiusCells % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) }
                        : visual).ToImmutableArray() : item.Visuals
            }).ToImmutableArray()
        };
    }
    internal static ScenePose Pose(RailwaySample sample, ScenePoint origin, bool firstEndPositive)
    {
        var forward = firstEndPositive ? sample.Forward : -sample.Forward;
        return new(RailwayCoordinates.Relative(sample.Position, origin), RailwayCoordinates.Pose(forward));
    }
}
