using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed record RailwayVehicleGeometry(RenderedDesign Geometry, ImmutableDictionary<SceneObjectId, string> Keys);

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
        SizeClass.Small => (3, 0.5, 0.25),
        SizeClass.Medium or SizeClass.Large => (7, 1, 0.5),
        SizeClass.ExtraLarge or SizeClass.Titan => (15, 2, 1),
        _ => throw new ArgumentException("Invalid vehicle size.", nameof(size))
    };
    internal static RailwayVehicleGeometry Build(IContentView content, InstanceView instance)
    {
        var geometry = WorldSceneSession.BuildInstance(content, instance, out var keys);
        if (content.FindDesign(instance.Design) is not ContentLookup<DesignEvaluation>.Found found ||
            found.Value is DesignEvaluation.Failed || instance.Details is not InstanceDetails.Vehicle vehicle) return new(geometry, keys);
        var projection = DesignGeometry.Projection(found.Value);
        var destroyed = instance.Parts.Where(part => part.Destroyed).Select(part => part.Part).ToHashSet();
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
        return new(geometry with { Objects = objects.ToImmutable() }, keys);
    }
    internal static RenderedDesign Place(RenderedDesign geometry, ScenePose pose, double distanceCells)
    {
        return geometry with
        {
            Objects = geometry.Objects.Select(item => item with
            {
                Pose = item.Parent is null ? SceneGeometry.Compose(pose, item.Pose) : item.Pose,
                Visuals = item.Visuals.Any(visual => visual is SceneVisual.Wheelset)
                    ? item.Visuals.Select(visual => visual is SceneVisual.Wheelset wheel
                        ? wheel with { RotationRadians = (distanceCells / wheel.RadiusCells % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) }
                        : visual).ToImmutableArray() : item.Visuals
            }).ToImmutableArray(),
            Picking = geometry.Picking.Select(item => item with { Pose = SceneGeometry.Compose(pose, item.Pose) }).ToImmutableArray()
        };
    }
    internal static ScenePose Pose(RailwaySample sample, ScenePoint origin, bool firstEndPositive)
    {
        var forward = firstEndPositive ? sample.Forward : -sample.Forward;
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        var up = Vector3.Cross(forward, right);
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new(
            right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1)));
        return new(RailwayCoordinates.Relative(sample.Position, origin), rotation);
    }
}
