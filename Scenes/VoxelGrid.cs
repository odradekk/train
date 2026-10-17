using System.Collections.Immutable;
using System.Numerics;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal sealed class CellProperties(VoxelOrientation orientation, VoxelAppearanceProjection? appearance,
    VoxelShape? shape, PaintColor? paint, PickSubject? subject, ImmutableArray<int> path,
    ContentReference? missingReference = null) : IEquatable<CellProperties>
{
    internal VoxelOrientation Orientation { get; } = orientation;
    internal VoxelAppearanceProjection? Appearance { get; } = appearance;
    internal VoxelShape? Shape { get; } = shape;
    internal PaintColor? Paint { get; } = paint;
    internal PickSubject? Subject { get; } = subject;
    internal ImmutableArray<int> Path { get; } = path;
    internal ContentReference? MissingReference { get; } = missingReference;
    internal string ShapeName { get; } = VoxelShapes.Name(shape, appearance);
    internal bool Occludes { get; } = VoxelShapes.Name(shape, appearance) == "cube" && appearance?.Presentation is VoxelPresentation.Cube;
    internal int CoveredFaces { get; } = appearance?.Presentation is VoxelPresentation.Cube
        ? VoxelShapes.Coverage(VoxelShapes.Name(shape, appearance), orientation) : 0;
    private readonly int hashCode = Hash(orientation, appearance, shape, paint, subject, path, missingReference);

    public bool Equals(CellProperties? other) => ReferenceEquals(this, other) || other is not null &&
        Orientation == other.Orientation && Appearance == other.Appearance && Shape == other.Shape && Paint == other.Paint &&
        Subject == other.Subject && MissingReference == other.MissingReference && Path.AsSpan().SequenceEqual(other.Path.AsSpan());
    public override bool Equals(object? other) => other is CellProperties properties && Equals(properties);
    public override int GetHashCode() => hashCode;

    private static int Hash(VoxelOrientation orientation, VoxelAppearanceProjection? appearance, VoxelShape? shape,
        PaintColor? paint, PickSubject? subject, ImmutableArray<int> path, ContentReference? missingReference)
    {
        var hash = new HashCode();
        hash.Add(orientation); hash.Add(appearance?.Definition); hash.Add(shape); hash.Add(paint); hash.Add(subject); hash.Add(missingReference);
        foreach (var index in path.AsSpan()) hash.Add(index);
        return hash.ToHashCode();
    }
}

internal readonly record struct SceneCell(GridPosition At, CellProperties Properties)
{
    internal VoxelOrientation Orientation => Properties.Orientation;
    internal VoxelAppearanceProjection? Appearance => Properties.Appearance;
    internal VoxelShape? Shape => Properties.Shape;
    internal PaintColor? Paint => Properties.Paint;
    internal PickSubject? Subject => Properties.Subject;
    internal ImmutableArray<int> Path => Properties.Path;
    internal ContentReference? MissingReference => Properties.MissingReference;
    internal bool Occludes => Properties.Occludes;
}

internal sealed class VoxelGrid
{
    internal const int ChunkSize = 32;
    private readonly Dictionary<GridPosition, int[]> chunks = [];
    private readonly ImmutableArray<CellProperties?> entries;
    internal IEnumerable<SceneCell> Cells
    {
        get
        {
            foreach (var (key, values) in chunks)
            for (var index = 0; index < values.Length; index++)
                if (values[index] != 0)
                    yield return new(new((key.X << 5) + (index & 31),
                        (key.Y << 5) + ((index >> 5) & 31), (key.Z << 5) + (index >> 10)), entries[values[index]]!);
        }
    }
    internal SceneBounds? Bounds { get; }
    internal IEnumerable<GridPosition> Chunks => chunks.Keys;
    internal long ArrayBytes => chunks.Values.Sum(values => (long)values.Length * sizeof(int)) +
        (long)entries.Length * IntPtr.Size + entries.Sum(entry => (long)(entry?.Path.Length ?? 0) * sizeof(int)) +
        entries.Where(entry => entry?.Appearance is not null).Select(entry => entry!.Appearance!).Distinct()
            .Sum(appearance => appearance.Presentation switch
            {
                VoxelPresentation.Cube cube => (long)cube.Shapes.Length * IntPtr.Size,
                VoxelPresentation.Mesh mesh => (long)mesh.SurfaceMaterials.Length * sizeof(MaterialCategory),
                _ => 0
            });

