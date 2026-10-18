using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class NpcInitializationTests
{
    [TestMethod]
    public void Generated_inputs_and_living_goods_match_runtime_market_targets_including_substitute_food()
    {
        var content = NpcInitializationFixture.Content();
        using var candidate = WorldFixture.Prepare(NpcInitializationFixture.Input(fill: true), content);
        using var session = candidate.Activate();
        var town = NpcInitializationFixture.Town(session);
        foreach (var good in town.Market.Goods)
            Assert.AreEqual(good.RestockTarget, good.Stock, good.Good.ToString());
        Assert.IsTrue(town.Market.Goods.Single(g => g.Good == NpcInitializationFixture.Staple).Stock > 0);
        Assert.IsTrue(town.Market.Goods.Single(g => g.Good == NpcInitializationFixture.Prepared).Stock > 2);
        Assert.IsTrue(town.Market.Goods.Single(g => g.Good == NpcInitializationFixture.InputGood).Stock > 0);
    }

    [TestMethod]
    public void Special_inventory_occupies_capacity_before_proportional_target_gap_allocation()
    {
        var content = NpcInitializationFixture.Content(capacity: 4, inventory: [new(NpcInitializationFixture.OutputA, 2)]);
        using var candidate = WorldFixture.Prepare(NpcInitializationFixture.Input(fill: true, hours: 1d / 3600), content);
        using var session = candidate.Activate();
        var goods = NpcInitializationFixture.Town(session).Market.Goods;
        Assert.AreEqual(2L, goods.Single(g => g.Good == NpcInitializationFixture.OutputA).Stock);
        Assert.AreEqual(1L, goods.Single(g => g.Good == NpcInitializationFixture.Staple).Stock);
        Assert.AreEqual(1L, goods.Single(g => g.Good == NpcInitializationFixture.Clothing).Stock);
        Assert.AreEqual(4L, goods.Sum(g => g.Stock));
    }

    [TestMethod]
    [DataRow(.25, 0L, 20L, 1L, 1L)]
    [DataRow(1d, 1L, 20L, 3L, 1L)]
    [DataRow(1d, 1L, 3L, 2L, 1L)]
    [DataRow(double.MaxValue, 0L, 8L, 6L, 2L)]
    public void Output_reserves_round_after_aggregation_offset_existing_stock_and_share_remaining_space(
        double seconds, long existing, long capacity, long expectedA, long expectedB)
    {
        var content = NpcInitializationFixture.Content(capacity, existing == 0 ? [] : [new(NpcInitializationFixture.OutputA, existing)]);
        using var candidate = WorldFixture.Prepare(NpcInitializationFixture.Input(hours: seconds == double.MaxValue ? seconds : seconds / 3600), content);
        using var session = candidate.Activate();
        var goods = NpcInitializationFixture.Town(session).Market.Goods;
        Assert.AreEqual(expectedA, goods.Single(g => g.Good == NpcInitializationFixture.OutputA).Stock);
        Assert.AreEqual(expectedB, goods.Single(g => g.Good == NpcInitializationFixture.OutputB).Stock);
        Assert.AreEqual(0L, goods.Single(g => g.Good == NpcInitializationFixture.InputGood).Stock);
    }

    [TestMethod]
    public void Large_finite_output_rates_preserve_proportions_when_multiplication_or_aggregation_overflows()
    {
        var content = NpcInitializationFixture.Content(capacity: 8, inventory: []);
        var factory = content.Designs.Single(d => d.Design == WorldFixture.FactoryBuilding);
        content = content with { Designs = content.Designs.Replace(factory, factory with
            { Parts = [factory.Parts[0] with { Capabilities = [new PartCapability.Production(1e308)] }] }) };
        using var candidate = WorldFixture.Prepare(NpcInitializationFixture.Input(hours: 1d / 3600), content);
        using var session = (CoreSession)candidate.Activate();
        var inventory = session.State.World!.Inventories.Single(i => i.Id == session.State.World.Settlements.Single().Inventory);
        Assert.AreEqual(6L, inventory.Goods.Single(g => g.Good == NpcInitializationFixture.OutputA).Quantity);
        Assert.AreEqual(2L, inventory.Goods.Single(g => g.Good == NpcInitializationFixture.OutputB).Quantity);
    }

    [TestMethod]
    [DataRow(false, false, false, 3L)]
    [DataRow(false, false, true, 3L)]
    [DataRow(true, false, false, 2L)]
    [DataRow(false, true, false, 2L)]
    public void Output_reserves_use_the_same_enabled_and_environment_eligible_potential_facilities(
        bool disabled, bool wrongTerrain, bool supplied, long expected)
    {
        var content = NpcInitializationFixture.Content(inventory: supplied ? [new(NpcInitializationFixture.InputGood, 4)] : []);
        var template = content.SettlementTemplates[0];
        if (disabled)
            content = content with { SettlementTemplates = [template with
                { ProductionPlans = template.ProductionPlans.SetItem(1, template.ProductionPlans[1] with { Enabled = false }) }] };
        if (wrongTerrain)
        {
            var process = content.Processes[0] with { Details = new ProcessDetails.Production(new("fixture:other-process"), [new(NpcInitializationFixture.InputGood, 2)], [new(NpcInitializationFixture.OutputA, 3), new(NpcInitializationFixture.OutputB, 1)]), AllowedTerrainTypes = [WorldFixture.Hills] };
            content = content with { Processes = content.Processes.Add(process), SettlementTemplates = [template with
                { ProductionPlans = template.ProductionPlans.SetItem(1, template.ProductionPlans[1] with
                    { Process = new ProcessRef.Production(new("fixture:other-process")) }) }] };
        }
        using var candidate = WorldFixture.Prepare(NpcInitializationFixture.Input(hours: 1d / 3600), content);
        using var session = candidate.Activate();
        var town = NpcInitializationFixture.Town(session);
        Assert.AreEqual(expected, town.Market.Goods.Single(g => g.Good == NpcInitializationFixture.OutputA).Stock);
        Assert.AreEqual(supplied ? 4L : 0L, town.Market.Goods.Single(g => g.Good == NpcInitializationFixture.InputGood).Stock);
    }

    [TestMethod]
    public void Extraction_reserves_use_potential_output_without_consuming_or_limiting_by_node_reserves()
    {
        var content = NpcInitializationFixture.Content(inventory: []);
        var factory = content.Designs.Single(d => d.Design == WorldFixture.FactoryBuilding);
        var extractionHash = new DesignHash(new string('7', 64));
        var extraction = factory with { Design = extractionHash,
            Kind = new DesignKind.Building(PlotSize.Small, BuildingKind.Production, [FacilityTag.Extraction]),
            Parts = [factory.Parts[0] with { Capabilities = [new PartCapability.Extraction(1)] }] };
        var process = new ProcessProjection(new ProcessDetails.Extraction(new("fixture:npc-extraction"),
            new(NpcInitializationFixture.OutputA, 3)), WorldFixture.Revision, 1, 0, 0, FacilityTag.Extraction, []);
        var template = content.SettlementTemplates[0];
        content = content with { Designs = content.Designs.Add(extraction), Processes = content.Processes.Add(process),
            SettlementTemplates = [template with {
                Plots = template.Plots.SetItem(2, template.Plots[2] with { PrebuiltDesign = extractionHash }),
                ProductionPlans = template.ProductionPlans.SetItem(1, new(3, CapabilityKind.Extraction,
                    new ProcessRef.Production(new("fixture:npc-extraction")), true, 1)) }] };
        var input = NpcInitializationFixture.Input(hours: 1d / 3600);
        input = input with { Configuration = input.Configuration with {
            Resources = [new(new("fixture:output-a"), [WorldFixture.Plain], 1, 1, 1, false, 0, 10, 0)] } };
        using var candidate = WorldFixture.Prepare(input, content);
        using var session = candidate.Activate();
        var town = NpcInitializationFixture.Town(session);
        // One second yields 3 extracted units plus 1.5 manufactured units, rounded after combining.
        Assert.AreEqual(5L, town.Market.Goods.Single(g => g.Good == NpcInitializationFixture.OutputA).Stock);
        Assert.AreEqual(1L, session.Read().World.Nodes.Single().Resources.Single().Quantity);
    }

    [TestMethod]
    public void Generated_vehicles_obey_workshop_tiers_and_slots_and_ignore_candidate_order()
    {
        var content = NpcInitializationFixture.Content();
        var input = NpcInitializationFixture.Input(vehicles: true);
        var alternate = new DesignHash(new string('8', 64));
        var station = content.Designs.Single(d => d.Design == WorldFixture.Station);
        content = content with
        {
            Designs = content.Designs.Replace(station, station with { Parts = [station.Parts[0] with
                { Capabilities = [new PartCapability.Platform(), new PartCapability.Storage(200),
                    new PartCapability.Construction(1, PlotSize.Large, SizeClass.Medium)] }] })
                .Add(content.Designs[0] with { Design = alternate, Kind = new DesignKind.Vehicle(SizeClass.Medium, 1) }),
            MarketRules = [content.MarketRules[0] with
                { VehiclePurchaseLimits = [new(SizeClass.Small, 1), new(SizeClass.Medium, 1), new(SizeClass.Large, 3)] }]
        };
        input = input with { Configuration = input.Configuration with { NpcInitialization = input.Configuration.NpcInitialization with
            { ForSaleVehicleCandidates = [WorldFixture.Vehicle, alternate, WorldFixture.SecondVehicle] } } };
        using var first = WorldFixture.Prepare(input, content);
        using var session = first.Activate();
        var town = NpcInitializationFixture.Town(session);
        Assert.AreEqual(2, town.VehiclesForSale.Length);
        foreach (var size in new[] { SizeClass.Small, SizeClass.Medium })
        {
            var capacity = town.VehiclePurchaseCapacities.Single(c => c.Size == size);
            Assert.AreEqual(1, capacity.ForSale);
            Assert.AreEqual(0, capacity.Available);
        }
        foreach (var id in town.VehiclesForSale)
        {
            var vehicle = ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(id))).Value;
            Assert.IsTrue(vehicle.Design == WorldFixture.Vehicle || vehicle.Design == alternate);
            Assert.AreEqual(town.Relation, vehicle.Ownership);
            Assert.AreEqual(100L, vehicle.Structure.EffectiveCurrent);
            Assert.IsTrue(vehicle.Parts.All(p => p.Durability.EffectiveCurrent == 100));
        }
        var npc = input.Configuration.NpcInitialization;
        using var second = WorldFixture.Prepare(input with { Configuration = input.Configuration with
            { NpcInitialization = npc with { ForSaleVehicleCandidates = npc.ForSaleVehicleCandidates.Reverse().ToImmutableArray() } } }, content);
        using var reordered = second.Activate();
        Assert.AreEqual(WorldFixture.Success(session.Capture(CapturePurpose.Save)).Checkpoint.CanonicalText,
            WorldFixture.Success(reordered.Capture(CapturePurpose.Save)).Checkpoint.CanonicalText);
    }

    [TestMethod]
    public void Repainting_a_candidate_preserves_generated_vehicle_composition_for_the_same_seed()
    {
        var (input, content) = NpcInitializationFixture.RandomVehicles();
        var npc = input.Configuration.NpcInitialization with { MinimumForSaleVehicles = 1, MaximumForSaleVehicles = 1 };
        input = input with { WorldSeed = 40, Configuration = input.Configuration with { NpcInitialization = npc } };
        var firstDesign = content.Designs.Single(d => d.Design == WorldFixture.Vehicle);
        var secondDesign = content.Designs.Single(d => d.Design == WorldFixture.SecondVehicle);
        content = content with { Designs = content.Designs
            .Replace(firstDesign, firstDesign with { Composition = new(new string('1', 64)) })
            .Replace(secondDesign, secondDesign with { Composition = new(new string('2', 64)) }) };
        using var originalCandidate = WorldFixture.Prepare(input, content);
        using var original = originalCandidate.Activate();
        var originalVehicle = NpcInitializationFixture.Town(original).VehiclesForSale.Single();
        var originalInstance = ((Lookup<InstanceView>.Found)original.Read()
            .FindInstance(new InstanceId.Vehicle(originalVehicle))).Value;

        var repaintedHash = new DesignHash(new string('0', 64));
        secondDesign = content.Designs.Single(d => d.Design == WorldFixture.SecondVehicle);
        var repaintedContent = content with { Designs = content.Designs.Replace(secondDesign, secondDesign with { Design = repaintedHash }) };
        var repaintedInput = input with { Configuration = input.Configuration with { NpcInitialization = npc with
            { ForSaleVehicleCandidates = [WorldFixture.Vehicle, repaintedHash] } } };
        using var repaintedCandidate = WorldFixture.Prepare(repaintedInput, repaintedContent);
        using var repainted = repaintedCandidate.Activate();
        var repaintedVehicle = NpcInitializationFixture.Town(repainted).VehiclesForSale.Single();
        var repaintedInstance = ((Lookup<InstanceView>.Found)repainted.Read()
            .FindInstance(new InstanceId.Vehicle(repaintedVehicle))).Value;

        Assert.AreEqual(originalInstance.Composition, repaintedInstance.Composition);
        Assert.AreEqual(repaintedContent.Designs.Single(d => d.Composition == originalInstance.Composition).Design,
            repaintedInstance.Design);
    }

    [TestMethod]
    public void Random_vehicle_counts_cover_both_bounds_and_select_every_eligible_design()
    {
        var (input, content) = NpcInitializationFixture.RandomVehicles();
        var counts = new HashSet<int>();
        var designs = new HashSet<DesignHash>();
        for (ulong seed = 0; seed < 16; seed++)
        {
            using var candidate = WorldFixture.Prepare(input with { WorldSeed = seed }, content);
            using var session = candidate.Activate();
            var vehicles = NpcInitializationFixture.Town(session).VehiclesForSale;
            Assert.IsTrue(vehicles.Length is >= 0 and <= 3, $"seed={seed}, count={vehicles.Length}");
            counts.Add(vehicles.Length);
            foreach (var id in vehicles)
                designs.Add(((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(id))).Value.Design);
        }
        Assert.IsTrue(counts.Contains(0), "固定种子组须覆盖数量下界。");
        Assert.IsTrue(counts.Contains(3), "固定种子组须覆盖数量上界。");
        CollectionAssert.AreEquivalent(input.Configuration.NpcInitialization.ForSaleVehicleCandidates.ToArray(), designs.ToArray());
    }

    [TestMethod]
    public void Hostile_npc_settlements_receive_inventory_and_vehicles_even_when_purchase_capacity_is_unavailable()
    {
        var input = NpcInitializationFixture.Input(fill: true, vehicles: true);
        input = input with { Configuration = input.Configuration with
            { WidthMeters = 30, HeightMeters = 30, HostileSettlementRatio = .5 } };
        using var candidate = WorldFixture.Prepare(input, NpcInitializationFixture.Content());
        using var session = candidate.Activate();
        var hostile = session.Read().World.Nodes.Select(n => n.Occupant).OfType<NodeOccupant.Settlement>()
            .Where(s => s.Relation == SettlementRelation.Hostile).ToArray();
        Assert.IsTrue(hostile.Length > 0);
        foreach (var occupant in hostile)
        {
            var town = ((Lookup<SettlementView>.Found)session.Read().FindSettlement(occupant.Id)).Value;
            Assert.AreEqual(2, town.VehiclesForSale.Length);
            Assert.IsTrue(town.Market.Goods.Any(g => g.Good == NpcInitializationFixture.InputGood && g.Stock > 0));
            Assert.IsTrue(town.VehiclePurchaseCapacities.All(c => c.Available == 0));
            foreach (var id in town.VehiclesForSale)
                Assert.AreEqual(SettlementRelation.Hostile,
                    ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(id))).Value.Ownership);
        }
    }

    [TestMethod]
    public void Missing_workshop_generates_no_vehicles_and_insufficient_eligible_slots_fail_without_retry()
    {
        var content = NpcInitializationFixture.Content(workshop: false);
        using (var candidate = WorldFixture.Prepare(NpcInitializationFixture.Input(vehicles: true), content))
            Assert.IsTrue(((Lookup<SettlementView>.Found)candidate.View.FindSettlement(((NodeOccupant.Settlement)candidate.View.World.Nodes.Single().Occupant).Id)).Value.VehiclesForSale.IsEmpty);
        content = NpcInitializationFixture.Content();
        content = content with { MarketRules = [content.MarketRules[0] with { VehiclePurchaseLimits = [new(SizeClass.Small, 1)] }] };
        var result = CoreFactory.Create().PrepareNew(NpcInitializationFixture.Input(vehicles: true), content, WorldFixture.Options);
        var problem = ((CoreResult<ICoreSessionCandidate>.Failure)result).Problems.Single();
        Assert.AreEqual("Configuration.NpcInitialization.MinimumForSaleVehicles", problem.Field);
        Assert.AreEqual("config_invalid", problem.RelatedIdentities[0]);
        Assert.AreEqual("0", problem.RelatedIdentities[3]);
        var input = NpcInitializationFixture.Input(vehicles: true);
        input = input with { Configuration = input.Configuration with { NpcInitialization = input.Configuration.NpcInitialization with
            { ForSaleVehicleCandidates = [WorldFixture.SecondVehicle] } } };
        var noEligible = (CoreResult<ICoreSessionCandidate>.Failure)CoreFactory.Create().PrepareNew(input,
            NpcInitializationFixture.Content(), WorldFixture.Options);
        Assert.AreEqual(problem.Field, noEligible.Problems.Single().Field);
    }

    [TestMethod]
    public void Buying_a_generated_vehicle_restores_and_strictly_replays_the_same_boundary()
    {
        var content = NpcInitializationFixture.Content();
        using var candidate = WorldFixture.Prepare(NpcInitializationFixture.Input(fill: true, vehicles: true), content);
        using var session = candidate.Activate();
        var town = NpcInitializationFixture.Town(session);
        var vehicle = town.VehiclesForSale[0];
        Assert.IsTrue(session.Submit(new CoreCommand.BuyVehicle(town.Id, vehicle, new(1000)), CommandSource.AutomationTest)
            is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted });
        Assert.AreEqual(SettlementRelation.PlayerOwned, ((Lookup<InstanceView>.Found)session.Read().FindInstance(new InstanceId.Vehicle(vehicle))).Value.Ownership);
        var saved = WorldFixture.Success(session.Capture(CapturePurpose.Save));
        var factory = CoreFactory.Create();
        using var restored = WorldFixture.Success(factory.PrepareRestore(WorldFixture.Success(factory.DecodeState(saved.Encode())), content, WorldFixture.Options));
        using var restoredSession = restored.Activate();
        Assert.AreEqual(saved.Encode(), WorldFixture.Success(restoredSession.Capture(CapturePurpose.Save)).Encode());
        var recording = session.ReadRecording();
        var input = new CoreReplayInput(2, WorldFixture.Options.Runtime, recording.Starts[0].State.Encode(),
            recording.Commands, [saved.Checkpoint], recording.EndPosition, null);
        using var replay = WorldFixture.Success(CoreFactory.Create().PrepareReplay(input, WorldFixture.Options.Runtime, [content]));
        var result = replay.Advance(0, saved.Position);
        Assert.IsNull(result.Difference);
        Assert.IsNull(result.Fault);
        Assert.AreEqual(saved.Encode(), replay.CaptureBoundary().Encode());
    }
}

