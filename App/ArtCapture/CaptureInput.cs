using System.Globalization;
using Train.Core.Contracts;

namespace Train.App.ArtCapture;

internal sealed record CaptureInput(string SavePath, WorldLocation Location, TrainId Train, string Name)
{
    internal static CaptureInput Parse(IReadOnlyList<string> arguments)
    {
        const string usage = "用法：--capture-save <存档> --capture-location edge:<ID>|node:<ID> --capture-train <ID> --capture-name <名称>";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Count; i += 2)
        {
            if (i + 1 >= arguments.Count || arguments[i] is not
                ("--capture-save" or "--capture-location" or "--capture-train" or "--capture-name") ||
                !values.TryAdd(arguments[i], arguments[i + 1])) throw new ArgumentException(usage);
        }
        if (values.Count != 4) throw new ArgumentException(usage);
        var scene = values["--capture-location"].Split(':');
        if (scene.Length != 2 || scene[0] is not ("edge" or "node") ||
            !ulong.TryParse(scene[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0 ||
            !ulong.TryParse(values["--capture-train"], NumberStyles.None, CultureInfo.InvariantCulture, out var train) || train == 0)
            throw new ArgumentException(usage);
        var name = values["--capture-name"];
        if (name.Length == 0 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("输出名称只接受英文字母、数字、连字符和下划线。");
        return new(Path.GetFullPath(values["--capture-save"]),
            scene[0] == "edge" ? new WorldLocation.Edge(new(id), 0) : new WorldLocation.Node(new(id)), new(train), name);
    }

    internal TrainView Validate(ICoreReadView core)
    {
        if (!(Location switch
            {
                WorldLocation.Edge edge => core.FindEdge(edge.Id) is Lookup<EdgeView>.Found,
                WorldLocation.Node node => core.FindNode(node.Id) is Lookup<NodeView>.Found,
                _ => false
            }))
            throw new InvalidDataException("指定场景不存在。");
        if (core.FindTrain(Train) is not Lookup<TrainView>.Found found || found.Value.Vehicles.IsEmpty)
            throw new InvalidDataException("指定列车不存在或编组为空。");
        var present = (Location, found.Value.Position) switch
        {
            (WorldLocation.Edge edge, TrainPosition.Edge train) => edge.Id == train.Id,
            (WorldLocation.Node node, TrainPosition.Node train) => node.Id == train.Id,
            _ => false
        };
        if (!present) throw new InvalidDataException($"列车不在指定场景。当前位置：{found.Value.Position}");
        return found.Value;
    }
}
