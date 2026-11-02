using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainFormationWorkspaceTests
{
    [Test]
    public async Task Long_vehicle_lists_keep_the_footer_visible_below_the_shell_bar_at_supported_scales()
    {
        var window = TestScene.GetTree().Root;
        var size = window.Size; var canvas = window.ContentScaleSize; var scale = window.ContentScaleFactor;
        var view = LongWorld(1);
        const string longName = "青川长途货运机车与山区联合运输车辆";
        var complete = new DesignEvaluation.Complete(new("vehicle-design"), new("vehicle-composition"),
            new(new(longName, ""), new DesignSpecification.Vehicle(SizeClass.Medium), new([], [], [])), null,
            new(null, null, null, true, null, null, [], [], [], [], []), true, []);
        var content = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
        {
            Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
            { [new("vehicle-design")] = new ContentLookup<DesignEvaluation>.Found(complete) }
        };
        try
        {
            await WithWorkspace([], async (panel, binding, current, published) =>
            {
                Open(panel, binding, current, published);
                await new UiTestInput(TestScene).Activate(Find<CheckBox>(panel, "Vehicle1"));
                Require(Find<CheckBox>(panel, "Vehicle1").TooltipText.Contains(longName), "长名称没有完整提示。");
                foreach (var (dimensions, factor) in new[] {
                    (new Vector2I(1920, 1080), 1f), (new Vector2I(1280, 720), 1.25f),
                    (new Vector2I(1280, 720), 1.75f), (new Vector2I(800, 720), 2f), (new Vector2I(960, 1080), 2f) })
                {
                    window.ContentScaleSize = dimensions.X == 960 ? Vector2I.Zero : canvas;
                    window.Size = dimensions; window.ContentScaleFactor = factor;
                    await new UiTestInput(TestScene).Frames(6);
                    var ui = Find<GameUi>(window, "GameUi");
                    var bounds = Find<PanelContainer>(panel, "Formation").GetGlobalRect();
                    var bar = ui.GetNode<PanelContainer>("Root/World/Bar").GetGlobalRect();
                    var form = Find<Button>(panel, "Form").GetGlobalRect();
                    Require(bounds.Position.Y >= bar.End.Y && bounds.End.Y <= window.GetVisibleRect().End.Y &&
                        form.Position.Y >= bounds.Position.Y && form.End.Y <= bounds.End.Y,
                        $"编组工作区或主按钮越过可用区域：{dimensions} × {factor}，{bounds}，{form}。");
                    Require(Find<VBoxContainer>(panel, "AvailableVehicles").GetChildCount() == 50,
                        "缩放丢失候选车辆。");
                    if (dimensions.X == 1920) await UiCapture.Capture(TestScene, "formation-long-list");
                }
            }, view, shell: true, contentOverride: content);
        }
        finally { window.ContentScaleSize = canvas; window.Size = size; window.ContentScaleFactor = scale; }
    }

    [Test]
    public async Task Checkbox_click_survives_a_world_revision()
    {
        await WithWorkspace([], async (panel, binding, current, content) =>
        {
            Open(panel, binding, current, content);
            var check = Find<CheckBox>(panel, "Vehicle1");
            await new UiTestInput(TestScene).ClickAcrossRevision(check, () => panel.Refresh(binding, LongWorld(2), content));
            Require(check.ButtonPressed && Find<VBoxContainer>(panel, "OrderedVehicles").GetChildCount() == 1,
                "世界修订丢失了正在进行的勾选。");
        }, LongWorld(1));
    }

    [Test]
    public async Task Selected_vehicles_leaving_the_node_block_the_draft_until_removed()
    {
        await WithWorkspace([], async (panel, binding, current, content) =>
        {
            Open(panel, binding, current, content);
            var input = new UiTestInput(TestScene);
            await input.Activate(Find<CheckBox>(panel, "Vehicle1"));
            panel.Refresh(binding, LongWorld(2, moved: true), content);
            Require(Find<CheckBox>(panel, "Vehicle1").Disabled && Find<CheckBox>(panel, "Vehicle1").ButtonPressed &&
                Find<Button>(panel, "Form").Disabled && Find<Button>(panel, "FormOnly").Disabled &&
                Find<Label>(panel, "Reason").Text.Contains("不可用"), "离开当前地点的车辆仍可提交，或失效草稿没有保留。");
            await input.Activate(Find<Button>(Find<VBoxContainer>(panel, "OrderedVehicles").GetChild(0), "Remove"));
            Require(Find<VBoxContainer>(panel, "OrderedVehicles").GetChildCount() == 0,
                "失效车辆不能从草稿移出。");
            panel.Refresh(binding, LongWorld(3), content);
            await input.Activate(Find<CheckBox>(panel, "Vehicle1"));
            Require(!Find<Button>(panel, "Form").Disabled, "重新选择当地车辆后仍被失效草稿阻止。");
        }, LongWorld(1));
    }

    private static FixedCoreReadView LongWorld(ulong revision, bool moved = false)
    {
        var basis = World();
        var first = ((Lookup<InstanceView>.Found)basis.FindInstance(new InstanceId.Vehicle(new(1)))).Value;
        var ids = Enumerable.Range(1, 50).Select(id => new InstanceId.Vehicle(new((ulong)id))).ToArray();
        var location = new WorldLocation.Node(new(moved ? 2UL : 1UL));
        return new(basis.Position, basis.InputIdentity, revision: new(revision))
        {
            World = basis.World, Economy = basis.Economy,
            Nodes = basis.World.Nodes.ToDictionary(node => node.Id, node => basis.FindNode(node.Id)),
            Instances = ids.ToDictionary(id => (InstanceId)id, id => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(first with { Id = id, Location = location })),
            Locations = basis.World.Nodes.ToDictionary(node => (WorldLocation)new WorldLocation.Node(node.Id), node =>
                (Lookup<LocationView>)new Lookup<LocationView>.Found(new(new WorldLocation.Node(node.Id), [],
                    node.Id == location.Id ? ids.Cast<InstanceId>().ToImmutableArray() : [], [], [], node.Occupant))),
            FormationPreviews = [(null, [], new CoreResult<TrainFormationPreview>.Failure([new("composition_empty", "OrderedVehicles", null, [])])),
                (null, [new(1)], new CoreResult<TrainFormationPreview>.Success(new(12, 34, 56, 78, [], null)))]
        };
    }
}
