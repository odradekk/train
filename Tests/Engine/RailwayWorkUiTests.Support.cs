using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class RailwayWorkUiTests
{
    private async Task Run(HostSimulationTests.HostSetup setup, Func<GameUi, Task> body,
        IScenesFactory? scenes = null)
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            if (scenes is not null) app.CreateScenes = () => scenes;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            await body(app.GetNode<GameUi>("GameUi"));
        }
        finally { await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }
    private async Task<PanelContainer> OpenFromList(GameUi ui, int edgeIndex)
    {
        if (Find<GameShell>(ui, "World").ListContent?.Name != "EdgesList")
            await _input.Activate(Find<Button>(ui, "Edges"));
        await _input.ClickItem(Find<ItemList>(Find<PanelContainer>(ui, "EdgesList"), "Objects"), edgeIndex);
        await _input.Activate(Find<Button>(LatestDetail(ui), "RailwayWork"));
        return Find<PanelContainer>(ui, "RailwayWork");
    }

    private async Task DismissError(GameUi ui)
    {
        var panel = ui.GetNode<PanelContainer>("Root/ErrorPanel");
        if (panel.Visible) await _input.Activate(Find<Button>(panel, "Close"));
    }

    private sealed class LineScene(SceneBinding binding) : IWorldSceneSession
    {
        public SceneSessionId Id { get; } = new(Guid.NewGuid());
        public SceneBinding Binding => binding;
        public WorldSceneTarget? Target { get; private set; }
        public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target)
        {
            Target = target;
            return new SceneResult<SceneRequestId>.Success(new(1));
        }
        public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot, ImmutableArray<EventEnvelope> events)
            => new SceneResult<SimulationPosition>.Success(snapshot.Core.Position);
        public SceneResult<ISceneFrame> Present(SceneFrameRequest request, ISceneFrame? displayedFrame)
            => new SceneResult<ISceneFrame>.Success(new FixedSceneFrame
            {
                Update = new(null, new(Id, new(1), new(1), 1), default, [], [], new([], []), []),
                TerrainHeightResult = new Lookup<double>.Missing()
            });
        public SceneResult<SceneUpdate?> PresentCandidate(SceneFrameStamp? prepared) => new SceneResult<SceneUpdate?>.Success(null);
        public SceneResult<PickSubject> Resolve(SceneHit hit) =>
            throw new InvalidOperationException("施工测试不拾取场景。");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static EdgeView Edge(HostCoordinator host, ulong id) =>
        ((Lookup<EdgeView>.Found)host.GetStatus()!.View.FindEdge(new(id))).Value;
    private static PanelContainer LatestDetail(GameUi ui) => Find<PanelContainer>(ui, "Details");

    private static void Accept(HostCoordinator host, CoreCommand command)
    {
        var result = host.Submit(host.GetStatus()!.Binding, command, CommandSource.AutomationTest);
        if (result is not SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted })
            throw new InvalidOperationException(command.GetType().Name + " 被拒绝：" + DecisionText(result));
    }

    private static string DecisionText(SubmissionResult result) => result switch
    {
        SubmissionResult.NotAccepted rejected => CommandDiagnostics.Describe(rejected.Reason),
        SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } =>
            string.Join("；", rejected.Problems.Select(problem => CommandDiagnostics.Describe(problem))),
        SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted faulted } => CommandDiagnostics.Describe(faulted.Fault),
        SubmissionResult.Completed completed => completed.Receipt.Decision.GetType().Name,
        _ => result.ToString() ?? ""
    };

    private static string ClaimSettlement(string payload, string railGood)
    {
        var root = JsonNode.Parse(payload)!;
        var world = root["world"]!;
        JsonNode? settlement = null;
        foreach (var item in world["settlements"]!.AsArray())
            if (item!["node"]!["value"]!.GetValue<ulong>() == 14) settlement = item;
        if (settlement is null) throw new InvalidOperationException("存档里没有节点 14 的聚居地。");
        settlement["relation"] = (int)SettlementRelation.PlayerOwned;
        var settlementId = settlement["id"]!["value"]!.GetValue<ulong>();
        var buildings = new HashSet<ulong>();
        foreach (var plot in settlement["plots"]!.AsArray())
            if (plot!["building"]?["value"] is { } building) buildings.Add(building.GetValue<ulong>());
        var parked = new HashSet<ulong>();
        foreach (var vehicle in settlement["parked_vehicles"]!.AsArray()) parked.Add(vehicle!["value"]!.GetValue<ulong>());
        var claimedNode = false;
        foreach (var node in world["nodes"]!.AsArray())
        {
            if (node!["id"]!["value"]!.GetValue<ulong>() != 14) continue;
            var occupant = node["occupant"]!;
            if (occupant["$kind"]!.GetValue<string>() != "settlement" || occupant["id"]!["value"]!.GetValue<ulong>() != settlementId)
                throw new InvalidOperationException("节点 14 的占据者不是该聚居地：" + occupant.ToJsonString());
            occupant["relation"] = (int)SettlementRelation.PlayerOwned;
            claimedNode = true;
        }
        if (!claimedNode) throw new InvalidOperationException("存档里没有节点 14。");
        foreach (var instance in world["instances"]!.AsArray())
        {
            var id = instance!["id"]!;
            var value = id["id"]!["value"]!.GetValue<ulong>();
            var kind = id["$kind"]!.GetValue<string>();
            if (kind == "building" && buildings.Contains(value) || kind == "vehicle" && parked.Contains(value))
                instance["ownership"] = (int)SettlementRelation.PlayerOwned;
        }
        var inventoryId = settlement["inventory"]!["value"]!.GetValue<ulong>();
        var inventory = world["inventories"]!.AsArray().Single(item => item!["id"]!["value"]!.GetValue<ulong>() == inventoryId)!;
        var goods = inventory["goods"]!.AsArray();
        var key = "voxel:" + railGood;
        var insertAt = goods.Count;
        JsonNode? existing = null;
        for (var i = 0; i < goods.Count; i++)
        {
            var itemKey = GoodKey(goods[i]!["good"]!);
            if (itemKey == key) { existing = goods[i]; break; }
            if (string.CompareOrdinal(itemKey, key) > 0) { insertAt = i; break; }
        }
        if (existing is not null) existing["quantity"] = existing["quantity"]!.GetValue<long>() + 18;
        else
        {
            goods.Insert(insertAt, new JsonObject
            {
                ["good"] = new JsonObject { ["$kind"] = "voxel", ["definition"] = new JsonObject { ["value"] = railGood } },
                ["quantity"] = 18
            });
            AddRailReference(root, inventoryId, key, railGood);
        }
        return root.ToJsonString();
    }

    private static void AddRailReference(JsonNode root, ulong inventoryId, string key, string railGood)
    {
        var definitions = root["references"]!["definitions"]!.AsArray();
        var field = $"World.Inventories[{inventoryId}].Goods[{key}]";
        var wasMissing = false;
        var insertAt = definitions.Count;
        var placed = false;
        for (var i = 0; i < definitions.Count; i++)
        {
            var item = definitions[i]!;
            var itemKey = DefinitionSortKey(item["definition"]!);
            if (itemKey == key) wasMissing = item["was_missing"]!.GetValue<bool>();
            if (placed) continue;
            var itemField = item["field"]!.GetValue<string>();
            var comparison = string.CompareOrdinal(itemKey, key);
            if (comparison == 0 && itemField == field) return;
            if (comparison > 0 || comparison == 0 && string.CompareOrdinal(itemField, field) > 0)
            {
                insertAt = i;
                placed = true;
            }
        }
        definitions.Insert(insertAt, new JsonObject
        {
            ["definition"] = new JsonObject { ["$kind"] = "voxel", ["id"] = new JsonObject { ["value"] = railGood } },
            ["frozen_revision"] = null,
            ["field"] = field,
            ["was_missing"] = wasMissing
        });
    }

    private static string DefinitionSortKey(JsonNode definition)
    {
        var kind = definition["$kind"]!.GetValue<string>();
        if (kind == "process")
        {
            var id = definition["id"]!;
            return id["$kind"]!.GetValue<string>() + ":" + id["definition"]!["value"]!.GetValue<string>();
        }
        var prefix = kind switch
        {
            "voxel" => "voxel",
            "composite" => "composite",
            "rule" => "rule",
            "settlement_type" => "settlement",
            "template" => "template",
            "terrain" => "terrain",
            "creature" => "creature",
            "lair" => "lair",
            _ => throw new InvalidOperationException("未知引用种类：" + kind)
        };
        return prefix + ":" + definition["id"]!["value"]!.GetValue<string>();
    }

    private static string GoodKey(JsonNode good)
    {
        var kind = good["$kind"]!.GetValue<string>();
        var id = kind == "assembly" ? good["composition"]!["value"]!.GetValue<string>() : good["definition"]!["value"]!.GetValue<string>();
        return kind + ":" + id;
    }

    private static bool Connects(EdgeMapItem edge, ulong left, ulong right) =>
        edge.Start.Value == left && edge.End.Value == right || edge.Start.Value == right && edge.End.Value == left;

    private static string DescribeTrain(ICoreReadView view, TrainId id)
    {
        if (view.FindTrain(id) is not Lookup<TrainView>.Found train) return $"列车 {id.Value} 不存在。";
        var position = train.Value.Position switch
        {
            TrainPosition.Node node => $"节点 {node.Id.Value} 阶段 {node.Phase}",
            TrainPosition.Edge edge => $"线路 {edge.Id.Value} 轨 {edge.Track} 距离 {edge.DistanceMeters:0.##}",
            _ => "未知位置"
        };
        var blockers = train.Value.Blockers.IsDefaultOrEmpty ? "" : " 阻塞 " + string.Join("、", train.Value.Blockers.Select(item => item.Code));
        return $"列车 {id.Value} 活动 {train.Value.Activity}，{position}。{blockers}";
    }

    private static string DescribeEdges(ICoreReadView view, ulong node) =>
        string.Join("；", view.World.Edges.Where(edge => edge.Start.Value == node || edge.End.Value == node)
            .Select(edge => $"线路 {edge.Id.Value} {edge.Start.Value}—{edge.End.Value} {edge.Tracks} 轨"));

    private static string DescribeSettlements(ICoreReadView view) =>
        string.Join("；", view.World.Nodes.Select(node => node.Occupant).OfType<NodeOccupant.Settlement>()
            .Select(item => $"聚居地 {item.Id.Value} 关系 {item.Relation}"));

    private static Vector2 Midpoint(MacroMap map, ICoreReadView view, ulong edgeId)
    {
        var edge = view.World.Edges.Single(item => item.Id.Value == edgeId);
        var start = view.World.Nodes.Single(item => item.Id == edge.Start).Position;
        var end = view.World.Nodes.Single(item => item.Id == edge.End).Position;
        return (map.MapToScreen(start) + map.MapToScreen(end)) / 2;
    }

    private static (Vector2 Construction, Vector2 Break) Marks(MacroMap map, ICoreReadView view, ulong edgeId)
    {
        var edge = view.World.Edges.Single(item => item.Id.Value == edgeId);
        var start = map.MapToScreen(view.World.Nodes.Single(item => item.Id == edge.Start).Position);
        var end = map.MapToScreen(view.World.Nodes.Single(item => item.Id == edge.End).Position);
        var normal = (end - start).Normalized().Orthogonal();
        var midpoint = (start + end) / 2;
        return (midpoint - normal * 16, midpoint + normal * 16);
    }

    private static bool RegionHas(Image image, Control map, Vector2 local, Color expected, int radius)
    {
        for (var y = -radius; y <= radius; y++)
        for (var x = -radius; x <= radius; x++)
            if (Near(Pixel(image, map, local + new Vector2(x, y)), expected)) return true;
        return false;
    }

    private static string Describe(Image image, Control map, Vector2 local) => $" 实际 {Pixel(image, map, local)}，位置 {local}。";

    private static Color Pixel(Image image, Control map, Vector2 local)
    {
        var global = map.GetGlobalTransformWithCanvas() * local;
        var visible = map.GetViewport().GetVisibleRect().Size;
        var point = new Vector2(global.X * image.GetWidth() / visible.X, global.Y * image.GetHeight() / visible.Y);
        return image.GetPixel(Mathf.Clamp(Mathf.RoundToInt(point.X), 0, image.GetWidth() - 1),
            Mathf.Clamp(Mathf.RoundToInt(point.Y), 0, image.GetHeight() - 1));
    }

    private static bool Near(Color pixel, Color expected) =>
        Math.Abs(pixel.R - expected.R) < 0.08f && Math.Abs(pixel.G - expected.G) < 0.08f && Math.Abs(pixel.B - expected.B) < 0.08f;
}
