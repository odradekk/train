using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EconomyUiTests
{
    [Test]
    public async Task Newly_rented_warehouse_is_selected_without_losing_actual_payment()
    {
        LeaseRequest rent = new LeaseRequest.Rent(new(1), 20, EconomyFixture.LeaseDuration * 2);
        Dictionary<LeaseRequest, CoreResult<LeaseQuote>> Quotes(ulong revision)
        {
            var quotes = new Dictionary<LeaseRequest, CoreResult<LeaseQuote>>
            {
                [rent] = new CoreResult<LeaseQuote>.Success(new(rent, new(revision), new(827), 20, []))
            };
            LeaseRequest partialInput = new LeaseRequest.Rent(new(1), 2, EconomyFixture.LeaseDuration * 2);
            quotes.Add(partialInput, new CoreResult<LeaseQuote>.Success(new(partialInput, new(revision), new(83), 20, [])));
            foreach (var id in new LeaseId[] { new(1), new(2) })
            foreach (var multiple in new ulong[] { 1, 2, 3, 4 })
            {
                LeaseRequest renew = new LeaseRequest.Renew(id, EconomyFixture.LeaseDuration * multiple);
                var blocked = id.Value + multiple > 4;
                quotes.Add(renew, new CoreResult<LeaseQuote>.Success(new(renew, new(revision),
                    blocked ? null : new Money(id.Value == 2 ? 953 : 419), 20,
                    blocked ? [new("invalid_state", "Request.DurationSteps", null, [])] : [])));
            }
            return quotes;
        }
        var before = EconomyFixture.Create(leaseQuotes: Quotes(1));
        var basis = EconomyFixture.Create(2, leaseCount: 2, leaseQuotes: Quotes(2));
        var empty = ((Lookup<InventoryView>.Found)basis.FindInventory(new(14))).Value with
        { Capacity = 20, Used = 0, Reserved = 0, Available = 20, GoodsMassTons = 0, Stacks = [], Reservations = [] };
        var inventories = new InventoryId[] { new(10), new(11), new(12), new(13), new(14) }
            .ToDictionary(id => id, basis.FindInventory);
        inventories[new(14)] = new Lookup<InventoryView>.Found(empty);
        var rented = new FixedCoreReadView(basis.Position, basis.InputIdentity, revision: basis.Revision)
        {
            World = basis.World, Economy = basis.Economy,
            Nodes = new NodeId[] { new(1), new(2) }.ToDictionary(id => id, basis.FindNode),
            Locations = new WorldLocation[] { new WorldLocation.Node(new(1)), new WorldLocation.Node(new(2)) }
                .ToDictionary(id => id, basis.FindLocation),
            Trains = new TrainId[] { new(7), new(8), new(9) }.ToDictionary(id => id, basis.FindTrain),
            Instances = new InstanceId[] { new InstanceId.Vehicle(new(9)), new InstanceId.Vehicle(new(10)), new InstanceId.Vehicle(new(11)) }
                .ToDictionary(id => id, basis.FindInstance),
            Settlements = new SettlementId[] { new(1), new(2) }.ToDictionary(id => id, basis.FindSettlement),
            Inventories = inventories,
            Leases = new LeaseId[] { new(1), new(2) }.ToDictionary(id => id, basis.FindLease),
            LeaseQuotes = Quotes(2)
        };
        var acquired = ((Lookup<LeaseView>.Found)rented.FindLease(new(2))).Value;
        var setup = new HostSimulationTests.HostSetup(before, EconomyFixture.Content(mapPresentation: true, maxLeaseMultiple: 4));
        setup.Session.QueueSubmit(new CoreCommand.AcquireLease(rent, new(827)), CommandSource.UserInterface,
            new SubmissionResult.Completed(new(1, before.Position, rented.Position, new CommandDecision.Accepted([], [])),
                [new(rented.Position, 0, new CoreEvent.LeaseAcquired(acquired, new(811), false))], null), rented);
        await Run(setup, async (ui, panel) =>
        {
            await Page(panel, 3);
            await _input.Choose(Find<OptionButton>(panel, "Lease"), 0);
            await _input.Choose(Find<OptionButton>(panel, "LeaseMultiple"), 1);
            await _input.Enter(Find<LineEdit>(panel, "LeaseCapacity"), "20");
            await _input.Activate(Find<Button>(panel, "ConfirmLease"));
            await _input.WaitUntil(() => ReferenceEquals(ui.View, rented));
            await _input.Frames();
            var details = Find<Label>(panel, "LeaseDetails");
            var confirm = Find<Button>(panel, "ConfirmLease");
            var result = Find<Label>(panel, "LeaseResult").Text;
            Require(details.IsVisibleInTree() && details.Text.Contains("0/20") && details.Text.Contains("第3天 00:00") &&
                confirm.Text.Contains("续租") && confirm.Text.Contains("953") &&
                result.Contains("811") && !result.Contains("827"),
                $"新租后应显示事件指定的新仓库及续租报价，并保留实付：可见={details.IsVisibleInTree()}，详情={details.Text}，按钮={confirm.Text}，结果={result}。");
        });
        setup.Session.AssertComplete();
    }
}