internal static class NpcInitializationFixture
{
    internal static GoodId InputGood => new GoodId.Voxel(WorldFixture.Material);
    internal static GoodId Staple => new GoodId.Voxel(new("fixture:food"));
    internal static GoodId Prepared => new GoodId.Voxel(new("fixture:prepared-food"));
    internal static GoodId Clothing => new GoodId.Voxel(new("fixture:clothing"));
    internal static GoodId OutputA => new GoodId.Voxel(new("fixture:output-a"));
    internal static GoodId OutputB => new GoodId.Voxel(new("fixture:output-b"));

    internal static WorldGenerationInput Input(bool fill = false, double hours = 0, bool vehicles = false) => WorldFixture.Input with
    {
        Configuration = WorldFixture.Input.Configuration with
        { NpcInitialization = new(fill, hours, vehicles ? 2 : 0, vehicles ? 5 : 0, [WorldFixture.Vehicle, WorldFixture.SecondVehicle]) }
    };

    internal static (WorldGenerationInput Input, CoreContentProjection Content) RandomVehicles()
    {
        var input = Input(fill: true, hours: 1d / 3600, vehicles: true);
        input = input with { Configuration = input.Configuration with { NpcInitialization = input.Configuration.NpcInitialization with
            { MinimumForSaleVehicles = 0, MaximumForSaleVehicles = 3 } } };
        var content = Content();
        var second = content.Designs.Single(d => d.Design == WorldFixture.SecondVehicle);
        content = content with
        {
            Designs = content.Designs.Replace(second, second with { Kind = new DesignKind.Vehicle(SizeClass.Small, 1) }),
            MarketRules = [content.MarketRules[0] with { VehiclePurchaseLimits = [new(SizeClass.Small, 4)] }]
        };
        return (input, content);
    }

