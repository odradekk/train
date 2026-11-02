using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Capacity_blockers_and_stale_bindings_disable_submission_across_inspector_switches()
    {
        var view = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.Owned, forSale: 0, purchaseAvailable: 0, blocked: true);
        var content = EconomyFixture.Content();
        var setup = new HostSimulationTests.HostSetup(view, content);
        await Run(setup, async (ui, economy) =>
        {
            await Page(economy, 2);
            Require(Find<Label>(economy, "SoldOut").Text.Contains("售罄") &&
                Find<Label>(economy, "PurchaseCapacity").Text.Contains("收购名额：0"), "售罄或收购名额未展示。");
            await _input.Activate(Find<Button>(economy, "SelectSellVehicle"));
            await _input.Activate(Find<Button>(economy, "QuoteVehicle"));
            Require(Find<Button>(economy, "ConfirmVehicle").Disabled &&
                Find<Label>(economy, "VehicleQuote").Text.Contains("可用容量不足") &&
                !Find<Label>(economy, "VehicleQuote").Text.Contains("insufficient_capacity"), "收购受阻时不得允许卖车。");
            await Market(economy);
            Require(Find<Button>(economy, "ConfirmTrade").Disabled &&
                Find<Label>(economy, "Quote").Text.Contains("资金不足") &&
                Find<Label>(economy, "Quote").Text.Contains("可用容量不足") &&
                !Find<Label>(economy, "Quote").Text.Contains("insufficient_"), "受阻报价未禁止提交或遗漏原因。");
            await _input.Activate(Find<Button>(ui, "Fleet"));
            await _input.ClickItem(Find<Tree>(Find<PanelContainer>(ui, "FleetList"), "Objects"), 0);
            var inventory = Find<PanelContainer>(ui, "Details");
            await _input.ChooseTab(Find<TabContainer>(inventory, "Pages"), 2);
            Require(!economy.IsVisibleInTree() && Find<Label>(inventory, "Capacity").IsVisibleInTree(),
                "列车货物页没有替换聚居地检查器。");
            Find<WorldPanels>(ui, "WorldPanels").Refresh(new HostBinding(Guid.NewGuid()), view, content);
            Require(Find<Label>(inventory, "Description").Text.Contains("已不存在") &&
                Find<Button>(inventory, "EditPlan").Disabled, "列车检查器仍接受旧绑定。");
            await _input.Activate(Find<Button>(ui, "Nodes"));
            await _input.ClickItem(NodeObjects(ui), FirstSelectableItem(NodeObjects(ui)));
            economy = Find<PanelContainer>(ui, "Settlement");
            await Market(economy);
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(new HostBinding(Guid.NewGuid()), view, content);
            Require(Find<Button>(economy, "ConfirmTrade").Disabled && !Find<LineEdit>(economy, "Quantity").Editable,
                "旧绑定窗口仍允许操作。");
            var host = ui.GetParent<Train.App.App>().Host!;
            var binding = host.GetStatus()!.Binding;
            setup.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(
                new FixedCoreCapture(new(view.Position, "fixed", "fixed"), HostCoreScenario.References, "opaque", view.InputIdentity, view.Revision)));
            var reloaded = EconomyFixture.Content(2);
            using var reload = new ScriptedContentCandidate(reloaded, host.Content!, ContentChange.Presentation);
            setup.ContentFactory!.PrepareLoadCall = (request, _, _) => request.Purpose is ContentLoadPurpose.Reload
                ? new ContentResult<IContentCandidate>.Success(reload)
                : throw new InvalidOperationException("UNCONFIGURED: " + request.Purpose);
            Require(host.BeginReload(CommandSource.UserInterface).Problem is null, "内容重载被拒绝。");
            await _input.WaitUntil(() => !host.IsBusy && ReferenceEquals(host.Content, reloaded));
            Find<EconomyPanels>(ui, "EconomyPanels").Refresh(binding, view, host.Content!);
            Require(Find<Button>(economy, "ConfirmTrade").Disabled,
                "内容重载后旧窗口仍允许操作。");
            Require(setup.Session.SubmitCount == 0, "无效窗口提交了命令。");
            var app = ui.GetParent<Train.App.App>();
            Require(app.Saves!.ReturnToMenu() is null, "无法返回主菜单。");
            if (app.Saves.Leaving is not null) app.Saves.ChooseLeave(Train.App.Saves.LeaveChoice.Discard);
            await _input.WaitUntil(() => !GodotObject.IsInstanceValid(economy));
            Require(!GodotObject.IsInstanceValid(inventory), "返回菜单后列车检查器没有释放。");
            Require(app.Host!.GetStatus() is null, "关闭经济窗口时旧对局仍活动。");
        });
        setup.Session.AssertComplete();
    }

    [Test]
    public async Task Node_and_train_inspectors_refresh_vehicle_actions_in_place()
    {
        var basis = EconomyFixture.Create(vehicle: EconomyFixture.VehicleState.ForSale);
        var node = basis.World.Nodes[0] with { Occupant = new NodeOccupant.Empty() };
        var parked = new FixedCoreReadView(basis.Position, basis.InputIdentity, revision: basis.Revision)
        {
            World = basis.World with { Nodes = [node] }, Economy = basis.Economy,
            Nodes = new Dictionary<NodeId, Lookup<NodeView>>
                { [node.Id] = new Lookup<NodeView>.Found(new(node, [], 80, 0, 80)) },
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
                { [new WorldLocation.Node(node.Id)] = new Lookup<LocationView>.Found(new(new WorldLocation.Node(node.Id),
                    [new(7)], [new InstanceId.Vehicle(new(9)), new InstanceId.Vehicle(new(10))], [], [], node.Occupant)) },
            Instances = new VehicleId[] { new(9), new(10) }.ToDictionary(id => (InstanceId)new InstanceId.Vehicle(id),
                id => basis.FindInstance(new InstanceId.Vehicle(id))),
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(7)] = basis.FindTrain(new(7)) }
        };
        var owned = EconomyFixture.Create(revision: 2, vehicle: EconomyFixture.VehicleState.Owned);
        var onEdge = EconomyFixture.Create(revision: 3, trainAtNode: false);
        var setup = new HostSimulationTests.HostSetup(parked, EconomyFixture.Content());
        await Boot(setup, async ui =>
        {
            var panels = ui.GetNode<WorldPanels>("Root/World/WorldPanels");
            var host = ui.GetParent<Train.App.App>().Host!;
            panels.OpenObject(new WorldObjectId.Node(new(1)));
            await _input.Frames();
            var shell = Find<GameShell>(ui, "*");
            var detail = shell.InspectorContent!;
            var nodeText = Find<Label>(detail, "Description").Text;
            Require(nodeText.Contains("占据者：") && !nodeText.Contains('\r'), nodeText);
            Require(Named(detail, "RefitParkedVehicle").Any(button => button.Text.Contains("车辆 9")) &&
                !Named(detail, "ParkedVehicleServices").Any(button => button.Text.Contains("车辆 9")),
                "非自有停放车辆出现了服务按钮。");
            panels.Refresh(host.GetStatus()!.Binding, owned, host.Content!);
            Require(detail.Visible && Named(detail, "ParkedVehicleServices").Any(button => button.Text == "车辆 9 服务…") &&
                Named(detail, "RefitParkedVehicle").Any(button => button.Text.Contains("车辆 9")),
                "停放车辆改为自有后没有补上服务按钮，或关闭了详情。");
            panels.OpenObject(new WorldObjectId.Train(new(7)));
            await _input.Frames();
            var train = shell.InspectorContent!;
            Require(!GodotObject.IsInstanceValid(detail) && shell.SelectedObject == new WorldObjectId.Train(new(7)),
                "列车检查器没有替换节点详情。");
            var trainText = Find<Label>(train, "Description").Text;
            Require(trainText.Contains("状态：") && !trainText.Contains('\r'), trainText);
            await _input.ChooseTab(Find<TabContainer>(train, "Pages"), 3);
            Require(Named(train, "RefitVehicle").Any(button => button.Text == "改装…") &&
                Named(train, "VehicleServices").Any(button => button.Text == "车辆服务…"),
                "停在节点的列车没有车辆改装和服务按钮。");
            panels.Refresh(host.GetStatus()!.Binding, onEdge, host.Content!);
            Require(train.Visible && Find<TabContainer>(train, "Pages").CurrentTab == 3 &&
                Named(train, "RefitVehicle").Any(button => button.Text == "改装…") &&
                !Named(train, "VehicleServices").Any(button => button.Visible),
                "列车离开节点后仍保留服务按钮，或关闭了详情。");
        });
        setup.Session.AssertComplete();
    }
}
