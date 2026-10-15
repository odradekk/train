using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;

namespace Train.Core.World;

internal sealed record ResourceRegenerationState(VoxelDefinitionId Good, Int128 InitialQuantity,
    ulong ElapsedSteps, ulong Sequence);
internal sealed record ResourceRecoveryState(NodeId Node, GoodId Good, double Remaining);

internal static class WorldResources
{
    internal static ImmutableArray<ResourceRegenerationState> Initial(ImmutableArray<ResourceGenerationEntry> entries,
        IEnumerable<NodeMapItem> nodes) => entries.Where(e => !e.Renewable).OrderBy(e => e.Good.Value, StringComparer.Ordinal).Select(e =>
            new ResourceRegenerationState(e.Good, Total(nodes, new GoodId.Voxel(e.Good)), 0, 0)).ToImmutableArray();

    internal static ulong? IntervalSteps(double seconds)
    {
        var steps = Math.Ceiling(seconds * SimulationTime.StepsPerSecond);
        return steps >= ulong.MaxValue ? null : (ulong)Math.Max(1, steps);
    }

    internal static ulong Sample(ulong seed, VoxelDefinitionId good, ulong sequence, bool reserve)
        => KeyedRandom.Sample(new(seed, reserve ? "resource.regeneration.reserve.v1" : "resource.regeneration.node.v1",
            [.. WorldRandom.Identity(good.Value), sequence]));

    private static bool BelowThreshold(Int128 quantity, Int128 initial, double ratio)
    {
        // 全世界储量可能超过 double 的整数精度，直接比较整数与二进制比例。
        var bits = BitConverter.DoubleToUInt64Bits(ratio);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var significand = (bits & ((1UL << 52) - 1)) | (exponent == 0 ? 0 : 1UL << 52);
        return (BigInteger)quantity << (exponent == 0 ? 1074 : 1075 - exponent) < (BigInteger)initial * significand;
    }

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Advance(WorldState before, WorldState committed)
    {
        var nodes = committed.Nodes.ToBuilder();
        var exhausted = ImmutableArray.CreateBuilder<CoreEvent>();
        var recovered = ImmutableArray.CreateBuilder<CoreEvent>();
        var regenerated = ImmutableArray.CreateBuilder<CoreEvent>();
        var recovery = ImmutableArray.CreateBuilder<ResourceRecoveryState>();
        var remainders = before.ResourceRecovery.ToDictionary(r => (r.Node, r.Good), r => r.Remaining);
        foreach (var index in Enumerable.Range(0, before.Nodes.Length).OrderBy(i => before.Nodes[i].Id.Value))
        {
            var original = before.Nodes[index];
            if (original.Resources.IsEmpty) continue;
            var node = nodes[index];
            var resources = node.Resources.ToBuilder();
            foreach (var resource in original.Resources.OrderBy(r => WorldReferences.GoodKey(r.Good), StringComparer.Ordinal))
            {
                var current = resources.FirstOrDefault(r => r.Good == resource.Good);
                if (resource.Quantity > 0 && (current is null || current.Quantity == 0))
                    exhausted.Add(new CoreEvent.ResourceExhausted(node.Id, resource.Good,
                        resource.Renewal is ResourceRenewal.Renewable));
                if (resource.Renewal is not ResourceRenewal.Renewable renewable) continue;
                var missing = renewable.Maximum - resource.Quantity;
                var amount = renewable.UnitsPerSecond * SimulationTime.StepSeconds
                    + remainders.GetValueOrDefault((node.Id, resource.Good));
                var units = amount >= missing ? missing : (long)Math.Floor(amount);
                var remainder = amount >= missing ? 0 : amount - units;
                if (remainder > 0) recovery.Add(new(node.Id, resource.Good, remainder));
                if (units == 0) continue;
                var restored = current! with { Quantity = checked(current!.Quantity + units) };
                resources[resources.IndexOf(current)] = restored;
                if (current.Quantity == 0)
                    recovered.Add(new CoreEvent.ResourceRecovered(node.Id, resource.Good, restored.Quantity));
            }
            if (!resources.SequenceEqual(node.Resources)) nodes[index] = node with { Resources = resources.ToImmutable() };
        }

        var regenerations = before.ResourceRegenerations.OrderBy(r => r.Good.Value, StringComparer.Ordinal).ToImmutableArray().ToBuilder();
        for (var index = 0; index < regenerations.Count; index++)
        {
            var state = regenerations[index];
            var entry = before.Source.Configuration.Resources.Single(e => e.Good == state.Good);
            var elapsed = checked(state.ElapsedSteps + 1);
            var interval = IntervalSteps(entry.RegenerationIntervalSeconds);
            regenerations[index] = state with { ElapsedSteps = elapsed };
            if (interval is not { } period || elapsed != period) continue;
            regenerations[index] = state with { ElapsedSteps = 0 };
            var good = new GoodId.Voxel(state.Good);
            if (!BelowThreshold(Total(before.Nodes, good), state.InitialQuantity, entry.GlobalRegenerationRemainingReserveRatio)) continue;
            var candidates = before.Nodes.Where(n => entry.AllowedTerrainTypes.Contains(n.Terrain.TypeId)
                && !n.Resources.Any(r => r.Good == good)).OrderBy(n => n.Id.Value).ToArray();
            if (candidates.Length == 0) continue;
            var chosen = (int)WorldRandom.Integer(Sample(before.Source.WorldSeed, state.Good, state.Sequence, false),
                0, candidates.Length - 1);
            var quantity = WorldRandom.Integer(Sample(before.Source.WorldSeed, state.Good, state.Sequence, true),
                entry.MinimumReserve, entry.MaximumReserve);
            regenerations[index] = regenerations[index] with { Sequence = checked(state.Sequence + 1) };
            if (quantity == 0) continue;
            var nodeIndex = before.Nodes.IndexOf(candidates[chosen]);
            var node = nodes[nodeIndex];
            nodes[nodeIndex] = node with { Resources = node.Resources.Add(new(good, quantity, new ResourceRenewal.NonRenewable()))
                .OrderBy(r => WorldReferences.GoodKey(r.Good), StringComparer.Ordinal).ToImmutableArray() };
            regenerated.Add(new CoreEvent.ResourceRegenerated(node.Id, good, quantity));
        }
        return (committed with { Nodes = nodes.ToImmutable(), ResourceRecovery = recovery.ToImmutable(),
            ResourceRegenerations = regenerations.ToImmutable() }, exhausted.Concat(recovered).Concat(regenerated).ToImmutableArray());
    }

    private static Int128 Total(IEnumerable<NodeMapItem> nodes, GoodId good) => nodes.SelectMany(n => n.Resources)
        .Where(r => r.Good == good).Aggregate((Int128)0, (sum, r) => checked(sum + r.Quantity));
}
