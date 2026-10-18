using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes;

internal static class StructureDamage
{
    internal const double RemovalFraction = 0.4;
    internal const double SmokeRatio = 0.5;
    internal const double FireRatio = 0.25;

    internal readonly record struct CellRef(string Path, GridPosition At);
    internal readonly record struct PartSignature(int Part, long Effective, long Maximum);
    internal readonly record struct Signature(long StructureEffective, long StructureMaximum, ImmutableArray<PartSignature> Parts)
    {
        internal static Signature From(InstanceView instance) => new(instance.Structure.EffectiveCurrent, instance.Structure.Maximum,
            instance.Parts.OrderBy(part => part.Part.Value).Select(part =>
                new PartSignature(part.Part.Value, part.Durability.EffectiveCurrent, part.Durability.Maximum)).ToImmutableArray());

        internal static bool Same(Signature left, Signature right) =>
            left.StructureEffective == right.StructureEffective && left.StructureMaximum == right.StructureMaximum &&
            left.Parts.SequenceEqual(right.Parts);
    }

    internal readonly record struct PlacedLight(string Path, GridPosition At, SceneLight Light);
    internal readonly record struct PlacedEffect(string Path, PersistentSceneEffect Effect);

    internal sealed record Report(double Ratio, float SurfaceDamage, bool Scorched, ImmutableArray<CellRef> Order,
        ImmutableHashSet<CellRef> Removed, ImmutableDictionary<CellRef, float> Damage,
        ImmutableArray<PlacedLight> Lights, ImmutableArray<PlacedEffect> Effects);

    internal static Report Assess(InstanceView instance, DesignSceneProjection projection, IContentView? content = null)
    {
        var ratio = Ratio(instance.Structure);
        var functional = projection.Elements.Where(element => element.FunctionalPart).Select(element => element.Number).ToHashSet();
        var destroyed = instance.Parts.Where(part => part.Destroyed).Select(part => part.Part).ToHashSet();
        var structure = projection.Voxels.Where(voxel => !voxel.Missing && !functional.Contains(voxel.Number) && !destroyed.Contains(voxel.Number))
            .OrderBy(voxel => voxel.At.X).ThenBy(voxel => voxel.At.Y).ThenBy(voxel => voxel.At.Z).ToArray();
        var order = Order(instance.Id, instance.Design, structure);
        var cap = (int)Math.Floor(order.Length * RemovalFraction);
        var removedCount = (int)Math.Floor(order.Length * RemovalFraction * (1 - ratio));
        var removed = order.Take(removedCount).Select(Cell).ToImmutableHashSet();
        var next = ratio < 1 && removedCount < cap && removedCount < order.Length ? order[removedCount] : (SceneVoxelProjection?)null;
        var damage = ImmutableDictionary.CreateBuilder<CellRef, float>();
        if (next is { } marked) damage[Cell(marked)] = 1;
        var removedAt = removed.Select(cell => cell.At).ToHashSet();
        var structureByAt = structure.ToDictionary(voxel => voxel.At);
        foreach (var cell in removed)
            foreach (var neighbor in Neighbors(cell.At))
                if (!removedAt.Contains(neighbor) && structureByAt.TryGetValue(neighbor, out var voxel))
                    damage[Cell(voxel)] = 1;
        foreach (var part in instance.Parts.Where(part => !part.Destroyed && functional.Contains(part.Part)))
        {
            var loss = (float)(1 - Ratio(part.Durability));
            if (loss <= 0) continue;
            foreach (var voxel in projection.Voxels.Where(voxel => voxel.Number == part.Part && !voxel.Missing))
                damage[Cell(voxel)] = loss;
        }
        var lights = ImmutableArray.CreateBuilder<PlacedLight>();
        var effects = ImmutableArray.CreateBuilder<PlacedEffect>();
        var partRatios = instance.Parts.ToDictionary(part => part.Part);
        foreach (var voxel in projection.Voxels.Where(voxel => !voxel.Missing && !destroyed.Contains(voxel.Number) && !removed.Contains(Cell(voxel))))
        {
            if (content?.FindVoxelAppearance(voxel.Voxel) is not ContentLookup<VoxelAppearanceProjection>.Found { Value.Behaviors.Light: { } light })
                continue;
            var partRatio = functional.Contains(voxel.Number) && partRatios.TryGetValue(voxel.Number, out var part) ? Ratio(part.Durability) : ratio;
            var lightDamage = functional.Contains(voxel.Number) ? (float)(1 - partRatio) : (float)Math.Max(0, (FireRatio - ratio) / FireRatio);
            lights.Add(new(DesignGeometry.Key(voxel.ObjectPath), voxel.LocalAt,
                new(light.Color, light.RangeCells, lightDamage, Seed(instance.Id, voxel.Number.Value, voxel.LocalAt, 1))));
        }
        if (ratio < SmokeRatio)
            effects.Add(new("", new(PersistentEffectKind.Smoke, Center(structure, removed),
                (float)((SmokeRatio - ratio) / SmokeRatio), Seed(instance.Id, 0, default, 2))));
        if (ratio < FireRatio)
            effects.Add(new("", new(PersistentEffectKind.Fire, Center(structure, removed),
                (float)((FireRatio - ratio) / FireRatio), Seed(instance.Id, 0, default, 3))));
        foreach (var part in instance.Parts.Where(part => !part.Destroyed && functional.Contains(part.Part)))
        {
            var loss = 1 - Ratio(part.Durability);
            if (loss <= 0) continue;
            var cell = projection.Voxels.Where(voxel => voxel.Number == part.Part && !voxel.Missing)
                .OrderBy(voxel => voxel.At.X).ThenBy(voxel => voxel.At.Y).ThenBy(voxel => voxel.At.Z).FirstOrDefault();
            if (cell is null) continue;
            effects.Add(new(DesignGeometry.Key(cell.ObjectPath), new(PersistentEffectKind.Sparks,
                new(cell.LocalAt.X + .5f, cell.LocalAt.Y + .5f, cell.LocalAt.Z + .5f),
                (float)loss, Seed(instance.Id, part.Part.Value, cell.LocalAt, 4))));
        }
        return new(ratio, (float)(1 - ratio), removedCount > 0, order.Select(Cell).ToImmutableArray(), removed,
            damage.ToImmutable(), lights.DrainToImmutable(), effects.DrainToImmutable());
    }