    internal VoxelGrid(ImmutableArray<SceneCell> cells)
    {
        if (cells.IsDefault) throw new ArgumentException("Cells must be initialized.", nameof(cells));
        var properties = ImmutableArray.CreateBuilder<CellProperties?>();
        properties.Add(null);
        var ids = new Dictionary<CellProperties, int>();
        var minimum = new GridPosition(int.MaxValue, int.MaxValue, int.MaxValue);
        var maximum = new GridPosition(int.MinValue, int.MinValue, int.MinValue);
        var previousChunk = default(GridPosition);
        int[]? values = null;
        CellProperties? previousProperties = null;
        var previousId = 0;
        foreach (var cell in cells)
        {
            var key = Chunk(cell.At);
            if (values is null || key != previousChunk)
            {
                if (!chunks.TryGetValue(key, out values))
                    chunks.Add(key, values = new int[ChunkSize * ChunkSize * ChunkSize]);
                previousChunk = key;
            }
            var index = Index(cell.At);
            if (values[index] != 0) throw new ArgumentException("Cells overlap.", nameof(cells));
            var id = previousId;
            if (id == 0 || !ReferenceEquals(previousProperties, cell.Properties))
            {
                if (!ids.TryGetValue(cell.Properties, out id))
                {
                    id = properties.Count;
                    ids.Add(cell.Properties, id);
                    properties.Add(cell.Properties);
                }
                previousProperties = cell.Properties;
                previousId = id;
            }
            values[index] = id;
            minimum = new(Math.Min(minimum.X, cell.At.X), Math.Min(minimum.Y, cell.At.Y), Math.Min(minimum.Z, cell.At.Z));
            maximum = new(Math.Max(maximum.X, cell.At.X), Math.Max(maximum.Y, cell.At.Y), Math.Max(maximum.Z, cell.At.Z));
        }
        entries = properties.ToImmutable();
        if (!cells.IsEmpty)
            Bounds = new(new(minimum.X, minimum.Y, minimum.Z), new(maximum.X + 1f, maximum.Y + 1f, maximum.Z + 1f));
    }

    internal static GridPosition Chunk(GridPosition cell) => new(cell.X >> 5, cell.Y >> 5, cell.Z >> 5);
    private static int Index(GridPosition cell) => (cell.X & 31) + ChunkSize * ((cell.Y & 31) + ChunkSize * (cell.Z & 31));
    private int EntryAt(GridPosition cell) => chunks.TryGetValue(Chunk(cell), out var values) ? values[Index(cell)] : 0;
    internal SceneCell? Find(GridPosition cell)
    {
        var id = EntryAt(cell);
        return id == 0 ? null : new(cell, entries[id]!);
    }
    internal bool Contains(GridPosition cell) => EntryAt(cell) != 0;
    internal bool Occludes(GridPosition cell, GridFace face) =>
        ((entries[EntryAt(cell)]?.CoveredFaces ?? 0) & (1 << (int)face)) != 0;

    internal (SceneCell Cell, GridFace Face, double Distance)? Pick(SceneRay ray)
    {
        if (Bounds is null || !SceneGeometry.Intersect(ray, Bounds, out var distance, out var face, out var exit)) return null;
        var direction = ray.Direction;
        var x = StartCell(ray.Origin.X + direction.X * distance, direction.X);
        var y = StartCell(ray.Origin.Y + direction.Y * distance, direction.Y);
        var z = StartCell(ray.Origin.Z + direction.Z * distance, direction.Z);
        var nextX = NextBoundary(ray.Origin.X, direction.X, x);
        var nextY = NextBoundary(ray.Origin.Y, direction.Y, y);
        var nextZ = NextBoundary(ray.Origin.Z, direction.Z, z);
        while (distance <= exit && distance <= ray.MaximumDistanceCells)
        {
            var cell = Find(new(x, y, z));
            if (cell is { } occupied) return (occupied, face, distance);
            var next = Math.Min(nextX, Math.Min(nextY, nextZ));
            if (!double.IsFinite(next)) return null;
            // Simultaneous crossings skip cells touched only at an edge or corner.
            if (nextZ == next)
            {
                z += Math.Sign(direction.Z);
                nextZ += Math.Abs(1.0 / direction.Z);
                face = direction.Z > 0 ? GridFace.NegativeZ : GridFace.PositiveZ;
            }
            if (nextY == next)
            {
                y += Math.Sign(direction.Y);
                nextY += Math.Abs(1.0 / direction.Y);
                face = direction.Y > 0 ? GridFace.NegativeY : GridFace.PositiveY;
            }
            if (nextX == next)
            {
                x += Math.Sign(direction.X);
                nextX += Math.Abs(1.0 / direction.X);
                face = direction.X > 0 ? GridFace.NegativeX : GridFace.PositiveX;
            }
            distance = next;
        }
        return null;
    }

    private static int StartCell(double point, float direction)
    {
        var floor = Math.Floor(point);
        return checked((int)(direction < 0 && point == floor ? floor - 1 : floor));
    }
    private static double NextBoundary(float origin, float direction, int cell) => direction == 0
        ? double.PositiveInfinity : ((direction > 0 ? cell + 1.0 : cell) - origin) / direction;
}

