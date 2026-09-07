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

        if (arguments.Count >= 5 &&
            string.Equals(arguments[0], "damage", StringComparison.OrdinalIgnoreCase) &&
            (arguments.Count - 1) % 4 == 0 &&
            TryDamageTargets(arguments, out var targets))
            return FormatDamage(_session.Damage(targets));

        return "Usage: voxel reset | voxel query <x> <y> <z> | voxel remove <x> <y> <z> | voxel add <x> <y> <z> <namespace:id> <orientation> | voxel damage <x> <y> <z> <amount> [<x> <y> <z> <amount> ...].";
    }

    private string FormatResult(WorldCommandResult result) =>
        $"Voxel {result.Status} at ({result.Position.X}, {result.Position.Y}, {result.Position.Z}). {FormatWithCount(_session.Query(result.Position))}";

    private string FormatDamage(WorldDamageResult result)
    {
        if (!result.Succeeded)
            return FormatResult(new WorldCommandResult(result.Status, result.Position));

        // Field-safe lines: a legal max-length definition ID already fills most of
        // FormatConsole's 200-character Message clip when packed onto one line.
        return string.Join("\n", result.Targets.SelectMany(target =>
        {
            var query = _session.Query(target.Position);
            return new[]
            {
                $"Voxel {target.Status} at ({target.Position.X}, {target.Position.Y}, {target.Position.Z}) by {target.Amount}.",
                Format(query),
                $"Voxel count: {_session.World.VoxelCount}."
            };
        }));
    }

    private string FormatWithCount(WorldVoxelQuery query) =>
        $"{Format(query)} Voxel count: {_session.World.VoxelCount}.";

    private static string Format(WorldVoxelQuery query) => query.Voxel is null
        ? $"Cell ({query.Position.X}, {query.Position.Y}, {query.Position.Z}): empty."
        : $"Cell ({query.Position.X}, {query.Position.Y}, {query.Position.Z}): {query.Voxel.DefinitionId.Value}, orientation {query.Voxel.Orientation}, durability {query.Voxel.CurrentDurability}.";

    private static bool TryDamageTargets(IReadOnlyList<string> arguments, out IReadOnlyList<VoxelDamageTarget> targets)
    {
        var parsed = new VoxelDamageTarget[(arguments.Count - 1) / 4];
        for (var index = 0; index < parsed.Length; index++)
        {
            var start = 1 + index * 4;
            if (!TryPosition(arguments, start, out var position) || !int.TryParse(arguments[start + 3], out var amount))
            {
                targets = Array.Empty<VoxelDamageTarget>();
                return false;
            }

            parsed[index] = new VoxelDamageTarget(position, amount);
        }

        targets = parsed;
        return true;
    }

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
