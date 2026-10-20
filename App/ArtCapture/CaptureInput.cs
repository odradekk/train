using System.Globalization;
using Train.Core.Contracts;

namespace Train.App.ArtCapture;

internal sealed record CaptureInput(string SavePath, WorldLocation Location, WorldObjectId Target, string Name, ulong MotionSteps = 2)
{
    internal static CaptureInput Parse(IReadOnlyList<string> arguments)
    {
        const string usage = "用法：--capture-save <存档> --capture-location edge:<ID>|node:<ID> --capture-train <ID>|--capture-lair <ID>|--capture-group <ID>|--capture-building <ID>|--capture-vehicle <ID> --capture-name <名称> [--capture-motion-steps <正整数>]";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Count; i += 2)
        {
            if (i + 1 >= arguments.Count || arguments[i] is not
                ("--capture-save" or "--capture-location" or "--capture-train" or "--capture-lair" or "--capture-group" or "--capture-building" or "--capture-vehicle" or "--capture-name" or "--capture-motion-steps") ||
                !values.TryAdd(arguments[i], arguments[i + 1])) throw new ArgumentException(usage);
        }
        var targets = new[] { "--capture-train", "--capture-lair", "--capture-group", "--capture-building", "--capture-vehicle" }.Where(values.ContainsKey).ToArray();
        if (targets.Length != 1 || !values.ContainsKey("--capture-save") ||
            !values.ContainsKey("--capture-location") || !values.ContainsKey("--capture-name")) throw new ArgumentException(usage);
        var scene = values["--capture-location"].Split(':');
        if (scene.Length != 2 || scene[0] is not ("edge" or "node") ||
            !ulong.TryParse(scene[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0 ||
            !ulong.TryParse(values[targets[0]], NumberStyles.None, CultureInfo.InvariantCulture, out var target) || target == 0)
            throw new ArgumentException(usage);
        var motionSteps = 2UL;
        if (values.TryGetValue("--capture-motion-steps", out var steps) &&
            (!ulong.TryParse(steps, NumberStyles.None, CultureInfo.InvariantCulture, out motionSteps) || motionSteps == 0))
            throw new ArgumentException(usage);
        var name = values["--capture-name"];
        if (name.Length == 0 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("输出名称只接受英文字母、数字、连字符和下划线。");
        return new(Path.GetFullPath(values["--capture-save"]),
            scene[0] == "edge" ? new WorldLocation.Edge(new(id), 0) : new WorldLocation.Node(new(id)),
            targets[0] switch
            {
                "--capture-train" => new WorldObjectId.Train(new(target)),
                "--capture-lair" => new WorldObjectId.Lair(new(target)),
                "--capture-building" => new WorldObjectId.Instance(new InstanceId.Building(new(target))),
                "--capture-vehicle" => new WorldObjectId.Instance(new InstanceId.Vehicle(new(target))),
                _ => new WorldObjectId.CreatureGroup(new(target))
            }, name, motionSteps);
    }

    internal WorldObjectId[] Validate(ICoreReadView core)
    {
        if (!(Location switch
            {
                WorldLocation.Edge edge => core.FindEdge(edge.Id) is Lookup<EdgeView>.Found,
                WorldLocation.Node node => core.FindNode(node.Id) is Lookup<NodeView>.Found,
                _ => false
            }))
            throw new InvalidDataException("指定场景不存在。");
        if (Target is WorldObjectId.Instance(var instance))
        {
            if (core.FindInstance(instance) is not Lookup<InstanceView>.Found foundInstance)
                throw new InvalidDataException("指定实例不存在。");
            if (!SameScene(Location, foundInstance.Value.Location))
                throw new InvalidDataException("实例不在指定场景。");
            return [Target];
        }
        if (Target is WorldObjectId.Lair(var lair))
        {
            if (core.FindLair(lair) is not Lookup<LairView>.Found foundLair)
                throw new InvalidDataException("指定生物聚落不存在。");
            if (Location is not WorldLocation.Node at || at.Id != foundLair.Value.Node)
                throw new InvalidDataException("生物聚落不在指定场景。");
            return [new WorldObjectId.Lair(lair)];
        }
        if (Target is WorldObjectId.CreatureGroup(var group))
        {
            if (core.FindCreatureGroup(group) is not Lookup<CreatureGroupView>.Found foundGroup || foundGroup.Value.Creatures.IsEmpty)
                throw new InvalidDataException("指定生物群不存在或成员为空。");
            if (!SameScene(Location, foundGroup.Value.Location)) throw new InvalidDataException("生物群不在指定场景。");
            return foundGroup.Value.Creatures.Select(creature => (WorldObjectId)new WorldObjectId.Creature(creature.Id)).ToArray();
        }
        if (Target is not WorldObjectId.Train(var trainId)) throw new ArgumentException("不支持的截图对象。");
        if (core.FindTrain(trainId) is not Lookup<TrainView>.Found found || found.Value.Vehicles.IsEmpty)
            throw new InvalidDataException("指定列车不存在或编组为空。");
        var present = (Location, found.Value.Position) switch
        {
            (WorldLocation.Edge edge, TrainPosition.Edge train) => edge.Id == train.Id,
            (WorldLocation.Node node, TrainPosition.Node train) => node.Id == train.Id,
            _ => false
        };
        if (!present) throw new InvalidDataException($"列车不在指定场景。当前位置：{found.Value.Position}");
        return [new WorldObjectId.Instance(new InstanceId.Vehicle(found.Value.Vehicles[0]))];
    }

    private static bool SameScene(WorldLocation left, WorldLocation right) => (left, right) switch
    {
        (WorldLocation.Edge a, WorldLocation.Edge b) => a.Id == b.Id,
        (WorldLocation.Node a, WorldLocation.Node b) => a.Id == b.Id,
        _ => false
    };
}