internal static class SceneGeometry
{
    internal static RenderedDesign Place(RenderedDesign geometry, ScenePose pose) => geometry with
    {
        Objects = geometry.Objects.Select(item => item.Parent is null
            ? item with { Pose = Compose(pose, item.Pose) } : item).ToImmutableArray(),
        Picking = geometry.Picking.Select(item => item with { Pose = Compose(pose, item.Pose) }).ToImmutableArray()
    };

    internal static Vector3 Normal(GridFace face) => face switch
    {
        GridFace.NegativeX => -Vector3.UnitX, GridFace.PositiveX => Vector3.UnitX,
        GridFace.NegativeY => -Vector3.UnitY, GridFace.PositiveY => Vector3.UnitY,
        GridFace.NegativeZ => -Vector3.UnitZ, GridFace.PositiveZ => Vector3.UnitZ,
        _ => throw new ArgumentException("Invalid face.", nameof(face))
    };

    internal static void Validate(SceneRay ray)
    {
        ArgumentNullException.ThrowIfNull(ray);
        if (!Finite(ray.Origin) || !Finite(ray.Direction) || Math.Abs(ray.Direction.LengthSquared() - 1) > 0.00001f ||
            !double.IsFinite(ray.MaximumDistanceCells) || ray.MaximumDistanceCells <= 0)
            throw new ArgumentException("A ray must be finite, normalized and have positive length.", nameof(ray));
    }
    internal static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    internal static void Validate(ScenePose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if (!Finite(pose.Position) || !float.IsFinite(pose.Rotation.LengthSquared()) ||
            Math.Abs(pose.Rotation.LengthSquared() - 1) > 0.00001f)
            throw new ArgumentException("A pose must be finite and have a unit rotation.", nameof(pose));
    }

    internal static ScenePose Compose(ScenePose parent, ScenePose child) => new(
        parent.Position + Vector3.Transform(child.Position, parent.Rotation),
        Quaternion.Normalize(parent.Rotation * child.Rotation));

    internal static ScenePose Placement(GridPosition at, VoxelOrientation orientation)
    {
        var (right, up, forward) = Axes(orientation);
        var rotation = Quaternion.CreateFromRotationMatrix(new(
            right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0, 0, 0, 0, 1));
        var center = new Vector3(0.5f);
        return new(new Vector3(at.X, at.Y, at.Z) + center - Vector3.Transform(center, rotation), rotation);
    }
    internal static (Vector3 Right, Vector3 Up, Vector3 Forward) Axes(VoxelOrientation orientation)
    {
        if (!Enum.IsDefined(orientation)) throw new ArgumentException("Invalid orientation.", nameof(orientation));
        var name = orientation.ToString();
        var right = Axis(name[..2]);
        var up = Axis(name[2..]);
        return (right, up, Vector3.Cross(right, up));
    }
    private static Vector3 Axis(string axis) => axis switch
    {
        "Px" => Vector3.UnitX, "Nx" => -Vector3.UnitX,
        "Py" => Vector3.UnitY, "Ny" => -Vector3.UnitY,
        "Pz" => Vector3.UnitZ, "Nz" => -Vector3.UnitZ,
        _ => throw new ArgumentException("Invalid axis.", nameof(axis))
    };

    internal static bool Intersect(SceneRay ray, SceneBounds bounds, out double entry, out GridFace face, out double exit)
    {
        entry = 0;
        exit = ray.MaximumDistanceCells;
        face = GridFace.NegativeX;
        for (var axis = 0; axis < 3; axis++)
        {
            var origin = axis == 0 ? ray.Origin.X : axis == 1 ? ray.Origin.Y : ray.Origin.Z;
            var direction = axis == 0 ? ray.Direction.X : axis == 1 ? ray.Direction.Y : ray.Direction.Z;
            var minimum = axis == 0 ? bounds.Minimum.X : axis == 1 ? bounds.Minimum.Y : bounds.Minimum.Z;
            var maximum = axis == 0 ? bounds.Maximum.X : axis == 1 ? bounds.Maximum.Y : bounds.Maximum.Z;
            if (direction == 0)
            {
                if (origin < minimum || origin >= maximum) return false;
                continue;
            }
            var near = (minimum - (double)origin) / direction;
            var far = (maximum - (double)origin) / direction;
            if (near > far) (near, far) = (far, near);
            if (near >= entry)
            {
                entry = near;
                face = (GridFace)(axis * 2 + (direction < 0 ? 1 : 0));
            }
            exit = Math.Min(exit, far);
            if (entry > exit) return false;
        }
        return exit >= 0;
    }
}
