using System.Globalization;
using Train.Domain.Commands;
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
        if (arguments.Count > 0 && string.Equals(arguments[0], "new", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count is not (5 or 6) ||
                !int.TryParse(arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeX) ||
                !int.TryParse(arguments[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeY) ||
                !int.TryParse(arguments[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeZ) ||
                !ulong.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out var seed) ||
                !BlankWorldSource.TryIdentity(sizeX, sizeY, sizeZ, seed, out var identity))
                return BlankParametersRejected();
            WorldChangeChoice? choice = null;
            if (arguments.Count == 6)
            {
                if (!WorldChangeConsole.TryParseChoice(arguments[5], out var parsed))
                    return BlankParametersRejected();
                choice = parsed;
            }
            var changed = _session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.NewBlank, choice, BlankIdentity: identity));
            return changed.Status == WorldChangeStatus.Completed
                ? $"Voxel blank world.\nbounds {sizeX},{sizeY},{sizeZ} seed {seed}\npaused true unsaved true\nVoxel count: 0."
                : WorldChangeConsole.Format(_session, changed);
        }
        if (arguments.Count is >= 1 and <= 2 && string.Equals(arguments[0], "reset", StringComparison.OrdinalIgnoreCase))
        {
            WorldChangeChoice? choice = null;
            if (arguments.Count == 2)
            {
                if (!WorldChangeConsole.TryParseChoice(arguments[1], out var parsed))
                    return Usage();
                choice = parsed;
            }

            var changed = _session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Reset, choice));
            if (changed.Status != WorldChangeStatus.Completed)
                return WorldChangeConsole.Format(_session, changed);
            var sample = _session.Identity.Slice switch
            {
                VoxelDevelopmentSlice.Slice2 => new WorldVoxelCoord(15, 16, 15),
                VoxelDevelopmentSlice.Slice3 => new WorldVoxelCoord(8, 8, 8),
                VoxelDevelopmentSlice.Slice4 => new WorldVoxelCoord(80, 1, 15),
                _ => new WorldVoxelCoord(4, 4, 4)
            };
            return $"Voxel world reset. {FormatWithCount(_session.Query(sample))}";
        }

        if (arguments.Count is 2 or 3 && string.Equals(arguments[0], "use", StringComparison.OrdinalIgnoreCase))
        {
            WorldChangeChoice? choice = null;
            if (arguments.Count == 3)
            {
                if (!WorldChangeConsole.TryParseChoice(arguments[2], out var parsed))
                    return Usage();
                choice = parsed;
            }

            var changed = _session.TryChangeWorld(new WorldChangeRequest(
                WorldChangeKind.SwitchConfiguration,
                choice,
                arguments[1]));
            if (changed.Status != WorldChangeStatus.Completed)
                return WorldChangeConsole.Format(_session, changed);
            return string.Join("\n",
                $"Voxel configuration {_session.WorldSource?.ConfigurationId}.",
                $"paused {_session.SimulationPaused.ToString().ToLowerInvariant()}",
                $"Voxel count: {_session.World.VoxelCount}.");
        }

        if (arguments.Count >= 1 && string.Equals(arguments[0], "edits", StringComparison.OrdinalIgnoreCase) &&
            WorldEditConsole.Execute(_session, arguments) is { } editsText)
            return editsText;

        if (WorldEditConsole.TrySplitAt(arguments, out var editArguments, out var atStep) &&
            atStep is { } step &&
            TryParseEdit(editArguments, out var edit))
            return WorldEditConsole.FormatSubmission(_session.SubmitEdit(edit, step));

        if (arguments.Count == 4 && string.Equals(arguments[0], "remove", StringComparison.OrdinalIgnoreCase) &&
            TryPosition(arguments, 1, out var removePosition))
            return FormatResult(_session.Remove(removePosition));

        if (arguments.Count == 4 && string.Equals(arguments[0], "query", StringComparison.OrdinalIgnoreCase) &&
            TryPosition(arguments, 1, out var queryPosition))
            return FormatWithCount(_session.Query(queryPosition));

        if (arguments.Count > 0 &&
            string.Equals(arguments[0], "collide", StringComparison.OrdinalIgnoreCase) &&
            TryCollisionQuery(arguments, out var collision))
            return FormatCollision(collision);

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

        if (arguments.Count >= 1 && string.Equals(arguments[0], "save", StringComparison.OrdinalIgnoreCase))
            return FormatSave(_session.Save(arguments.Count == 1 ? null : string.Join(' ', arguments.Skip(1))));

        if (arguments.Count is 2 or 3 &&
            string.Equals(arguments[0], "load", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(arguments[1], out var saveId))
        {
            WorldChangeChoice? choice = null;
            if (arguments.Count == 3)
            {
                if (!WorldChangeConsole.TryParseChoice(arguments[2], out var parsed))
                    return Usage();
                choice = parsed;
            }

            var changed = _session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Load, choice, SaveId: saveId));
            if (changed.Status == WorldChangeStatus.Completed)
            {
                return FormatSave(new WorldSaveResult(
                    WorldSaveStatus.Loaded,
                    _session.SaveId,
                    _session.DisplayName,
                    Adaptations: changed.Adaptations));
            }

            if (changed.Status == WorldChangeStatus.CandidateFailed && changed.CandidateStatus is { } candidateStatus)
            {
                return FormatSave(new WorldSaveResult(
                    candidateStatus,
                    _session.SaveId,
                    _session.DisplayName,
                    changed.Diagnostic ?? WorldSaveDiagnostic.For(candidateStatus)));
            }

            return WorldChangeConsole.Format(_session, changed);
        }

        return Usage();
    }

    private string Usage()
    {
        return string.Join('\n',
            "Usage: voxel reset [discard|save|cancel] | voxel query <x> <y> <z> | voxel collide <x> <y> <z> | voxel save [<display-name>] | voxel load <uuid> [discard|save|cancel]",
            "voxel use <configuration-id> [discard|save|cancel]",
            $"voxel new <size-x> <size-y> <size-z> <seed> [discard|save|cancel] ({BlankWorldSource.ExtentDescription}; seed 0..18446744073709551615)",
            "voxel collide <minx> <miny> <minz> <maxx> <maxy> <maxz>",
            "voxel edits [pending | log [<count>] | fault]",
            "voxel remove <x> <y> <z> [at <step>] | voxel add <x> <y> <z> <namespace:id> <orientation> [at <step>]",
            "voxel damage <x> <y> <z> <amount> [<x> <y> <z> <amount> ...] [at <step>].");
    }

    private static string BlankParametersRejected() =>
        $"Voxel IncompatibleBase.\nstage base subject blank relationship parameters\nUse voxel new <size-x> <size-y> <size-z> <seed> [discard|save|cancel]; {BlankWorldSource.ExtentDescription}; seed 0..18446744073709551615.";

    private static bool TryParseEdit(IReadOnlyList<string> arguments, out WorldEdit edit)
    {
        edit = null!;
        if (arguments.Count == 4 && string.Equals(arguments[0], "remove", StringComparison.OrdinalIgnoreCase) &&
            TryPosition(arguments, 1, out var removePosition))
            edit = new VoxelRemoveEdit(removePosition);
        else if (arguments.Count == 6 && string.Equals(arguments[0], "add", StringComparison.OrdinalIgnoreCase) &&
                 TryPosition(arguments, 1, out var addPosition) &&
                 TryDefinition(arguments[4], out var definitionId) &&
                 VoxelOrientation.TryParse(arguments[5], out var orientation))
            edit = new VoxelAddEdit(addPosition, definitionId, orientation);
        else if (arguments.Count >= 5 &&
                 string.Equals(arguments[0], "damage", StringComparison.OrdinalIgnoreCase) &&
                 (arguments.Count - 1) % 4 == 0 &&
                 TryDamageTargets(arguments, out var targets))
            edit = new VoxelDamageEdit(targets);
        return edit is not null;
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

    private string FormatSave(WorldSaveResult result)
    {
        if (!result.Succeeded)
        {
            var failed = new List<string> { $"Voxel {result.Status}.", $"Voxel count: {_session.World.VoxelCount}." };
            if (result.Diagnostic is { } diagnostic)
                failed.Insert(1, $"stage {diagnostic.Stage} subject {diagnostic.Subject} relationship {diagnostic.Relationship}");
            return string.Join("\n", failed);
        }

        var lines = new List<string>
        {
            $"Voxel {result.Status}.",
            $"id {result.SaveId:D}",
            $"name {result.DisplayName}",
            $"Voxel count: {_session.World.VoxelCount}."
        };
        foreach (var adaptation in result.Adaptations ?? Array.Empty<WorldSaveAdaptation>())
            lines.Add($"adapted {adaptation.Subject} {adaptation.Relationship}");
        return string.Join("\n", lines);
    }

    private string FormatWithCount(WorldVoxelQuery query) =>
        string.Join("\n", FormatLines(query).Append($"Voxel count: {_session.World.VoxelCount}."));

    private static IEnumerable<string> FormatLines(WorldVoxelQuery query)
    {
        if (query.Voxel is null)
        {
            yield return $"Cell ({query.Position.X}, {query.Position.Y}, {query.Position.Z}): empty.";
            yield break;
        }

        // Field-safe lines keep a legal max-length definition ID inside FormatConsole's 200-character clip.
        yield return $"Cell ({query.Position.X}, {query.Position.Y}, {query.Position.Z}): {query.Voxel.DefinitionId.Value}";
        yield return $"orientation {query.Voxel.Orientation}";
        yield return $"durability {query.Voxel.CurrentDurability}";
        if (query.Voxel.RuntimeId is { } runtimeId)
            yield return $"runtime {runtimeId}";
        if (query.Value is long value)
            yield return $"value {value}";
        if (query.Mass is double mass)
            yield return $"mass {VoxelDefinition.FormatMass(mass)}";
        if (query.MaxDurability is int max)
            yield return $"max {max}";
    }

    private static string Format(WorldVoxelQuery query) => string.Join("\n", FormatLines(query));

    private bool TryCollisionQuery(IReadOnlyList<string> arguments, out VoxelCollisionQueryResult result)
    {
        result = VoxelCollisionQueryResult.Invalid();
        if (arguments.Count == 4 && TryPosition(arguments, 1, out var cell))
        {
            result = _session.QueryCollision(cell);
            return true;
        }

        if (arguments.Count == 7 &&
            TryCoordinate(arguments[1], out var minX) &&
            TryCoordinate(arguments[2], out var minY) &&
            TryCoordinate(arguments[3], out var minZ) &&
            TryCoordinate(arguments[4], out var maxX) &&
            TryCoordinate(arguments[5], out var maxY) &&
            TryCoordinate(arguments[6], out var maxZ))
        {
            result = _session.QueryCollision(minX, minY, minZ, maxX, maxY, maxZ);
            return true;
        }

        return false;
    }

    private static string FormatCollision(VoxelCollisionQueryResult result)
    {
        if (result.Status == VoxelCollisionQueryStatus.InvalidQuery)
            return "Collision InvalidQuery.";
        if (result.Status == VoxelCollisionQueryStatus.OutOfBounds)
            return result.Probe is { } outOfBounds
                ? $"Collision OutOfBounds [{FormatBox(outOfBounds)}]."
                : "Collision OutOfBounds.";
        if (result.Status == VoxelCollisionQueryStatus.Miss || result.Hits.Count == 0)
            return result.Probe is { } miss
                ? $"Collision Miss [{FormatBox(miss)}]."
                : "Collision Miss.";

        var lines = new List<string> { $"Collision Hit ({result.Hits.Count})" };
        foreach (var hit in result.Hits)
        {
            lines.Add($"cell ({hit.Cell.X}, {hit.Cell.Y}, {hit.Cell.Z}): {hit.DefinitionId.Value}");
            lines.Add($"kind {VoxelDefinition.ToWire(hit.Kind)}");
        }

        return string.Join("\n", lines);
    }

    private static string FormatBox(VoxelCollisionBox box) =>
        $"{FormatCoordinate(box.MinX)}, {FormatCoordinate(box.MinY)}, {FormatCoordinate(box.MinZ)} -> {FormatCoordinate(box.MaxX)}, {FormatCoordinate(box.MaxY)}, {FormatCoordinate(box.MaxZ)}";

    private static bool TryCoordinate(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

    private static string FormatCoordinate(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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
