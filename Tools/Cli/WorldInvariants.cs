using Train.Core.Contracts;

namespace Train.Cli;

// 每次采样检查的生产、运输与仓储、施工和战斗不变量，全部来自公开查询。
// 返回 (对象, 字段, 说明)；progress 记录各施工任务上次的进度，用来发现倒退，本次检查结束时更新。
internal static class WorldInvariants
{
    private const double Tolerance = 1e-9;

    internal static List<(string Object, string Field, string Detail)> Check(ICoreReadView view,
        Dictionary<JobId, double> progress)
    {
        var problems = new List<(string Object, string Field, string Detail)>();
        var parked = new Dictionary<VehicleId, SettlementId>();
        foreach (var node in view.World.Nodes)
        {
            if (node.Occupant is not NodeOccupant.Settlement { Id: var id }
                || !Find(view.FindSettlement(id), $"settlement {id.Value}", out var settlement)) continue;
            var name = $"settlement {settlement.Id.Value}";
            foreach (var vehicle in settlement.ParkedVehicles.Concat(settlement.VehiclesForSale))
                parked.TryAdd(vehicle, settlement.Id);
            Inventory(settlement.Inventory, false);
            foreach (var lease in settlement.Leases)
                if (Find(view.FindLease(lease), $"lease {lease.Value}", out var found)) Inventory(found.Inventory, false);
            foreach (var facility in settlement.Facilities)
            {
                var key = $"{name} facility {facility.Key.Building.Value}/{facility.Key.Capability}";
                if (!Within(facility.ActualWorkPerSecond, 0, facility.CapabilityWorkPerSecond))
                    problems.Add((key, "actual_work_per_second", $"{facility.ActualWorkPerSecond:R} 不在 0–{facility.CapabilityWorkPerSecond:R}"));
                if (facility.Batch is { } batch && (!Within(batch.Progress, 0, 1) || !Within(batch.ProgressWork, 0, batch.FrozenWorkRequired)))
                    problems.Add((key, "batch", $"进度 {batch.Progress:R}，工作量 {batch.ProgressWork:R}/{batch.FrozenWorkRequired}"));
                if (facility.OutputBuffer.Any(goods => goods.Quantity < 0))
                    problems.Add((key, "output_buffer", "输出缓冲有负数量"));
            }
            foreach (var job in settlement.OwnedJobs.Concat(settlement.CommissionJobs).Distinct())
            {
                var key = $"job {job.Value}";
                if (!Find(view.FindJob(job), key, out var found) || found is not JobView.Construction { Value: var construction }) continue;
                if (!Within(construction.ProgressWork, 0, construction.FrozenTotalWork) || !Within(construction.Progress, 0, 1))
                    problems.Add((key, "progress", $"进度 {construction.Progress:R}，工作量 {construction.ProgressWork:R}/{construction.FrozenTotalWork}"));
                if (progress.TryGetValue(job, out var before) && construction.ProgressWork < before)
                    problems.Add((key, "progress_work", $"从 {before:R} 倒退到 {construction.ProgressWork:R}"));
                progress[job] = construction.ProgressWork;
            }
        }

        var owners = new Dictionary<VehicleId, TrainId>();
        foreach (var item in view.World.Trains)
        {
            var name = $"train {item.Id.Value}";
            if (!Find(view.FindTrain(item.Id), name, out var train)) continue;
            // 动力部件被毁后列车会从高于最大速度逐步制动，所以只要求速度有限且非负。
            if (!double.IsFinite(train.SpeedMetersPerSecond) || train.SpeedMetersPerSecond < 0)
                problems.Add((name, "speed_meters_per_second", $"{train.SpeedMetersPerSecond:R}"));
            Inventory(train.Inventory, true);
            foreach (var vehicle in train.Vehicles)
            {
                var key = $"vehicle {vehicle.Value}";
                if (!owners.TryAdd(vehicle, train.Id))
                    problems.Add((key, "train", $"同时属于列车 {owners[vehicle].Value} 和 {train.Id.Value}"));
                if (parked.TryGetValue(vehicle, out var settlement))
                    problems.Add((key, "parked", $"在列车 {train.Id.Value} 中，又停放在聚居地 {settlement.Value}"));
                if (Find(view.FindInstance(new InstanceId.Vehicle(vehicle)), key, out var instance)
                    && (instance.Details is not InstanceDetails.Vehicle { Train: { } at } || at != train.Id))
                    problems.Add((key, "details.train", $"实例记录的列车不是 {train.Id.Value}"));
            }
        }
        foreach (var vehicle in owners.Keys.Concat(parked.Keys).Distinct())
            if (Find(view.FindInstance(new InstanceId.Vehicle(vehicle)), $"vehicle {vehicle.Value}", out var instance)
                && (!Durable(instance.Structure) || instance.Parts.Any(part => !Durable(part.Durability))))
                problems.Add(($"vehicle {vehicle.Value}", "durability", "结构或部件耐久不在 0 到上限之间"));

        foreach (var item in view.World.Combats)
        {
            if (!Find(view.FindCombat(item.Id), $"combat {item.Id.Value}", out var combat)) continue;
            foreach (var participant in combat.Participants)
                if (participant.Unit switch
                    {
                        CombatUnitId.Instance { Id: var id } => view.FindInstance(id) is not Lookup<InstanceView>.Found,
                        CombatUnitId.CreatureGroup { Id: var id } => view.FindCreatureGroup(id) is not Lookup<CreatureGroupView>.Found,
                        _ => true
                    })
                    problems.Add(($"combat {item.Id.Value}", "participants", $"参战单位不存在：{participant.Unit}"));
        }
        foreach (var item in view.World.CreatureGroups)
            if (Find(view.FindCreatureGroup(item.Id), $"creature group {item.Id.Value}", out var group))
                foreach (var creature in group.Creatures)
                    if (creature.Health < 0 || creature.Health > creature.MaximumHealth)
                        problems.Add(($"creature {creature.Id.Value}", "health", $"{creature.Health}/{creature.MaximumHealth}"));
        return problems;

        void Inventory(InventoryId id, bool bounded)
        {
            var name = $"inventory {id.Value}";
            if (!Find(view.FindInventory(id), name, out var inventory)) return;
            if (inventory.Used < 0 || inventory.Reserved < 0 || bounded && inventory.Used > inventory.Capacity)
                problems.Add((name, "used", $"已用 {inventory.Used}、预留 {inventory.Reserved}、容量 {inventory.Capacity}"));
            if (inventory.Stacks.Any(stack => stack.Quantity <= 0))
                problems.Add((name, "stacks", "有非正数量的货堆"));
        }

        // 引用的对象查不到也是违反项，不中止采样。
        bool Find<T>(Lookup<T> lookup, string name, out T value) where T : notnull
        {
            if (lookup is Lookup<T>.Found found)
            {
                value = found.Value;
                return true;
            }
            problems.Add((name, "missing", "引用的对象查不到"));
            value = default!;
            return false;
        }
    }

    private static bool Within(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum - Tolerance && value <= maximum * (1 + Tolerance) + Tolerance;

    private static bool Durable(DurabilityView durability) =>
        durability.EffectiveCurrent >= 0 && durability.EffectiveCurrent <= durability.Maximum;
}
