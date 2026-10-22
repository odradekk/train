using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.Inventory;

[TestClass]
public sealed class InventoryEncodingTests
{
    [TestMethod]
    public void Transfers_and_both_lease_requests_round_trip_with_all_good_identities()
    {
        GoodId[] goods = [new GoodId.Voxel(new("fixture:ore")), new GoodId.Composite(new("fixture:plate")), new GoodId.Assembly(new(new string('A', 64)))];
        foreach (var good in goods)
        {
            RoundTrip(new CoreCommand.LoadGoods(new(1), new(2), new(3), good, long.MaxValue));
            RoundTrip(new CoreCommand.UnloadGoods(new(1), new(2), new(3), good, 1));
        }
        RoundTrip(new CoreCommand.AcquireLease(new LeaseRequest.Rent(new(2), long.MaxValue, ulong.MaxValue), new(long.MaxValue)));
        RoundTrip(new CoreCommand.AcquireLease(new LeaseRequest.Renew(new(7), 1), new(0)));
    }

    [TestMethod]
    public void Transfer_has_a_fixed_canonical_vector()
    {
        Assert.AreEqual("{\"input_format_version\":2,\"kind\":\"inventory.load_goods\",\"train\":1,\"settlement\":2,\"local_inventory\":3,\"good\":{\"kind\":\"voxel\",\"definition\":\"fixture:ore\"},\"quantity\":4}",
            CoreCommandEncoding.Encode(new CoreCommand.LoadGoods(new(1), new(2), new(3), new GoodId.Voxel(new("fixture:ore")), 4)));
    }

    [TestMethod]
    public void Unload_other_good_kinds_and_both_lease_requests_have_fixed_canonical_vectors()
    {
        var assembly = new string('A', 64);
        (CoreCommand Command, string Payload)[] cases =
        [
            (new CoreCommand.UnloadGoods(new(1), new(2), new(3), new GoodId.Composite(new("fixture:plate")), 5),
                "{\"input_format_version\":2,\"kind\":\"inventory.unload_goods\",\"train\":1,\"settlement\":2,\"local_inventory\":3,\"good\":{\"kind\":\"composite\",\"definition\":\"fixture:plate\"},\"quantity\":5}"),
            (new CoreCommand.LoadGoods(new(1), new(2), new(3), new GoodId.Assembly(new(assembly)), 6),
                "{\"input_format_version\":2,\"kind\":\"inventory.load_goods\",\"train\":1,\"settlement\":2,\"local_inventory\":3,\"good\":{\"kind\":\"assembly\",\"composition\":\"" + assembly + "\"},\"quantity\":6}"),
            (new CoreCommand.AcquireLease(new LeaseRequest.Rent(new(2), 3, 5184000), new(24)),
                "{\"input_format_version\":2,\"kind\":\"lease.acquire\",\"request\":{\"kind\":\"rent\",\"settlement\":2,\"capacity\":3,\"duration_steps\":5184000},\"maximum_payment\":24}"),
            (new CoreCommand.AcquireLease(new LeaseRequest.Renew(new(7), 1), new(0)),
                "{\"input_format_version\":2,\"kind\":\"lease.acquire\",\"request\":{\"kind\":\"renew\",\"lease\":7,\"duration_steps\":1},\"maximum_payment\":0}")
        ];
        foreach (var (command, payload) in cases)
        {
            Assert.AreEqual(payload, CoreCommandEncoding.Encode(command));
            Assert.AreEqual(payload, CoreCommandEncoding.Encode(((CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(payload)).Value.Command));
        }
    }

    [TestMethod]
    public void Invalid_command_prerequisites_are_rejected_before_encoding()
    {
        CoreCommand[] commands = [
            new CoreCommand.LoadGoods(new(0), new(2), new(3), new GoodId.Voxel(new("fixture:ore")), 1),
            new CoreCommand.UnloadGoods(new(1), new(2), new(3), new GoodId.Voxel(new("fixture:ore")), 0),
            new CoreCommand.AcquireLease(new LeaseRequest.Rent(new(2), 0, 1), new(1)),
            new CoreCommand.AcquireLease(new LeaseRequest.Renew(new(7), 0), new(1)),
            new CoreCommand.AcquireLease(new LeaseRequest.Rent(new(2), 1, 1), new(-1))];
        foreach (var command in commands)
            Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(command));
    }

