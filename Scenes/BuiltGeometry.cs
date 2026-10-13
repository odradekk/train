using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed record BuiltGeometry(RenderedDesign Geometry, ImmutableDictionary<SceneObjectId, string> Keys)
{
    internal static RenderedDesign Place(RenderedDesign geometry, ScenePose pose) => geometry with
    {
        Objects = geometry.Objects.Select(item => item.Parent is null
            ? item with { Pose = SceneGeometry.Compose(pose, item.Pose) } : item).ToImmutableArray(),
        Picking = geometry.Picking.Select(item => item with { Pose = SceneGeometry.Compose(pose, item.Pose) }).ToImmutableArray()
    };
}

internal sealed record BuiltObject(DesignHash Design, ImmutableHashSet<PartId> Destroyed, BuiltGeometry Geometry);
