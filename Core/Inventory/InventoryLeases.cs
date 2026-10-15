using System.Collections.Immutable;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.World;

namespace Train.Core.Inventory;

internal static class InventoryLeases
{
    internal static void Validate(LeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request is LeaseRequest.Rent r && (r.Settlement.Value == 0 || r.Capacity <= 0 || r.DurationSteps == 0)
            || request is LeaseRequest.Renew n && (n.Lease.Value == 0 || n.DurationSteps == 0))
            throw new ArgumentException("租赁须提供非零身份、正容量和正租期。", nameof(request));
    }

    internal static LeaseView Read(LeaseState lease, ulong step)
        => new(lease.Id, lease.Settlement, lease.Inventory, lease.Capacity, lease.ExpiresAtStep, lease.Paid, step >= lease.ExpiresAtStep);

    internal static LeaseQuote Quote(WorldState world, CoreContentProjection content, ulong step,
        CoreRevision revision, LeaseRequest request, Money? maximumPayment = null)
    {
        var lease = request is LeaseRequest.Renew renew ? world.Leases.FirstOrDefault(l => l.Id == renew.Lease) : null;
        var settlementId = request is LeaseRequest.Rent rent ? rent.Settlement : lease?.Settlement;
        var settlement = world.Settlements.FirstOrDefault(s => s.Id == settlementId);
        var capacity = request is LeaseRequest.Rent r ? r.Capacity : lease?.Capacity;
        var duration = request is LeaseRequest.Rent rental ? rental.DurationSteps : ((LeaseRequest.Renew)request).DurationSteps;
        var ruleId = world.Source.Configuration.Rules.Market;
        var rule = content.MissingReferences.Any(m => m.Reference == new CoreDefinitionReference.Rule(ruleId))
            ? null : content.MarketRules.SingleOrDefault(r => r.Id == ruleId);
        long rentable = 0;
        var occupancyFits = true;
        if (settlement is not null && rule is not null)
        {
            var (storage, free) = InventoryStorage.Settlement(world, content, settlement, step, lease?.Id);
            rentable = storage.RentableCapacity;
            if (lease is not null)
            {
                var inventory = world.Inventories.Single(i => i.Id == lease.Inventory);
                // 排除旧租约后重新承诺额度；已有货物与预留可能合法超容，不能忽略。
                var occupied = checked(InventoryStorage.Used(inventory) + InventoryStorage.Reserved(inventory));
                occupancyFits = occupied <= free;
            }
        }
        var durationValid = rule is not null && duration >= (ulong)rule.LeaseDurationTicks
            && duration % (ulong)rule.LeaseDurationTicks == 0
            && (Int128)duration + (lease is null ? 0 : Math.Max(step, lease.ExpiresAtStep) - step)
                <= (Int128)rule.LeaseDurationTicks * rule.MaximumLeaseDurationMultiple;
        Money? total = null;
        if (rule is not null && durationValid
            && capacity is { } quantity && quantity <= rentable && occupancyFits)
        {
            var amount = checked((Int128)rule.RentMoneyPerCapacityHour.Value * quantity * duration);
            const int stepsPerHour = SimulationTime.StepsPerSecond * 3600;
            total = new(checked((long)(amount / stepsPerHour + (amount % stepsPerHour == 0 ? 0 : 1))));
        }
        CoreProblem? problem;
        if (request is LeaseRequest.Renew n && lease is null) problem = new("object_missing", "Request.Lease", null, [n.Lease.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        else if (settlement is null) problem = new("object_missing", "Request.Settlement", new WorldObjectId.Settlement(settlementId!.Value), []);
        else if (settlement.Relation == SettlementRelation.Hostile) problem = new("access_denied", "Request.Settlement", new WorldObjectId.Settlement(settlement.Id), []);
        else if (settlement.Relation == SettlementRelation.PlayerOwned) problem = new("invalid_state", "Request.Settlement", new WorldObjectId.Settlement(settlement.Id), []);
        else if (rule is null) problem = new("content_missing", "MarketRule", null, [ruleId.Value]);
        else if (!durationValid) problem = new("invalid_state", "Request.DurationSteps", null, []);
        else if (capacity > rentable || !occupancyFits) problem = new("insufficient_capacity", "Request.Capacity", new WorldObjectId.Settlement(settlement.Id), []);
        else if (maximumPayment is { } maximum && total!.Value.Value > maximum.Value) problem = new("price_condition_failed", "MaximumPayment", null, []);
        else if (total!.Value.Value > world.Money.Value) problem = new("insufficient_money", "Wallet", null, []);
        else if (lease is null && (world.Next.Lease == ulong.MaxValue || world.Next.Inventory == ulong.MaxValue))
            problem = new("identity_exhausted", world.Next.Lease == ulong.MaxValue ? "World.Next.Lease" : "World.Next.Inventory", null, []);
        else problem = null;
        return new(request, revision, total, rentable, problem is null ? [] : [problem]);
    }

    internal static CommandEffect Execute(WorldState world, CoreContentProjection content, CoreCommand.AcquireLease command, ulong step)
    {
        var quote = Quote(world, content, step, new(0), command.Request, command.MaximumPayment);
        if (!quote.Blockers.IsEmpty) return new CommandEffect.Rejected(quote.Blockers);
        var payment = quote.Total!.Value;
        LeaseState lease;
        if (command.Request is LeaseRequest.Rent rent)
        {
            lease = new(new(world.Next.Lease), rent.Settlement, new(world.Next.Inventory), rent.Capacity,
                checked(step + rent.DurationSteps), payment);
            world = world with
            {
                Leases = world.Leases.Add(lease),
                Inventories = world.Inventories.Add(new(lease.Inventory, new InventoryOwner.Lease(lease.Id), [], [])),
                Next = world.Next with { Lease = checked(world.Next.Lease + 1), Inventory = checked(world.Next.Inventory + 1) }
            };
        }
        else
        {
            var renew = (LeaseRequest.Renew)command.Request;
            var old = world.Leases.Single(l => l.Id == renew.Lease);
            lease = old with { ExpiresAtStep = checked(Math.Max(step, old.ExpiresAtStep) + renew.DurationSteps),
                Paid = new(checked(old.Paid.Value + payment.Value)) };
            world = world with { Leases = world.Leases.Select(l => l.Id == lease.Id ? lease : l).ToImmutableArray() };
        }
        world = world with { Money = new(checked(world.Money.Value - payment.Value)) };
        return new CommandEffect.Accepted(content,
            [new CoreEvent.LeaseAcquired(Read(lease, step), payment, command.Request is LeaseRequest.Renew)], world,
            command.Request is LeaseRequest.Rent ? [new WorldObjectId.Inventory(lease.Inventory)] : []);
    }

    internal static ImmutableArray<CoreEvent> ExpirationEvents(WorldState world, ulong step)
        => world.Leases.Where(l => l.ExpiresAtStep == checked(step + 1)).OrderBy(l => l.Id.Value)
            .Select(l => (CoreEvent)new CoreEvent.LeaseExpired(l.Id, l.Settlement, l.Inventory)).ToImmutableArray();
}
