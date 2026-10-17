using System.Numerics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Rendering;

[TestClass]
public sealed class ConstructionFogTests
{
    [TestMethod]
    public void FollowedTrainIncludesEveryCarriageAndItsNestedParts()
    {
        var objects = Objects();
        HashSet<VehicleId> vehicles = [new(101), new(102)];

        foreach (var id in new ulong[] { 1, 2, 3, 4, 5, 6 })
            Assert.IsTrue(SceneDisplay.BelongsToFollowedTrain(objects[new(id)].Source, objects, vehicles),
                $"Followed carriage or nested part {id} must remain visible.");
        foreach (var id in new ulong[] { 7, 8, 9 })
            Assert.IsFalse(SceneDisplay.BelongsToFollowedTrain(objects[new(id)].Source, objects, vehicles),
                $"Other train or terrain {id} must retain construction fog.");
    }

    [TestMethod]
    public void ChangingFollowedMembersExcludesPreviousCarriagesAndTheirParts()
    {
        var objects = Objects();
        HashSet<VehicleId> vehicles = [new(101), new(102)];
        Assert.IsTrue(SceneDisplay.BelongsToFollowedTrain(objects[new(3)].Source, objects, vehicles));

        vehicles = [new(103)];

        foreach (var id in new ulong[] { 1, 2, 3, 4, 5, 6, 9 })
            Assert.IsFalse(SceneDisplay.BelongsToFollowedTrain(objects[new(id)].Source, objects, vehicles));
        foreach (var id in new ulong[] { 7, 8 })
            Assert.IsTrue(SceneDisplay.BelongsToFollowedTrain(objects[new(id)].Source, objects, vehicles));
    }

    [TestMethod]
    public void NoFollowedVehiclesExcludesAllObjects()
    {
        var objects = Objects();
        HashSet<VehicleId> vehicles = [];

        foreach (var item in objects.Values)
            Assert.IsFalse(SceneDisplay.BelongsToFollowedTrain(item.Source, objects, vehicles));
    }

    private static Dictionary<SceneObjectId, SceneDisplay.DisplayObject> Objects()
    {
        SceneObject[] sources = [
            Object(1, vehicle: 101), Object(2, parent: 1), Object(3, parent: 2),
            Object(4, vehicle: 102), Object(5, parent: 4), Object(6, parent: 5),
            Object(7, vehicle: 103), Object(8, parent: 7), Object(9)
        ];
        return sources.ToDictionary(source => source.Id,
            source => new SceneDisplay.DisplayObject(source, null!, []));
    }

    private static SceneObject Object(ulong id, ulong? parent = null, ulong? vehicle = null) =>
        new(new(id), parent is { } parentId ? new SceneObjectId(parentId) : null,
            new(Vector3.Zero, Quaternion.Identity),
            vehicle is { } vehicleId ? new WorldObjectId.Instance(new InstanceId.Vehicle(new(vehicleId))) : null,
            [], [], []);
}