    internal static bool Removed(Report report, SceneVoxelProjection voxel) => report.Removed.Contains(Cell(voxel));

    private static CellRef Cell(SceneVoxelProjection voxel) => new(DesignGeometry.Key(voxel.ObjectPath), voxel.LocalAt);

    private static double Ratio(DurabilityView durability) =>
        durability.Maximum <= 0 ? 1 : Math.Clamp((double)durability.EffectiveCurrent / durability.Maximum, 0, 1);

    private static Vector3 Center(SceneVoxelProjection[] structure, ImmutableHashSet<CellRef> removed)
    {
        var remaining = structure.Where(voxel => !removed.Contains(Cell(voxel))).ToArray();
        if (remaining.Length == 0) return Vector3.Zero;
        var sum = Vector3.Zero;
        foreach (var voxel in remaining) sum += new Vector3(voxel.At.X + .5f, voxel.At.Y + .5f, voxel.At.Z + .5f);
        return sum / remaining.Length;
    }

    private static IEnumerable<GridPosition> Neighbors(GridPosition at)
    {
        yield return at with { X = at.X - 1 };
        yield return at with { X = at.X + 1 };
        yield return at with { Y = at.Y - 1 };
        yield return at with { Y = at.Y + 1 };
        yield return at with { Z = at.Z - 1 };
        yield return at with { Z = at.Z + 1 };
    }

