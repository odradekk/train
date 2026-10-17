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
    public void ChangingFollowIncludesOnlyCurrentTrainCarriagesAndNestedParts()
    {
        var objects = Objects();
        (VehicleId[] Vehicles, ulong[] Objects)[] cases = [
            ([new(101), new(102)], [1, 2, 3, 4, 5, 6]),
            ([new(103)], [7, 8]),
            ([], [])
        ];
        foreach (var (members, expected) in cases)
        {
            var vehicles = members.ToHashSet();
            foreach (var (id, item) in objects)
                Assert.AreEqual(expected.Contains(id.Value), SceneDisplay.BelongsToFollowedTrain(item.Source, objects, vehicles),
                    $"Only current followed carriages and nested parts may be exempt: object {id.Value}.");
        }
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
