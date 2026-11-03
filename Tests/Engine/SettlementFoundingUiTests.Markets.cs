using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SettlementFoundingUiTests
{
    [Test]
    public async Task Node_founding_lists_only_stocked_accessible_markets_in_world_order()
    {
        foreach (var stocked in new[] { true, false })
        {
            var (view, content) = FoundingMarkets(stocked);
            var setup = new HostSimulationTests.HostSetup(view, content);
            await Run(setup, async ui =>
            {
                await OpenDetail(ui, 0);
                var text = Find<Label>(LatestDetail(ui), "FoundingPurchases").Text;
                var food = text.Split('\n').Single(line => line.StartsWith("主食：", StringComparison.Ordinal));
                var equipment = text.Split('\n').Single(line => line.StartsWith("设备：", StringComparison.Ordinal));
                if (stocked)
                {
                    var first = food.IndexOf("青川", StringComparison.Ordinal);
                    var second = food.IndexOf("石港", StringComparison.Ordinal);
                    var third = food.IndexOf("柳湾", StringComparison.Ordinal);
                    Require(first >= 0 && second > first && third > second && food.Contains("等 4 处"),
                        $"补货地点必须按世界节点顺序显示前三处，并说明总共有四处：{text}");
                    Require(!new[] { "北原", "自有仓", "敌城", "空市", "受阻市" }.Any(food.Contains),
                        $"补货提示不能列出第四个名称、自有或敌对聚居地、无货或受阻市场：{text}");
                    Require(equipment.Contains("北原") && !equipment.Contains('、') && !equipment.Contains("等 "),
                        $"另一种材料只能列出出售该货物的市场：{text}");
                }
                else Require(food.Contains("目前没有市场有货") && equipment.Contains("目前没有市场有货"),
                    $"每种缺少的材料都必须说明没有市场有货：{text}");
                Require(!new[] { "价格", "单价", "距离", "米", "元" }.Any(text.Contains),
                    $"补货地点提示不应承诺价格或距离：{text}");
            });
            setup.Session.AssertComplete();
        }
    }

    [Test]
    public async Task Node_founding_groups_same_name_markets_before_limiting_locations()
    {
        var cases = new (string[] Names, string Food, string Equipment)[]
        {
            (["矿业镇经济布局", "自有仓", "敌城", "空市", "受阻市", "矿业镇经济布局", "矿业镇经济布局", "矿业镇经济布局"],
                "主食：矿业镇经济布局 4 处当前有货。", "设备：矿业镇经济布局当前有货。"),
            (["青川", "自有仓", "敌城", "空市", "受阻市", "石港", "青川", "石港"],
                "主食：青川 2 处、石港 2 处当前有货。", "设备：石港当前有货。"),
            (["青川", "自有仓", "敌城", "空市", "受阻市", "石港", "青川", "柳湾"],
                "主食：青川 2 处、石港、柳湾当前有货。", "设备：柳湾当前有货。"),
            (["青川", "自有仓", "敌城", "空市", "受阻市", "石港", "青川", "柳湾", "北原", "石港"],
                "主食：青川 2 处、石港 2 处、柳湾等 6 处当前有货。", "设备：柳湾当前有货。")
        };
        foreach (var (names, food, equipment) in cases)
        {
            var (view, content) = FoundingMarkets(true, names);
            var setup = new HostSimulationTests.HostSetup(view, content);
            await Run(setup, async ui =>
            {
                await OpenDetail(ui, 0);
                var text = Find<Label>(LatestDetail(ui), "FoundingPurchases").Text;
                Require(text == food + "\n" + equipment,
                    $"同名市场必须合并计数，按首次出现顺序列前三组，超出时说明总处数。实际：{text}");
            });
            setup.Session.AssertComplete();
        }
    }

    private static (FixedCoreReadView View, FixedContentView Content) FoundingMarkets(bool stocked, string[]? marketNames = null)
    {
        var basis = SettlementFoundingUiFixture.World(1, null, materialQuantity: 0);
        var source = EconomyFixture.Content(mapPresentation: true, equipmentName: "设备");
        var preview = ((CoreResult<SettlementFoundingPreview>.Success)basis.PreviewSettlement(new(1))).Value;
        var town = ((Lookup<SettlementView>.Found)basis.FindSettlement(new(1))).Value;
        var ground = ((ContentLookup<SettlementLayoutProjection>.Found)source.FindSettlementLayout(new(1))).Value;
        var names = marketNames ?? ["青川", "自有仓", "敌城", "空市", "受阻市", "石港", "柳湾", "北原"];
        var nodes = ImmutableArray.CreateBuilder<NodeMapItem>();
        nodes.Add(basis.World.Nodes[0]);
        var towns = new Dictionary<SettlementId, Lookup<SettlementView>>();
        var layouts = new Dictionary<SettlementId, ContentLookup<SettlementLayoutProjection>>();
        for (var index = 0; index < names.Length; index++)
        {
            var id = new SettlementId((ulong)(names.Length - index));
            var relation = index == 1 ? SettlementRelation.PlayerOwned
                : index == 2 ? SettlementRelation.Hostile : SettlementRelation.NonHostile;
            var node = basis.World.Nodes[0] with
            {
                Id = new((ulong)index + 2), Position = new(900 + index * 100, 900),
                Occupant = new NodeOccupant.Settlement(id, relation)
            };
            nodes.Add(node);
            ImmutableArray<CoreProblem> blockers = index == 4 ? [new("content_missing", "Good", null, [])] : [];
            var market = town.Market with
            {
                Goods = [new(EconomyFixture.Food, 100, 0, 0, stocked && index != 3 ? 20 : 0, 100, 0, blockers),
                    new(EconomyFixture.Equipment, 100, 0, 0, stocked && index == 7 ? 10 : 0, 100, 0, [])]
            };
            towns.Add(id, new Lookup<SettlementView>.Found(town with
            {
                Id = id, Node = node.Id, Relation = relation, Market = market,
                ParkedVehicles = [], VehiclesForSale = [], Leases = []
            }));
            layouts.Add(id, new ContentLookup<SettlementLayoutProjection>.Found(ground with
                { Owner = id, Display = new(names[index], "") }));
        }
        var train = ((Lookup<TrainView>.Found)basis.FindTrain(new(7))).Value;
        var view = new FixedCoreReadView(basis.Position, basis.InputIdentity, revision: basis.Revision)
        {
            Economy = basis.Economy,
            World = basis.World with { Nodes = nodes.ToImmutable(), Trains = [new(train.Id, train.Position, train.Activity)] },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, [], 80, 0, 80))),
            Locations = nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id), node =>
                (Lookup<LocationView>)new Lookup<LocationView>.Found(new(new WorldLocation.Node(node.Id), [], [], [], [], node.Occupant))),
            Settlements = towns,
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [train.Id] = new Lookup<TrainView>.Found(train) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
                { [new InstanceId.Vehicle(new(7))] = basis.FindInstance(new InstanceId.Vehicle(new(7))) },
            Inventories = new Dictionary<InventoryId, Lookup<InventoryView>> { [train.Inventory] = basis.FindInventory(train.Inventory) },
            FoundingPreviews = new Dictionary<NodeId, CoreResult<SettlementFoundingPreview>>
            {
                [new(1)] = new CoreResult<SettlementFoundingPreview>.Success(preview with
                    { RequiredMaterials = [new(EconomyFixture.Food, 12), new(EconomyFixture.Equipment, 2)] })
            }
        };
        var icon = new AssetRevision("fixture-food-png");
        var content = new FixedContentView(source.Core, source.Generation, source.PersistentRevision, source.Packages)
        {
            Voxels = source.Voxels, Blueprints = source.Blueprints, LocalDrafts = source.LocalDrafts,
            SettlementLayouts = layouts,
            DefinitionDisplays = new Dictionary<CoreDefinitionReference, ContentLookup<ContentDisplay>>
            {
                [new CoreDefinitionReference.Terrain(new("fixture:plain"))] = source.FindDefinitionDisplay(new CoreDefinitionReference.Terrain(new("fixture:plain"))),
                [new CoreDefinitionReference.SettlementType(town.Type)] = source.FindDefinitionDisplay(new CoreDefinitionReference.SettlementType(town.Type))
            },
            Goods = new Dictionary<GoodId, ContentLookup<GoodPresentation>>
            {
                [EconomyFixture.Food] = source.FindGood(EconomyFixture.Food),
                [EconomyFixture.Equipment] = source.FindGood(EconomyFixture.Equipment)
            },
            Assets = new Dictionary<AssetRevision, ContentLookup<ContentAsset>> { [icon] = source.FindAsset(icon) },
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
                { [new("fixture-vehicle")] = source.FindDesign(new("fixture-vehicle")) }
        };
        return (view, content);
    }
}