    internal static CoreContentProjection Content(long capacity = 200, ImmutableArray<GoodsQuantity> inventory = default, bool workshop = true)
    {
        var content = WorldFixture.Content;
        var station = content.Designs.Single(d => d.Design == WorldFixture.Station);
        var capabilities = ImmutableArray.Create<PartCapability>(new PartCapability.Platform(), new PartCapability.Storage(capacity));
        if (workshop) capabilities = capabilities.Add(new PartCapability.Construction(1, PlotSize.Large, SizeClass.Small));
        station = station with { Parts = [station.Parts[0] with { Capabilities = capabilities }] };
        var factory = station with { Design = WorldFixture.FactoryBuilding,
            Kind = new DesignKind.Building(PlotSize.Small, BuildingKind.Production, [FacilityTag.Agriculture]),
            Parts = [station.Parts[0] with { Capabilities = [new PartCapability.Production(1)] }] };
        var template = content.SettlementTemplates[0];
        return content with
        {
            Goods = [.. content.Goods, new(OutputA, WorldFixture.Revision, new(1), 1, true, null), new(OutputB, WorldFixture.Revision, new(1), 1, true, null)],
            Designs = [content.Designs[0], station, factory, content.Designs[0] with { Design = WorldFixture.SecondVehicle, Kind = new DesignKind.Vehicle(SizeClass.Large, 1) }],
            Processes = [new(new ProcessDetails.Production(new("fixture:npc-process"), [new(InputGood, 2)], [new(OutputA, 3), new(OutputB, 1)]), WorldFixture.Revision, 2, 0, 0, FacilityTag.Agriculture, [])],
            MarketRules = [content.MarketRules[0] with { BasicReserveHours = 1d / 3600, ImprovementReserveHours = 1d / 3600, ProductionReserveHours = 1d / 3600, VehiclePurchaseLimits = [new(SizeClass.Small, 2), new(SizeClass.Large, 3)] }],
            SettlementTemplates = [template with { Plots = [.. template.Plots, new(2, PlotSize.Small, WorldFixture.FactoryBuilding), new(3, PlotSize.Small, WorldFixture.FactoryBuilding)],
                SpecialInitialInventory = inventory.IsDefault ? [new(Prepared, 2)] : inventory,
                ProductionPlans = [new(2, CapabilityKind.Production, new ProcessRef.Production(new("fixture:npc-process")), true, 0), new(3, CapabilityKind.Production, new ProcessRef.Production(new("fixture:npc-process")), true, 1)] }]
        };
    }

    internal static SettlementView Town(ICoreSession session) =>
        ((Lookup<SettlementView>.Found)session.Read().FindSettlement(((NodeOccupant.Settlement)session.Read().World.Nodes.Single().Occupant).Id)).Value;
}
