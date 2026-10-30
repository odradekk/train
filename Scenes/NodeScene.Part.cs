using System.Runtime.CompilerServices;
using Train.Content.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed partial class NodeScene
{
    internal sealed record Part(string Inputs, BuiltGeometry Geometry, IReadOnlyDictionary<GridColumn, double> Heights,
        ScenePoint Origin = default, SceneBounds? Bounds = null)
    {
        internal long ArrayBytes { get; } = Geometry.Geometry.Objects.Sum(item => (long)IntPtr.Size *
            (1 + item.Visuals.Length + item.Lights.Length + item.Effects.Length) + item.Visuals.OfType<SceneVisual.Geometry>()
                .Select(visual => visual.Mesh).Distinct<SceneMesh>(ReferenceEqualityComparer.Instance)
                .Sum(mesh => (long)mesh.Vertices.Length * Unsafe.SizeOf<SceneVertex>() + (long)mesh.TriangleIndices.Length * sizeof(int))) +
            Geometry.Geometry.Picking.Select(pick => pick.Grid).Distinct().Sum(grid => grid.ArrayBytes) +
            (long)Geometry.Geometry.Picking.Length * IntPtr.Size + ProblemBytes(Geometry.Geometry.Diagnostics);
    }
}
