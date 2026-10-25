using System.Collections.Immutable;
using System.Numerics;
using Train.Core.Contracts;

namespace Train.Core.World;

internal sealed record ResourceRegenerationState(VoxelDefinitionId Good, Int128 InitialQuantity,
    ulong Sequence);
internal sealed record ResourceRecoveryState(NodeId Node, GoodId Good, double Remaining);

internal static class WorldResources
{
    internal static ImmutableArray<ResourceRegenerationState> Initial(ImmutableArray<ResourceGenerationEntry> entries,
        IEnumerable<NodeMapItem> nodes) => entries.Where(e => !e.Renewable).OrderBy(e => e.Good.Value, StringComparer.Ordinal).Select(e =>
            new ResourceRegenerationState(e.Good, Total(nodes, new GoodId.Voxel(e.Good)), 0)).ToImmutableArray();

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

    internal static (WorldState World, ImmutableArray<CoreEvent> Events) Advance(WorldState before, WorldState committed,
        ulong completedSteps)
    {
        ImmutableArray<NodeMapItem>.Builder? nodes = null;
        ImmutableArray<ResourceRecoveryState>.Builder? recovery = null;
        Dictionary<(NodeId, GoodId), double>? remainders = null;
        List<CoreEvent.ResourceExhausted>? exhausted = null;
        List<CoreEvent.ResourceRecovered>? recovered = null;
        List<CoreEvent.ResourceRegenerated>? regenerated = null;
        for (var index = 0; index < before.Nodes.Length; index++)
        {
            var original = before.Nodes[index];
            if (original.Resources.IsEmpty) continue;
            var node = committed.Nodes[index];
            ImmutableArray<NodeResourceView>.Builder? resources = null;
            foreach (var resource in original.Resources)
            {
                var renewable = resource.Renewal as ResourceRenewal.Renewable;
                if (original.Resources == node.Resources && (renewable is null || resource.Quantity == renewable.Maximum)) continue;
                var current = node.Resources.FirstOrDefault(r => r.Good == resource.Good);
                if (resource.Quantity > 0 && (current is null || current.Quantity == 0))
                    (exhausted ??= []).Add(new CoreEvent.ResourceExhausted(node.Id, resource.Good,
                        resource.Renewal is ResourceRenewal.Renewable));
                if (renewable is null || resource.Quantity == renewable.Maximum) continue;
                recovery ??= ImmutableArray.CreateBuilder<ResourceRecoveryState>();
                var previous = before.ResourceRecovery.IsEmpty ? 0 :
                    (remainders ??= before.ResourceRecovery.ToDictionary(r => (r.Node, r.Good), r => r.Remaining))
                        .GetValueOrDefault((node.Id, resource.Good));
                var missing = renewable.Maximum - resource.Quantity;
                var amount = renewable.UnitsPerSecond * SimulationTime.StepSeconds + previous;
                var units = amount >= missing ? missing : (long)Math.Floor(amount);
                var remainder = amount >= missing ? 0 : amount - units;
                if (remainder > 0) recovery.Add(new(node.Id, resource.Good, remainder));
                if (units == 0) continue;
                var restored = current! with { Quantity = checked(current!.Quantity + units) };
                resources ??= node.Resources.ToBuilder();
                resources[resources.IndexOf(current)] = restored;
                if (current.Quantity == 0)
                    (recovered ??= []).Add(new CoreEvent.ResourceRecovered(node.Id, resource.Good, restored.Quantity));
            }
            if (resources is not null)
                (nodes ??= committed.Nodes.ToBuilder())[index] = node with { Resources = resources.ToImmutable() };
        }

        ImmutableArray<ResourceRegenerationState>.Builder? regenerations = null;
        var nextStep = checked(completedSteps + 1);
        for (var index = 0; index < before.ResourceRegenerations.Length; index++)
        {
            var state = before.ResourceRegenerations[index];
            var entry = before.Source.Configuration.Resources.Single(e => e.Good == state.Good);
            var interval = IntervalSteps(entry.RegenerationIntervalSeconds);
            if (interval is not { } period || nextStep % period != 0) continue;
            var good = new GoodId.Voxel(state.Good);
            if (!BelowThreshold(Total(before.Nodes, good), state.InitialQuantity, entry.GlobalRegenerationRemainingReserveRatio)) continue;
            var candidates = before.Nodes.Where(n => entry.AllowedTerrainTypes.Contains(n.Terrain.TypeId)
                && !n.Resources.Any(r => r.Good == good)).OrderBy(n => n.Id.Value).ToArray();
            if (candidates.Length == 0) continue;
            var chosen = (int)WorldRandom.Integer(Sample(before.Source.WorldSeed, state.Good, state.Sequence, false),
                0, candidates.Length - 1);
            var quantity = WorldRandom.Integer(Sample(before.Source.WorldSeed, state.Good, state.Sequence, true),
                entry.MinimumReserve, entry.MaximumReserve);
            (regenerations ??= before.ResourceRegenerations.ToBuilder())[index] = state with { Sequence = checked(state.Sequence + 1) };
            if (quantity == 0) continue;
            var nodeIndex = before.Nodes.IndexOf(candidates[chosen]);
            nodes ??= committed.Nodes.ToBuilder();
            var node = nodes[nodeIndex];
            nodes[nodeIndex] = node with { Resources = node.Resources.Add(new(good, quantity, new ResourceRenewal.NonRenewable()))
                .OrderBy(r => r.Good, WorldReferences.GoodComparer).ToImmutableArray() };
            (regenerated ??= []).Add(new CoreEvent.ResourceRegenerated(node.Id, good, quantity));
        }
        var recoveryStates = recovery is null ? committed.ResourceRecovery : recovery.OrderBy(r => r.Node.Value)
            .ThenBy(r => r.Good, WorldReferences.GoodComparer).ToImmutableArray();
        if (recovery is not null && recoveryStates.SequenceEqual(committed.ResourceRecovery)) recoveryStates = committed.ResourceRecovery;
        var world = nodes is null && regenerations is null && recoveryStates == committed.ResourceRecovery ? committed : committed with
        {
            Nodes = nodes?.ToImmutable() ?? committed.Nodes,
            ResourceRecovery = recoveryStates,
            ResourceRegenerations = regenerations is null ? committed.ResourceRegenerations :
                regenerations.OrderBy(r => r.Good.Value, StringComparer.Ordinal).ToImmutableArray()
        };
        if (exhausted is null && recovered is null && regenerated is null) return (world, []);
        return (world, (exhausted ?? []).OrderBy(e => e.Node.Value)
            .ThenBy(e => e.Good, WorldReferences.GoodComparer).Cast<CoreEvent>()
            .Concat((recovered ?? []).OrderBy(e => e.Node.Value).ThenBy(e => e.Good, WorldReferences.GoodComparer))
            .Concat((regenerated ?? []).OrderBy(e => e.Good, WorldReferences.GoodComparer)).ToImmutableArray());
    }

    private static Int128 Total(IEnumerable<NodeMapItem> nodes, GoodId good) => nodes.SelectMany(n => n.Resources)
        .Where(r => r.Good == good).Aggregate((Int128)0, (sum, r) => checked(sum + r.Quantity));
}
