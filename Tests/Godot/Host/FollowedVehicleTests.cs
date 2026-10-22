using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Host;

[TestClass]
public sealed class FollowedVehicleTests
{
    private static VehicleId Center(params int?[] lengths) =>
        FollowedVehicle.AtCenter([.. lengths.Select((length, index) => (new VehicleId((ulong)index + 1), length))]);

    [TestMethod]
    public void Single_vehicle_is_followed() => Assert.AreEqual(new VehicleId(1), Center(136));

    [TestMethod]
    public void Equal_odd_consist_follows_the_middle_vehicle() => Assert.AreEqual(new VehicleId(3), Center(80, 80, 80, 80, 80));

    [TestMethod]
    public void Center_on_a_joint_follows_the_vehicle_nearer_the_first_end() => Assert.AreEqual(new VehicleId(2), Center(80, 80, 80, 80));

    [TestMethod]
    [DataRow(new[] { 80, 16 }, 1UL, DisplayName = "大型机车加小货车")]
    [DataRow(new[] { 16, 16, 80 }, 3UL)]
    [DataRow(new[] { 16, 40, 16, 16 }, 2UL)]
    public void Unequal_consist_follows_the_vehicle_spanning_half_the_length(int[] lengths, ulong expected) =>
        Assert.AreEqual(new VehicleId(expected), Center([.. lengths.Select(length => (int?)length)]));

    [TestMethod]
    public void Unknown_length_falls_back_to_the_first_vehicle() => Assert.AreEqual(new VehicleId(1), Center(16, null, 80));
}
