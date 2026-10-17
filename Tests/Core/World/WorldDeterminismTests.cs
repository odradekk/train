using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldDeterminismTests
{
    [TestMethod]
    public void Multi_node_fixture_exercises_terrain_occupants_resources_railway_and_fleet()
    {
        using var candidate = Prepare(MultiInput, MultiContent);
        using var session = (CoreSession)candidate.Activate();
        var state = session.State.World;
        var world = session.Read().World;
        Assert.IsTrue(world.Nodes.Length > 1);
        Assert.AreEqual(2, world.Nodes.Select(n => n.Terrain.TypeId).Distinct().Count());
        Assert.IsTrue(state!.Settlements.Select(s => s.Type).Distinct().Count() >= 2);
        Assert.IsTrue(state.Lairs.Length > 0);
        Assert.IsTrue(world.Nodes.All(n => n.Resources.Length == 2));
        Assert.AreEqual((int)Math.Floor(world.Nodes.Length * .4), state.Settlements.Length);
        Assert.AreEqual((int)Math.Floor(world.Nodes.Length * .1), state.Lairs.Length);
        Assert.AreEqual(2, state.Trains.Length);
        CollectionAssert.AreEqual(new[] { Vehicle, SecondVehicle, SecondVehicle, Vehicle },
            state.Instances.Where(i => i.Id is InstanceId.Vehicle).Select(i => i.Design).ToArray());
        var root = ((TrainPosition.Node)world.Trains[0].Position).Id;
        var reached = new HashSet<NodeId> { root };
        var pending = new Queue<NodeId>(reached);
        while (pending.TryDequeue(out var node))
            foreach (var edge in world.Edges.Where(e => e.Tracks > 0))
            {
                if (edge.Start == node && reached.Add(edge.End)) pending.Enqueue(edge.End);
                if (edge.End == node && reached.Add(edge.Start)) pending.Enqueue(edge.Start);
            }
        Assert.IsTrue(world.Nodes.Any(n => reached.Contains(n.Id) && n.Occupant is NodeOccupant.Empty));
        Assert.AreEqual(1, world.Edges.Count(e => e.Tracks > 0) - reached.Count + 1);
    }

    [TestMethod]
    public void Permuting_all_unordered_inputs_preserves_generated_payload_and_checkpoint()
    {
        using var first = Prepare(MultiInput, MultiContent);
        using var second = Prepare(Permute(MultiInput), Permute(MultiContent));
        using var firstSession = first.Activate();
        using var secondSession = second.Activate();
        var left = Success(firstSession.Capture(CapturePurpose.Save));
        var right = Success(secondSession.Capture(CapturePurpose.Save));
        Assert.AreEqual(left.Encode(), right.Encode());
        Assert.AreEqual(left.Checkpoint, right.Checkpoint);
    }

    [TestMethod]
    public void Stage1_world_v1_multi_node_generation_matches_frozen_checkpoint_and_structure()
    {
        // stage1-world-v1 的审核基线；生成器或状态格式升级时必须显式审核此预期，不能自动刷新。
        // 与跨进程初始采样一致，内容身份为 A；摘要另由 Python hashlib 校验。
        using var candidate = Prepare(MultiInput, MultiContent with { Identity = new(new string('A', 64)) });
        using var session = candidate.Activate();
        var world = session.Read().World;
        var capture = Success(session.Capture(CapturePurpose.Save));
        Assert.AreEqual("9D0E94535DC58792D6F76F2555B3A67A351A712BC80B3BC3B3D0D6CE829FD47D",
            capture.Checkpoint.Sha256);
        Assert.AreEqual(19, world.Nodes.Length);
        Assert.AreEqual(22, world.Edges.Length);
        Assert.AreEqual(7, world.Nodes.Count(node => node.Occupant is NodeOccupant.Settlement));
        Assert.AreEqual(1, world.Nodes.Count(node => node.Occupant is NodeOccupant.Lair));
        Assert.AreEqual(14, world.Edges.Count(edge => edge.Tracks > 0));
        var startId = ((TrainPosition.Node)world.Trains[0].Position).Id;
        Assert.AreEqual(new NodeId(2), startId);
        var start = world.Nodes.Single(node => node.Id == startId).Position;
        Assert.AreEqual(1d, Math.Floor(start.X / 10));
        Assert.AreEqual(0d, Math.Floor(start.Y / 10));
    }

    [TestMethod]
    public void Signed_zero_and_permuted_duplicate_bands_have_the_same_canonical_capture()
    {
        var positive = WorldFixture.Input.Configuration;
        var negativeZero = BitConverter.UInt64BitsToDouble(1UL << 63);
        var signedBand = positive.TerrainBands[0] with { MinimumElevation = negativeZero, MinimumMoisture = negativeZero };
        var input = WorldFixture.Input with { Configuration = positive with
            { ElevationNoise = [new(8, 0), new(16, 0)], TerrainBands = [positive.TerrainBands[0], signedBand] } };
        using var first = Prepare(input);
        using var second = Prepare(input with { Configuration = input.Configuration with
            { ElevationNoise = [new(16, negativeZero), new(8, negativeZero)], TerrainBands = [signedBand, positive.TerrainBands[0]],
                LairRatio = negativeZero, RiverBandHalfWidth = negativeZero } });
        using var left = first.Activate();
        using var right = second.Activate();
        var a = Success(left.Capture(CapturePurpose.Save));
        var b = Success(right.Capture(CapturePurpose.Save));
        Assert.AreEqual(a.Encode(), b.Encode());
        Assert.AreEqual(a.Checkpoint, b.Checkpoint);
    }

    private static WorldGenerationInput Permute(WorldGenerationInput input) => input with
    {
        Configuration = input.Configuration with
        {
            ElevationNoise = Reverse(input.Configuration.ElevationNoise),
            MoistureNoise = Reverse(input.Configuration.MoistureNoise),
            RiverNoise = Reverse(input.Configuration.RiverNoise),
            TerrainBands = Reverse(input.Configuration.TerrainBands),
            LairWeights = Reverse(input.Configuration.LairWeights),
            Resources = Reverse(input.Configuration.Resources).Select(resource => resource with
                { AllowedTerrainTypes = Reverse(resource.AllowedTerrainTypes) }).ToImmutableArray(),
            NpcInitialization = input.Configuration.NpcInitialization with
                { ForSaleVehicleCandidates = Reverse(input.Configuration.NpcInitialization.ForSaleVehicleCandidates) }
        },
        // 车队及车辆编组是有序输入；只重排每列车的货物集合。
        Start = input.Start with { Trains = input.Start.Trains.Select(train => train with
            { Inventory = Reverse(train.Inventory) }).ToImmutableArray() }
    };

    private static CoreContentProjection Permute(CoreContentProjection content) => content with
    {
        Goods = Reverse(content.Goods), DesignGoods = Reverse(content.DesignGoods),
        Designs = Reverse(content.Designs).Select(design => design with
        {
            Kind = design.Kind is DesignKind.Building building ? building with
                { FacilityTags = Reverse(building.FacilityTags) } : design.Kind,
            Parts = Reverse(design.Parts).Select(part => part with
                { Capabilities = Reverse(part.Capabilities) }).ToImmutableArray(),
            StructuralGoods = Reverse(design.StructuralGoods), BillOfMaterials = Reverse(design.BillOfMaterials),
            ReturnUnits = Reverse(design.ReturnUnits).Select(unit => unit with
                { Parts = Reverse(unit.Parts), StructuralGoods = Reverse(unit.StructuralGoods),
                    MissingReferences = Reverse(unit.MissingReferences) }).ToImmutableArray(),
            MissingReferences = Reverse(design.MissingReferences)
        }).ToImmutableArray(),
        Processes = Reverse(content.Processes).Select(process => process with
        {
            Details = process.Details switch
            {
                ProcessDetails.Production production => production with
                    { Inputs = Reverse(production.Inputs), Outputs = Reverse(production.Outputs) },
                ProcessDetails.Recovery recovery => recovery with
                    { Inputs = Reverse(recovery.Inputs), Outputs = Reverse(recovery.Outputs) },
                _ => process.Details
            },
            AllowedTerrainTypes = Reverse(process.AllowedTerrainTypes)
        }).ToImmutableArray(),
        PopulationRules = Reverse(content.PopulationRules).Select(rule => rule with
        {
            Needs = Reverse(rule.Needs), ConsumptionGoods = Reverse(rule.ConsumptionGoods).Select(good => good with
                { Satisfies = Reverse(good.Satisfies) }).ToImmutableArray()
        }).ToImmutableArray(),
        MarketRules = Reverse(content.MarketRules).Select(rule => rule with
            { GoodPrices = Reverse(rule.GoodPrices), VehiclePurchaseLimits = Reverse(rule.VehiclePurchaseLimits) }).ToImmutableArray(),
        ConstructionRules = Reverse(content.ConstructionRules), RailwayRules = Reverse(content.RailwayRules),
        CombatRules = Reverse(content.CombatRules),
        SettlementTypes = Reverse(content.SettlementTypes).Select(type => type with
            { PreferredTerrain = Reverse(type.PreferredTerrain) }).ToImmutableArray(),
        SettlementTemplates = Reverse(content.SettlementTemplates).Select(template => template with
        {
            Plots = Reverse(template.Plots), FoundingBuildings = Reverse(template.FoundingBuildings),
            SpecialInitialInventory = Reverse(template.SpecialInitialInventory), ProductionPlans = Reverse(template.ProductionPlans)
        }).ToImmutableArray(),
        Creatures = Reverse(content.Creatures), LairTypes = Reverse(content.LairTypes).Select(lair => lair with
            { Members = Reverse(lair.Members) }).ToImmutableArray(),
        MissingReferences = Reverse(content.MissingReferences)
    };

    private static ImmutableArray<T> Reverse<T>(ImmutableArray<T> values) => values.Reverse().ToImmutableArray();
}
