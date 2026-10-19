using System.Globalization;
using Train.Core.Contracts;

namespace Train.App.ArtCapture;

internal sealed record CaptureInput(string SavePath, WorldLocation Location, TrainId Train, string Name,
    LairId? Lair = null, CreatureGroupId? Group = null, ulong MotionSteps = 2)
{
    internal static CaptureInput Parse(IReadOnlyList<string> arguments)
    {
        const string usage = "用法：--capture-save <存档> --capture-location edge:<ID>|node:<ID> --capture-train <ID>|--capture-lair <ID>|--capture-group <ID> --capture-name <名称> [--capture-motion-steps <正整数>]";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Count; i += 2)
        {
            if (i + 1 >= arguments.Count || arguments[i] is not
                ("--capture-save" or "--capture-location" or "--capture-train" or "--capture-lair" or "--capture-group" or "--capture-name" or "--capture-motion-steps") ||
                !values.TryAdd(arguments[i], arguments[i + 1])) throw new ArgumentException(usage);
        }
        var targets = new[] { "--capture-train", "--capture-lair", "--capture-group" }.Where(values.ContainsKey).ToArray();
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
            new(targets[0] == "--capture-train" ? target : 0), name,
            targets[0] == "--capture-lair" ? new(target) : null, targets[0] == "--capture-group" ? new(target) : null, motionSteps);
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
        if (Lair is { } lair)
        {
            if (core.FindLair(lair) is not Lookup<LairView>.Found foundLair)
                throw new InvalidDataException("指定生物聚落不存在。");
            if (Location is not WorldLocation.Node at || at.Id != foundLair.Value.Node)
                throw new InvalidDataException("生物聚落不在指定场景。");
            return [new WorldObjectId.Lair(lair)];
        }
        if (Group is { } group)
        {
            if (core.FindCreatureGroup(group) is not Lookup<CreatureGroupView>.Found foundGroup || foundGroup.Value.Creatures.IsEmpty)
                throw new InvalidDataException("指定生物群不存在或成员为空。");
            var presentGroup = (Location, foundGroup.Value.Location) switch
            {
                (WorldLocation.Edge left, WorldLocation.Edge right) => left.Id == right.Id,
                (WorldLocation.Node left, WorldLocation.Node right) => left.Id == right.Id,
                _ => false
            };
            if (!presentGroup) throw new InvalidDataException("生物群不在指定场景。");
            return foundGroup.Value.Creatures.Select(creature => (WorldObjectId)new WorldObjectId.Creature(creature.Id)).ToArray();
        }
        if (core.FindTrain(Train) is not Lookup<TrainView>.Found found || found.Value.Vehicles.IsEmpty)
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
}
