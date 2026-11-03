using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Train.App;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class UiTests
{
    [Test]
    public async Task Major_settlement_outer_marker_accepts_input_while_ordinary_settlement_does_not()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        using var output = new GodotOutputCapture();
        using var dispatcher = new GodotMainThreadDispatcher();
        var factory = Train.Content.ContentFactory.Create();
        var loaded = factory.PrepareLoad(new(new(new("builtin"), new("mods"), null, "0.0.56", []),
            new ContentLoadPurpose.Initial()), new MajorSettlementFiles(), new GodotAssetInspector(dispatcher));
        Require(loaded is ContentResult<IContentCandidate>.Success, $"聚居地测试内容加载失败：{loaded}。");
        using var candidate = ((ContentResult<IContentCandidate>.Success)loaded).Value;
        var templates = new[] { "core:agricultural-town", "core:ordinary-town" };
        var content = factory.WithSettlementCopies(candidate.View, templates.Select((id, index) =>
        {
            var template = candidate.View.Core.SettlementTemplates.Single(item => item.Id.Value == id);
            return new OwnedSettlementTemplateReference(new((ulong)index + 1), template.Id,
                template.Plots.Select(plot => new TemplatePlotReference(plot.Number, plot.Size)).ToImmutableArray());
        }).ToImmutableArray());
        var original = UiWorldFixture.Map();
        var economy = EconomyFixture.Create();
        var nodes = original.World.Nodes.Select(node => node with
        { Occupant = new NodeOccupant.Settlement(new(node.Id.Value), SettlementRelation.Hostile) }).ToImmutableArray();
        var view = new FixedCoreReadView(default, HostCoreScenario.Input)
        {
            Economy = UiWorldFixture.Economy,
            World = original.World with { Nodes = nodes, Trains = [] },
            Settlements = nodes.ToDictionary(node => new SettlementId(node.Id.Value), node =>
                (Lookup<SettlementView>)new Lookup<SettlementView>.Found(
                    ((Lookup<SettlementView>.Found)economy.FindSettlement(new(1))).Value with
                    { Id = new(node.Id.Value), Node = node.Id, Relation = SettlementRelation.Hostile,
                        ParkedVehicles = [], VehiclesForSale = [], Leases = [] })),
            Inventories = new Dictionary<InventoryId, Lookup<InventoryView>>
            { [new(10)] = economy.FindInventory(new(10)) },
            Nodes = nodes.ToDictionary(node => node.Id, node => (Lookup<NodeView>)new Lookup<NodeView>.Found(new(node, [], 80, 0, 80))),
            Locations = nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id), node =>
                (Lookup<LocationView>)new Lookup<LocationView>.Found(new(new WorldLocation.Node(node.Id), [], [], [], [], node.Occupant)))
        };
        var scenes = new FixedDisplayFactory { OpenWorldResults = [new SceneResult<IWorldSceneSession>.Failure(
            [new("fixture_scene_unavailable", SceneStage.Build, null, null, null, null, "地图测试不构建世界场景。")])] };
        OS.AddLogger(output);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new HostSimulationTests.HostSetup(view, content).Create(logger);
            app.CreateScenes = () => scenes;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            Require(ui.ErrorText.Length == 0, $"聚居地地图显示失败：{ui.ErrorText}。");
            await UiCapture.Capture(TestScene, "major-settlements");
            WorldObjectId? clicked = null;
            map.ObjectClicked += value => clicked = value;
            // 先点击普通聚居地，避免主要聚居地命中后打开的详情面板遮挡后续输入。
            var ordinaryPoint = map.GlobalPosition + map.MapToScreen(nodes[1].Position) + new Vector2(18, 0);
            Require(map.GetGlobalRect().HasPoint(ordinaryPoint), "普通聚居地测试点击位于地图范围外。");
            var screenPoint = TestScene.GetViewport().GetScreenTransform() * ordinaryPoint;
            using (var motion = new InputEventMouseMotion { Position = screenPoint, GlobalPosition = screenPoint })
                Input.ParseInputEvent(motion);
            await _input.Frames();
            Require(tree.Root.GuiGetHoveredControl() == map, "普通聚居地测试点击被其他界面遮挡。");
            await _input.Click(ordinaryPoint);
            Require(clicked is null, "普通聚居地在圆心外 18 像素处被实际左键选中。");
            await _input.Click(map.GlobalPosition + map.MapToScreen(nodes[0].Position) + new Vector2(18, 0));
            Require(clicked == new WorldObjectId.Node(nodes[0].Id), "主要聚居地在圆心外 18 像素处未被实际左键选中。");
            Require(scenes.OpenCalls.Count == 0, "地图单击选中聚居地时不应打开场景。");
            var majorPoint = map.GlobalPosition + map.MapToScreen(nodes[0].Position) + new Vector2(18, 0);
            await _input.MouseButton(majorPoint, MouseButton.Left, true, doubleClick: true);
            await _input.MouseButton(majorPoint, MouseButton.Left, false);
            Require(scenes.OpenCalls.Count == 1, "地图双击没有发出唯一的场景打开请求。");
            Require(!output.Contains("scene_adapter_failed"), "地图点击打开场景记录了适配异常。");
        }
        finally { OS.RemoveLogger(output); await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

    private sealed class MajorSettlementFiles : IContentFiles
    {
        private readonly DiskContentFiles _disk = new(ProjectSettings.GlobalizePath("res://builtin/core"));

        public ContentIoResult<ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory)
        {
            if (directory.Root.Key == "mods") return new ContentIoResult<ImmutableArray<ContentFileEntry>>.Success([]);
            var result = _disk.Enumerate(directory);
            var extra = directory.RelativePath switch
            {
                "resources/settlement-types" => "ordinary-town.settlement-type.json",
                "resources/settlement-templates" => "ordinary-town.settlement-template.json",
                _ => null
            };
            return extra is not null && result is ContentIoResult<ImmutableArray<ContentFileEntry>>.Success entries
                ? new ContentIoResult<ImmutableArray<ContentFileEntry>>.Success(entries.Value.Add(new(extra, ContentEntryKind.File))) : result;
        }

        public ContentIoResult<ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
        {
            var ordinary = file.RelativePath.Contains("ordinary-town", StringComparison.Ordinal);
            var source = ordinary ? file with { RelativePath = file.RelativePath.Replace("ordinary-town", "agricultural-town", StringComparison.Ordinal) } : file;
            var result = _disk.Read(source, maximumBytes);
            if (result is not ContentIoResult<ImmutableArray<byte>>.Success bytes ||
                source.RelativePath is not ("resources/settlement-types/agricultural-town.settlement-type.json" or
                    "resources/settlement-templates/agricultural-town.settlement-template.json")) return result;
            var json = JsonNode.Parse(bytes.Value.AsSpan())!;
            if (ordinary) json["id"] = "ordinary-town";
            if (source.RelativePath.EndsWith(".settlement-type.json", StringComparison.Ordinal)) json["display"]!["major"] = !ordinary;
            else
            {
                if (ordinary) json["settlement_type"] = "ordinary-town";
                json["display"]!["name"] = ordinary ? "普通农业镇 · major=false" : "主要农业镇 · major=true";
            }
            return new ContentIoResult<ImmutableArray<byte>>.Success(Encoding.UTF8.GetBytes(json.ToJsonString()).ToImmutableArray());
        }
    }

    [Test]
    public async Task Map_input_hits_objects_and_panels_block_camera_input()
    {
        var tree = TestScene.GetTree();
        var fixture = new Fixture(tree);
        var scale = tree.Root.ContentScaleFactor;
        using var output = new GodotOutputCapture();
        var view = UiWorldFixture.Map();
        var setup = new HostSimulationTests.HostSetup(view);
        var scenes = new FixedDisplayFactory { OpenWorldResults = [new SceneResult<IWorldSceneSession>.Failure(
            [new("fixture_scene_unavailable", SceneStage.Build, null, null, null, null, "地图测试不构建世界场景。")])] };
        OS.AddLogger(output);
        try
        {
            var app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => setup.Create(logger);
            app.CreateScenes = () => scenes;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Host!.GetStatus() is not null);
            tree.Root.ContentScaleFactor = 1;
            UiCapture.ConfigureWindowIfNeeded(tree.Root);
            await _input.Frames(2);
            var ui = app.GetNode<GameUi>("GameUi");
            var map = ui.GetNode<MacroMap>("Root/World/MacroMap");
            Require(ReferenceEquals(map.View, view) && ReferenceEquals(ui.View, view), "地图和宿主未读取同一固定快照。");
            var layerPanel = map.GetNode<PopupPanel>("InformationLayers");
            await _input.Activate(map.GetNode<Button>("MapTools/Buttons/Layers"));
            await _input.Activate(map.GetNode<CheckBox>("InformationLayers/Body/Resources"), layerPanel);
            await _input.Activate(map.GetNode<CheckBox>("InformationLayers/Body/Platforms"), layerPanel);
            await _input.PressKey(Key.Escape);
            WorldObjectId? clicked = null;
            map.ObjectClicked += value => clicked = value;
            var nodePoint = map.GlobalPosition + map.MapToScreen(view.World.Nodes[1].Position);
            await _input.Click(nodePoint);
            Require(clicked == new WorldObjectId.Node(view.World.Nodes[1].Id), "实际左键没有命中地图节点。");
            Require(scenes.OpenCalls.Count == 0, "地图单击选中节点时不应打开场景。");
            await _input.MouseButton(nodePoint, MouseButton.Left, true, doubleClick: true);
            await _input.MouseButton(nodePoint, MouseButton.Left, false);

            var free = map.GlobalPosition + map.Size * new Vector2(.75f, .8f);
            var before = map.Offset;
            await _input.Drag(free, new Vector2(37, -19));
            Require(map.Offset.DistanceTo(before + new Vector2(37, -19)) < .1f, "实际中键拖动没有平移地图。");
            var local = free - map.GlobalPosition;
            var anchor = (local - map.Offset) / map.Zoom;
            var zoom = map.Zoom;
            await _input.MouseButton(free, MouseButton.WheelUp, true);
            await _input.MouseButton(free, MouseButton.WheelUp, false);
            Require(map.Zoom > zoom && ((local - map.Offset) / map.Zoom).DistanceTo(anchor) < .01f,
                "滚轮没有以实际鼠标位置缩放。");
            await UiCapture.Capture(TestScene, "macro-map");
            await _input.Activate(map.GetNode<Button>("MapTools/Buttons/Layers"));
            await _input.Activate(map.GetNode<CheckBox>("InformationLayers/Body/Resources"), layerPanel);
            await _input.Activate(map.GetNode<CheckBox>("InformationLayers/Body/Platforms"), layerPanel);
            await _input.PressKey(Key.Escape);
            await UiCapture.Capture(TestScene, "macro-map-off");
            await _input.Activate(map.GetNode<Button>("MapTools/Buttons/Layers"));
            await _input.Activate(map.GetNode<CheckBox>("InformationLayers/Body/Resources"), layerPanel);
            await _input.Activate(map.GetNode<CheckBox>("InformationLayers/Body/Platforms"), layerPanel);

            var panelPoint = (Vector2)layerPanel.Position + new Vector2(8, 8);
            before = map.Offset; zoom = map.Zoom; clicked = null;
            await _input.Click(panelPoint);
            await _input.Drag(panelPoint, new Vector2(12, 8));
            await _input.MouseButton(panelPoint, MouseButton.WheelUp, true);
            await _input.MouseButton(panelPoint, MouseButton.WheelUp, false);
            Require(clicked is null && map.Offset == before && map.Zoom == zoom,
                "信息层面板的输入穿透到了地图。");
            await _input.PressKey(Key.Escape);

            var fleet = Find<Button>(ui, "Fleet");
            fleet.GrabFocus();
            await _input.PressKey(Key.Space);
            var list = Find<Tree>(Find<PanelContainer>(ui, "FleetList"), "Objects");
            Require(list.IsVisibleInTree(), "顶栏实际按键没有打开车队列表。");
            await _input.Frames(2);
            var covered = list.GlobalPosition + new Vector2(30, 30);
            var nodePosition = map.GlobalPosition + map.MapToScreen(view.World.Nodes[1].Position);
            await _input.Drag(free, covered - nodePosition);
            Require(map.HitTest(covered - map.GlobalPosition) == new WorldObjectId.Node(view.World.Nodes[1].Id),
                "遮挡情景下方没有预期地图节点。");
            clicked = null;
            await _input.Click(covered);
            Require(clicked is null, "上层列表的左键输入穿透并选中了地图对象。");
            before = map.Offset;
            zoom = map.Zoom;
            await _input.Drag(covered, new Vector2(20, 12));
            await _input.MouseButton(covered, MouseButton.WheelUp, true);
            await _input.MouseButton(covered, MouseButton.WheelUp, false);
            Require(map.Offset == before && map.Zoom == zoom, "上层列表的实际输入穿透到了地图。");
            Require(scenes.OpenCalls.Count == 1, "地图双击没有发出唯一的场景打开请求。");
            Require(!output.Contains("scene_adapter_failed"), "地图点击打开场景记录了适配异常。");
        }
        finally { OS.RemoveLogger(output); await fixture.Cleanup(); tree.Root.ContentScaleFactor = scale; }
    }

}
