using Train.Domain.Voxels;

namespace Train;

public sealed class VoxelConsoleCommands
{
    private readonly VoxelDevelopmentSession _session;

    public VoxelConsoleCommands(VoxelDevelopmentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public string Execute(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 1 && string.Equals(arguments[0], "reset", StringComparison.OrdinalIgnoreCase))
        {
            _session.Reset();
            return $"Voxel world reset. {FormatWithCount(_session.Query(new WorldVoxelCoord(4, 4, 4)))}";
        }

        if (arguments.Count == 4 && string.Equals(arguments[0], "remove", StringComparison.OrdinalIgnoreCase) &&
            TryPosition(arguments, 1, out var removePosition))
            return FormatResult(_session.Remove(removePosition));

        if (arguments.Count == 4 && string.Equals(arguments[0], "query", StringComparison.OrdinalIgnoreCase) &&
            TryPosition(arguments, 1, out var queryPosition))
            return FormatWithCount(_session.Query(queryPosition));

        if (arguments.Count == 6 && string.Equals(arguments[0], "add", StringComparison.OrdinalIgnoreCase) &&
            TryPosition(arguments, 1, out var addPosition) &&
            TryDefinition(arguments[4], out var definitionId) &&
            VoxelOrientation.TryParse(arguments[5], out var orientation))
            return FormatResult(_session.Add(addPosition, definitionId, orientation));

        return "Usage: voxel reset | voxel query <x> <y> <z> | voxel remove <x> <y> <z> | voxel add <x> <y> <z> <namespace:id> <orientation>.";
    }

    private string FormatResult(WorldCommandResult result) =>
        $"Voxel {result.Status} at ({result.Position.X}, {result.Position.Y}, {result.Position.Z}). {FormatWithCount(_session.Query(result.Position))}";

    private string FormatWithCount(WorldVoxelQuery query) =>
        $"{Format(query)} Voxel count: {_session.World.VoxelCount}.";

    private static string Format(WorldVoxelQuery query) => query.Voxel is null
        ? $"Cell ({query.Position.X}, {query.Position.Y}, {query.Position.Z}): empty."
        : $"Cell ({query.Position.X}, {query.Position.Y}, {query.Position.Z}): {query.Voxel.DefinitionId.Value}, orientation {query.Voxel.Orientation}, durability {query.Voxel.CurrentDurability}.";

    private static bool TryPosition(IReadOnlyList<string> arguments, int start, out WorldVoxelCoord position)
    {
        if (int.TryParse(arguments[start], out var x) && int.TryParse(arguments[start + 1], out var y) && int.TryParse(arguments[start + 2], out var z))
        {
            position = new WorldVoxelCoord(x, y, z);
            return true;
        }

        position = default;
        return false;
    }

    private static bool TryDefinition(string text, out VoxelDefinitionId definitionId)
    {
        try
        {
            definitionId = VoxelDefinitionId.Parse(text);
            return true;
        }
        catch (ArgumentException)
        {
            definitionId = default;
            return false;
        }
    }
}
