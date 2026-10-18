using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed record BuiltGeometry(RenderedDesign Geometry, ImmutableDictionary<SceneObjectId, string> Keys);

internal sealed record BuiltObject(DesignHash Design, ImmutableHashSet<PartId> Destroyed, BuiltGeometry Geometry,
    StructureDamage.Signature Durability);