    [TestMethod]
    public void Lease_predecode_rejects_negative_payment_limit()
    {
        var payload = CoreCommandEncoding.Encode(new CoreCommand.AcquireLease(new LeaseRequest.Renew(new(7), 1), new(0)));
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(
            payload.Replace("\"maximum_payment\":0", "\"maximum_payment\":-1", StringComparison.Ordinal));
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual("/maximum_payment", failure.Problems.Single().Field);
    }

    [TestMethod]
    [DataRow("{\"kind\":\"rent\",\"settlement\":2,\"capacity\":0,\"duration_steps\":1}", "/request/capacity")]
    [DataRow("{\"kind\":\"renew\",\"lease\":0,\"duration_steps\":1}", "/request/lease")]
    [DataRow("{\"kind\":\"renew\",\"lease\":1,\"duration_steps\":0}", "/request/duration_steps")]
    [DataRow("{\"kind\":\"renew\",\"lease\":1,\"duration_steps\":1,\"capacity\":3}", "/request/capacity")]
    [DataRow("{\"kind\":\"renew\",\"lease\":1,\"lease\":2,\"duration_steps\":1}", "/request/lease")]
    public void Lease_predecode_rejects_invalid_and_open_variant_fields(string request, string field)
    {
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(
            "{\"input_format_version\":2,\"kind\":\"lease.acquire\",\"request\":" + request + ",\"maximum_payment\":1}");
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    [TestMethod]
    [DataRow("\"quantity\":4", "\"quantity\":0", "/quantity")]
    [DataRow("\"train\":1", "\"train\":0", "/train")]
    [DataRow("\"definition\":\"fixture:ore\"", "\"definition\":\"bad id\"", "/good/definition")]
    [DataRow("\"kind\":\"voxel\"", "\"kind\":\"unknown\"", "/good/kind")]
    [DataRow("\"definition\":\"fixture:ore\"", "\"definition\":\"fixture:ore\",\"composition\":\"x\"", "/good/composition")]
    public void Transfer_predecode_rejects_invalid_ids_quantities_and_good_variants(string from, string to, string field)
    {
        var payload = CoreCommandEncoding.Encode(new CoreCommand.LoadGoods(new(1), new(2), new(3), new GoodId.Voxel(new("fixture:ore")), 4));
        var failure = (CoreResult<DecodedCommand>.Failure)CoreCommandEncoding.Decode(payload.Replace(from, to, StringComparison.Ordinal));
        Assert.AreEqual("invalid_state", failure.Problems.Single().Code);
        Assert.AreEqual(field, failure.Problems.Single().Field);
    }

    [TestMethod]
    public void Invalid_trade_values_cannot_be_encoded()
    {
        var good = new GoodId.Voxel(new("fixture:ore"));
        CoreCommand[] commands = [
            new CoreCommand.BuyGoods(new(0), new(2), new(3), good, 1, new(0)),
            new CoreCommand.BuyGoods(new(1), new(0), new(3), good, 1, new(0)),
            new CoreCommand.SellGoods(new(1), new(2), new(0), good, 1, new(0)),
            new CoreCommand.BuyGoods(new(1), new(2), new(3), good, 0, new(0)),
            new CoreCommand.SellGoods(new(1), new(2), new(3), good, -1, new(0)),
            new CoreCommand.BuyGoods(new(1), new(2), new(3), good, 1, new(-1)),
            new CoreCommand.SellGoods(new(1), new(2), new(3), good, 1, new(-1)),
            new CoreCommand.BuyVehicle(new(0), new(2), new(0)),
            new CoreCommand.SellVehicle(new(1), new(0), new(0)),
            new CoreCommand.BuyVehicle(new(1), new(2), new(-1)),
            new CoreCommand.SellVehicle(new(1), new(2), new(-1))];
        foreach (var command in commands)
            Assert.ThrowsExactly<ArgumentException>(() => CoreCommandEncoding.Encode(command));
    }

    private static void RoundTrip(CoreCommand command)
    {
        var payload = CoreCommandEncoding.Encode(command);
        var decoded = ((CoreResult<DecodedCommand>.Success)CoreCommandEncoding.Decode(payload)).Value.Command;
        Assert.AreEqual(command, decoded);
        Assert.AreEqual(payload, CoreCommandEncoding.Encode(decoded!));
    }
}