    private static SceneVoxelProjection[] Order(InstanceId instance, DesignHash design, SceneVoxelProjection[] cells)
    {
        if (cells.Length == 0) return cells;
        var min = new GridPosition(cells.Min(cell => cell.At.X), cells.Min(cell => cell.At.Y), cells.Min(cell => cell.At.Z));
        var max = new GridPosition(cells.Max(cell => cell.At.X), cells.Max(cell => cell.At.Y), cells.Max(cell => cell.At.Z));
        var center = new Vector3((min.X + max.X) / 2f, (min.Y + max.Y) / 2f, (min.Z + max.Z) / 2f);
        var craters = Craters(instance, design, min, max, Math.Min(3, cells.Length));
        int Compare(SceneVoxelProjection left, SceneVoxelProjection right)
        {
            var leftCrater = Nearest(left);
            var rightCrater = Nearest(right);
            var crater = leftCrater.Index.CompareTo(rightCrater.Index);
            if (crater != 0) return crater;
            var distance = leftCrater.Distance.CompareTo(rightCrater.Distance);
            if (distance != 0) return distance;
            var shell = Shell(right).CompareTo(Shell(left));
            if (shell != 0) return shell;
            var x = left.At.X.CompareTo(right.At.X);
            if (x != 0) return x;
            var y = left.At.Y.CompareTo(right.At.Y);
            return y != 0 ? y : left.At.Z.CompareTo(right.At.Z);
        }
        return cells.OrderBy(cell => cell, Comparer<SceneVoxelProjection>.Create(Compare)).ToArray();

        (int Index, double Distance) Nearest(SceneVoxelProjection cell)
        {
            var best = 0;
            var distance = DistanceTo(cell, craters[0]);
            for (var index = 1; index < craters.Length; index++)
            {
                var candidate = DistanceTo(cell, craters[index]);
                if (candidate < distance) { distance = candidate; best = index; }
            }
            return (best, distance);
        }
        double Shell(SceneVoxelProjection cell) => Vector3.Distance(new(cell.At.X + .5f, cell.At.Y + .5f, cell.At.Z + .5f), center);
    }

    private static Vector3[] Craters(InstanceId instance, DesignHash design, GridPosition min, GridPosition max, int count)
    {
        var text = Encoding.UTF8.GetBytes(design.Value);
        var identity = new byte[9 + text.Length];
        var (kind, value) = instance switch
        {
            InstanceId.Vehicle vehicle => ((byte)1, vehicle.Id.Value),
            InstanceId.Building building => ((byte)2, building.Id.Value),
            _ => ((byte)0, 0UL)
        };
        identity[0] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(identity.AsSpan(1), value);
        text.CopyTo(identity.AsSpan(9));
        var hash = SHA256.HashData(identity);
        var craters = new Vector3[count];
        for (var index = 0; index < count; index++)
        {
            var block = SHA256.HashData(hash.Append((byte)index).ToArray());
            craters[index] = new(
                Spread(block, 0, min.X, max.X), Spread(block, 4, min.Y, max.Y), Spread(block, 8, min.Z, max.Z));
        }
        return craters;
    }

    private static float Spread(byte[] hash, int offset, int min, int max) =>
        min + (max - min) * (BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(offset)) / (float)uint.MaxValue);

    private static double DistanceTo(SceneVoxelProjection cell, Vector3 crater) =>
        Vector3.Distance(new(cell.At.X + .5f, cell.At.Y + .5f, cell.At.Z + .5f), crater);

    internal static ulong Seed(InstanceId instance, int part, GridPosition cell, byte tag)
    {
        Span<byte> identity = stackalloc byte[26];
        var (kind, value) = instance switch
        {
            InstanceId.Vehicle vehicle => ((byte)1, vehicle.Id.Value),
            InstanceId.Building building => ((byte)2, building.Id.Value),
            _ => ((byte)0, 0UL)
        };
        identity[0] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(identity[1..], value);
        BinaryPrimitives.WriteInt32LittleEndian(identity[9..], part);
        BinaryPrimitives.WriteInt32LittleEndian(identity[13..], cell.X);
        BinaryPrimitives.WriteInt32LittleEndian(identity[17..], cell.Y);
        BinaryPrimitives.WriteInt32LittleEndian(identity[21..], cell.Z);
        identity[25] = tag;
        return BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(identity));
    }
}
