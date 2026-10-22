using Train.Core.Contracts;

namespace Train.Cli;

// 每次采样检查的生产、运输与仓储、施工和战斗不变量，全部来自公开查询。
// 返回 (对象, 字段, 说明)；progress 记录各施工任务上次的进度，用来发现倒退。
internal static class WorldInvariants
{
    private const double Tolerance = 1e-9;

    internal static IEnumerable<(string Object, string Field, string Detail)> Check(ICoreReadView view,
        Dictionary<JobId, double> progress)
    {
        var parked = new Dictionary<VehicleId, SettlementId>();
        var settlements = view.World.Nodes.Select(node => node.Occupant).OfType<NodeOccupant.Settlement>()
            .Select(settlement => Found(view.FindSettlement(settlement.Id))).ToArray();
        foreach (var settlement in settlements)
        {
            var name = $"settlement {settlement.Id.Value}";
            foreach (var vehicle in settlement.ParkedVehicles.Concat(settlement.VehiclesForSale))
                parked.TryAdd(vehicle, settlement.Id);
            foreach (var problem in Inventory(view, settlement.Inventory, false)) yield return problem;
            foreach (var lease in settlement.Leases)
                foreach (var problem in Inventory(view, Found(view.FindLease(lease)).Inventory, false)) yield return problem;
            foreach (var facility in settlement.Facilities)
            {
                var key = $"{name} facility {facility.Key.Building.Value}/{facility.Key.Capability}";
                if (!Within(facility.ActualWorkPerSecond, 0, facility.CapabilityWorkPerSecond))
                    yield return (key, "actual_work_per_second", $"{facility.ActualWorkPerSecond:R} 不在 0–{facility.CapabilityWorkPerSecond:R}");
                if (facility.Batch is { } batch && (!Within(batch.Progress, 0, 1) || !Within(batch.ProgressWork, 0, batch.FrozenWorkRequired)))
                    yield return (key, "batch", $"进度 {batch.Progress:R}，工作量 {batch.ProgressWork:R}/{batch.FrozenWorkRequired}");
                if (facility.OutputBuffer.Any(goods => goods.Quantity < 0))
                    yield return (key, "output_buffer", "输出缓冲有负数量");
            }
            foreach (var id in settlement.OwnedJobs.Concat(settlement.CommissionJobs).Distinct())
            {
                if (Found(view.FindJob(id)) is not JobView.Construction { Value: var job }) continue;
                var key = $"job {id.Value}";
                if (!Within(job.ProgressWork, 0, job.FrozenTotalWork) || !Within(job.Progress, 0, 1))
                    yield return (key, "progress", $"进度 {job.Progress:R}，工作量 {job.ProgressWork:R}/{job.FrozenTotalWork}");
                if (progress.TryGetValue(id, out var before) && job.ProgressWork < before)
                    yield return (key, "progress_work", $"从 {before:R} 倒退到 {job.ProgressWork:R}");
                progress[id] = job.ProgressWork;
            }
        }

        var owners = new Dictionary<VehicleId, TrainId>();
        foreach (var item in view.World.Trains)
        {
            var train = Found(view.FindTrain(item.Id));
            var name = $"train {train.Id.Value}";
            if (!Within(train.SpeedMetersPerSecond, 0, train.MaximumSpeedMetersPerSecond))
                yield return (name, "speed_meters_per_second", $"{train.SpeedMetersPerSecond:R} 不在 0–{train.MaximumSpeedMetersPerSecond:R}");
            foreach (var problem in Inventory(view, train.Inventory, true)) yield return problem;
            foreach (var vehicle in train.Vehicles)
            {
                if (!owners.TryAdd(vehicle, train.Id))
                    yield return ($"vehicle {vehicle.Value}", "train", $"同时属于列车 {owners[vehicle].Value} 和 {train.Id.Value}");
                if (parked.TryGetValue(vehicle, out var settlement))
                    yield return ($"vehicle {vehicle.Value}", "parked", $"在列车 {train.Id.Value} 中，又停放在聚居地 {settlement.Value}");
                if (Found(view.FindInstance(new InstanceId.Vehicle(vehicle))).Details is not InstanceDetails.Vehicle { Train: { } at }
                    || at != train.Id)
                    yield return ($"vehicle {vehicle.Value}", "details.train", $"实例记录的列车不是 {train.Id.Value}");
            }
        }
        foreach (var vehicle in owners.Keys.Concat(parked.Keys).Distinct())
        {
            var instance = Found(view.FindInstance(new InstanceId.Vehicle(vehicle)));
            if (!Durable(instance.Structure) || instance.Parts.Any(part => !Durable(part.Durability)))
                yield return ($"vehicle {vehicle.Value}", "durability", "结构或部件耐久不在 0 到上限之间");
        }

        foreach (var item in view.World.Combats)
            foreach (var participant in Found(view.FindCombat(item.Id)).Participants)
                if (participant.Unit switch
                    {
                        CombatUnitId.Instance { Id: var id } => view.FindInstance(id) is not Lookup<InstanceView>.Found,
                        CombatUnitId.CreatureGroup { Id: var id } => view.FindCreatureGroup(id) is not Lookup<CreatureGroupView>.Found,
                        _ => true
                    })
                    yield return ($"combat {item.Id.Value}", "participants", $"参战单位不存在：{participant.Unit}");
        foreach (var item in view.World.CreatureGroups)
            foreach (var creature in Found(view.FindCreatureGroup(item.Id)).Creatures)
                if (creature.Health < 0 || creature.Health > creature.MaximumHealth)
                    yield return ($"creature {creature.Id.Value}", "health", $"{creature.Health}/{creature.MaximumHealth}");
    }

    private static IEnumerable<(string, string, string)> Inventory(ICoreReadView view, InventoryId id, bool bounded)
    {
        var inventory = Found(view.FindInventory(id));
        var name = $"inventory {id.Value}";
        if (inventory.Used < 0 || inventory.Reserved < 0 || bounded && inventory.Used > inventory.Capacity)
            yield return (name, "used", $"已用 {inventory.Used}、预留 {inventory.Reserved}、容量 {inventory.Capacity}");
        if (inventory.Stacks.Any(stack => stack.Quantity <= 0))
            yield return (name, "stacks", "有非正数量的货堆");
    }

    private static bool Within(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum - Tolerance && value <= maximum * (1 + Tolerance) + Tolerance;

    private static bool Durable(DurabilityView durability) =>
        durability.EffectiveCurrent >= 0 && durability.EffectiveCurrent <= durability.Maximum;

    private static T Found<T>(Lookup<T> lookup) where T : notnull => lookup is Lookup<T>.Found found
        ? found.Value : throw new InvalidDataException($"不变量检查的对象不存在：{lookup}。");
}
