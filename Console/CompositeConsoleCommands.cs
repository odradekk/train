using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train;

public sealed class CompositeConsoleCommands
{
    public const int PartPageSize = 32;
    public const int VehiclePageSize = 8;

    private readonly VoxelDevelopmentSession _session;
    private readonly Func<WorldChangeRequest, WorldChangeResult> _changeWorld;

    public CompositeConsoleCommands(VoxelDevelopmentSession session, Func<WorldChangeRequest, WorldChangeResult>? changeWorld = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _changeWorld = changeWorld ?? session.TryChangeWorld;
    }

    public string Execute(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count >= 1 && string.Equals(arguments[0], "reset", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseReset(arguments, out var choice, out var resetId))
                return Usage();
            var changed = _changeWorld(new WorldChangeRequest(
                WorldChangeKind.Reset,
                choice,
                FixtureId: resetId?.Value));
            if (changed.Status != WorldChangeStatus.Completed)
                return WorldChangeConsole.Format(_session, changed);
            return resetId is null ? FormatReset() : string.Join('\n', FormatReset(), FormatManufactured(changed));
        }

        if (WorldEditConsole.TrySplitAt(arguments, out var editArguments, out var atStep) &&
            atStep is { } step &&
            TryParseEdit(editArguments, out var edit))
            return WorldEditConsole.FormatSubmission(_session.SubmitEdit(edit, step));

        if (TryManufacture(arguments, out var definitionId, out var pose))
            return FormatManufacture(_session.Manufacture(definitionId, pose));

        if (arguments.Count >= 6 &&
            string.Equals(arguments[0], "damage", StringComparison.OrdinalIgnoreCase) &&
            (arguments.Count - 2) % 4 == 0 &&
            ulong.TryParse(arguments[1], out var damageId) &&
            damageId > 0 &&
            TryDamageTargets(new EntityId(damageId), arguments, out var targets))
            return FormatDamage(_session.Damage(targets));

        if (arguments.Count >= 1 && string.Equals(arguments[0], "query", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count == 1)
                return FormatVehiclePage(_session.QueryComposites(), 1);
            if (arguments.Count is 2 or 3 &&
                string.Equals(arguments[1], "page", StringComparison.OrdinalIgnoreCase) &&
                TryListPage(arguments, out var listPage))
                return FormatVehiclePage(_session.QueryComposites(), listPage);
            if (arguments.Count is 2 or 3 &&
                string.Equals(arguments[1], "damaged", StringComparison.OrdinalIgnoreCase) &&
                TryDamagedPage(arguments, out var damagedPage))
                return FormatDamagedPage(_session.QueryDamagedParts(), damagedPage);
            if (ulong.TryParse(arguments[1], out var rawId) && rawId > 0)
            {
                var query = _session.QueryComposite(new EntityId(rawId));
                if (query is null)
                    return $"Composite UnknownVehicle {rawId}.";
                if (arguments.Count == 2)
                    return FormatQuery(query);
                if (arguments.Count is 3 or 4 &&
                    string.Equals(arguments[2], "parts", StringComparison.OrdinalIgnoreCase) &&
                    TryPartPage(arguments, out var partPage))
                    return FormatPartsPage(query, partPage);
            }
        }

        if (TryRefit(arguments, out var refitText))
            return refitText;

        return Usage();
    }

    private string Usage()
    {
        return string.Join('\n',
            "Usage: composite reset [discard|save|cancel] [hollow|bridge|bridge-right] | composite manufacture [<namespace:id>] [pose <x> <y> <z>]",
            "composite damage <id> <x> <y> <z> <amount> [<x> <y> <z> <amount> ...]",
            "composite query [page [<page>] | damaged [<page>] | <id> [parts [<page>]]]",
            "composite add <id> <x> <y> <z> <namespace:id> <orientation>",
            "composite remove <id> <partId> | composite move <id> <partId> <x> <y> <z>",
            "composite rotate <id> <partId> <orientation>",
            "composite replace <id> <x> <y> <z> <namespace:id> <orientation>",
            "composite batch <id> <add|remove|move|rotate|replace> ...",
            "Default manufacture id is core:dev-hollow-shell. reset hollow/bridge rebuilds that fixture after clearing the session.",
            $"Vehicle pages are {VehiclePageSize} entries, sorted by runtime id. Part pages are {PartPageSize} entries, sorted by local cell.",
            "Damage uses local cells on one vehicle, one event. Page numbers start at 1.",
            "Refit is one atomic transaction. Batch occupancy swaps are legal; a failed batch leaves authority unchanged.",
            "manufacture/damage/add/remove/move/rotate/replace/batch accept a trailing at <step> to queue the edit.");
    }

    private bool TryRefit(IReadOnlyList<string> arguments, out string text)
    {
        text = "";
        if (!TryRefitOperations(arguments, out var vehicleId, out var operations))
            return false;
        text = FormatRefit(_session.Refit(vehicleId, operations));
        return true;
    }

    private static bool TryParseEdit(IReadOnlyList<string> arguments, out WorldEdit edit)
    {
        edit = null!;
        if (TryManufacture(arguments, out var definitionId, out var pose))
            edit = new VehicleManufactureEdit(definitionId, pose);
        else if (arguments.Count >= 6 &&
                 string.Equals(arguments[0], "damage", StringComparison.OrdinalIgnoreCase) &&
                 (arguments.Count - 2) % 4 == 0 &&
                 TryEntityId(arguments[1], out var damageId) &&
                 TryDamageTargets(damageId, arguments, out var targets))
            edit = new VehicleDamageEdit(targets);
        else if (TryRefitOperations(arguments, out var vehicleId, out var operations))
            edit = new VehicleRefitEdit(vehicleId, operations);
        return edit is not null;
    }

    /// <summary><c>manufacture [&lt;namespace:id&gt;] [pose &lt;x&gt; &lt;y&gt; &lt;z&gt;]</c>; the pose is a world translation.</summary>
    private static bool TryManufacture(IReadOnlyList<string> arguments, out CompositeDefinitionId definitionId, out CompositeTransform pose)
    {
        definitionId = HollowShellFixture.DefinitionId;
        pose = HollowShellFixture.DefaultTransform;
        if (arguments.Count == 0 || !string.Equals(arguments[0], "manufacture", StringComparison.OrdinalIgnoreCase))
            return false;
        var count = arguments.Count;
        if (count >= 5 && string.Equals(arguments[count - 4], "pose", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryFinite(arguments[count - 3], out var x) || !TryFinite(arguments[count - 2], out var y) || !TryFinite(arguments[count - 1], out var z))
                return false;
            pose = CompositeTransform.Translation(x, y, z);
            count -= 4;
        }

        return count switch
        {
            1 => true,
            2 => TryDefinition(arguments[1], out definitionId),
            _ => false
        };
    }

    private static bool TryFinite(string text, out double value) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);

    private static bool TryRefitOperations(
        IReadOnlyList<string> arguments,
        out EntityId vehicleId,
        out IReadOnlyList<CompositeRefitOperation> parsed)
    {
        parsed = Array.Empty<CompositeRefitOperation>();
        vehicleId = default;
        if (arguments.Count < 2)
            return false;
        if (!TryEntityId(arguments[1], out vehicleId))
            return false;

        IReadOnlyList<CompositeRefitOperation>? operations = null;
        if (string.Equals(arguments[0], "add", StringComparison.OrdinalIgnoreCase) &&
            arguments.Count == 7 &&
            TryCell(arguments, 2, out var addCell) &&
            TryVoxelDefinition(arguments[5], out var addDefinition) &&
            VoxelOrientation.TryParse(arguments[6], out var addOrientation))
            operations = new[] { CompositeRefitOperation.Add(addCell, addDefinition, addOrientation) };
        else if (string.Equals(arguments[0], "remove", StringComparison.OrdinalIgnoreCase) &&
                 arguments.Count == 3 &&
                 TryEntityId(arguments[2], out var removeId))
            operations = new[] { CompositeRefitOperation.Remove(removeId) };
        else if (string.Equals(arguments[0], "move", StringComparison.OrdinalIgnoreCase) &&
                 arguments.Count == 6 &&
                 TryEntityId(arguments[2], out var moveId) &&
                 TryCell(arguments, 3, out var destination))
            operations = new[] { CompositeRefitOperation.Move(moveId, destination) };
        else if (string.Equals(arguments[0], "rotate", StringComparison.OrdinalIgnoreCase) &&
                 arguments.Count == 4 &&
                 TryEntityId(arguments[2], out var rotateId) &&
                 VoxelOrientation.TryParse(arguments[3], out var rotateOrientation))
            operations = new[] { CompositeRefitOperation.Rotate(rotateId, rotateOrientation) };
        else if (string.Equals(arguments[0], "replace", StringComparison.OrdinalIgnoreCase) &&
                 arguments.Count == 7 &&
                 TryCell(arguments, 2, out var replaceCell) &&
                 TryVoxelDefinition(arguments[5], out var replaceDefinition) &&
                 VoxelOrientation.TryParse(arguments[6], out var replaceOrientation))
            operations = new[] { CompositeRefitOperation.Replace(replaceCell, replaceDefinition, replaceOrientation) };
        else if (string.Equals(arguments[0], "batch", StringComparison.OrdinalIgnoreCase) &&
                 TryBatchOperations(arguments, out var batch))
            operations = batch;

        if (operations is null)
            return false;
        parsed = operations;
        return true;
    }

    private string FormatRefit(CompositeRefitResult result)
    {
        if (!result.Succeeded)
            return $"Composite {result.Status}{(result.VehicleId is { } id ? $" {id}" : "")}.";

        var allocated = result.AllocatedIds is { Count: > 0 }
            ? string.Join(' ', result.AllocatedIds.Select(item => item.ToString()))
            : "none";
        var query = _session.QueryComposite(result.VehicleId!.Value)!;
        return string.Join('\n',
            $"Composite {result.Status}.",
            $"vehicle {result.VehicleId}",
            $"allocated {allocated}",
            $"parts {query.Parts.Count}",
            $"construction {query.Construction}",
            $"Voxel count: {_session.World.VoxelCount}.");
    }

    private static string FormatManufactured(WorldChangeResult changed) =>
        string.Join('\n',
            $"Composite {changed.FixtureStatus}.",
            $"vehicle {changed.FixtureVehicleId}",
            $"parts {changed.FixturePartCount}");

    private string FormatReset()
    {
        var queries = _session.QueryComposites();
        return string.Join('\n',
            "Composite world reset.",
            $"vehicles {queries.Count}",
            $"next {_session.NextEntityId}",
            $"transactions {_session.TransactionCount}",
            $"Voxel count: {_session.World.VoxelCount}.");
    }

    private static string FormatManufacture(CompositeManufactureResult result)
    {
        if (!result.Succeeded)
            return $"Composite {result.Status}.";

        return string.Join('\n',
            $"Composite {result.Status}.",
            $"vehicle {result.VehicleId}",
            $"parts {result.Parts!.Count}");
    }

    private string FormatDamage(CompositeDamageResult result)
    {
        if (!result.Succeeded)
            return $"Composite {result.Status}{(result.VehicleId is { } id ? $" {id}" : "")}.";

        var lines = new List<string> { $"Composite {result.Status}." };
        foreach (var target in result.Targets ?? Array.Empty<CompositeDamageTargetResult>())
        {
            var location = target.VehicleId is { } owner
                ? $" vehicle {owner}"
                : target.WorldCell is { } world
                    ? $" world ({world.X},{world.Y},{world.Z})"
                    : "";
            var part = target.PartId is { } partId ? $" part {partId}" : "";
            lines.Add(
                $"target ({target.Cell.X},{target.Cell.Y},{target.Cell.Z}) {target.Status} by {target.Amount} durability {target.RemainingDurability}{location}{part}");
        }

        var vehicles = _session.QueryComposites();
        lines.Add($"vehicles {vehicles.Count}");
        foreach (var query in vehicles)
            lines.Add($"vehicle {query.Id} parts {query.Parts.Count} construction {query.Construction} identity {query.IdentityRetention} operational {query.OperationalCondition}");
        lines.Add($"Voxel count: {_session.World.VoxelCount}.");
        return string.Join('\n', lines);
    }

    private static string FormatDamagedPage(IReadOnlyList<CompositeDamagedPartQuery> parts, int page)
    {
        if (parts.Count == 0)
            return "Composite damaged none.";
        var totalPages = Math.Max(1, (int)Math.Ceiling(parts.Count / (double)PartPageSize));
        var lines = new List<string>
        {
            $"damaged {parts.Count}",
            $"damaged page {page}/{totalPages}"
        };
        if (page < 1 || page > totalPages)
            return string.Join('\n', lines);
        var start = (page - 1) * PartPageSize;
        foreach (var part in parts.Skip(start).Take(PartPageSize))
        {
            lines.Add(
                $"damaged vehicle {part.VehicleId} part {part.PartId} cell ({part.Cell.X},{part.Cell.Y},{part.Cell.Z}) {part.DefinitionId.Value} durability {part.CurrentDurability}/{part.MaxDurability}");
        }

        return string.Join('\n', lines);
    }

    private static string FormatVehiclePage(IReadOnlyList<CompositeQuery> queries, int page)
    {
        if (queries.Count == 0)
            return "Composite none.";
        var ordered = queries.OrderBy(query => query.Id.Value).ToArray();
        var totalPages = Math.Max(1, (int)Math.Ceiling(ordered.Length / (double)VehiclePageSize));
        var lines = new List<string>
        {
            $"vehicles {ordered.Length}",
            $"vehicles page {page}/{totalPages}"
        };
        if (page < 1 || page > totalPages)
            return string.Join('\n', lines);
        var start = (page - 1) * VehiclePageSize;
        foreach (var query in ordered.Skip(start).Take(VehiclePageSize))
        {
            lines.Add(
                $"vehicle {query.Id} {query.DefinitionId.Value} parts {query.Parts.Count} construction {query.Construction} identity {query.IdentityRetention}");
        }

        return string.Join('\n', lines);
    }

    private static string FormatQuery(CompositeQuery query)
    {
        var envelope = query.Envelope is { } box
            ? $"envelope {FormatNumber(box.MinX)},{FormatNumber(box.MinY)},{FormatNumber(box.MinZ)} -> {FormatNumber(box.MaxX)},{FormatNumber(box.MaxY)},{FormatNumber(box.MaxZ)}"
            : "envelope none";
        var lines = new List<string>
        {
            $"Composite {query.Id} {query.DefinitionId.Value}",
            $"archetype {query.ArchetypeId.Value}",
            $"size {VehicleSize.ToWire(query.SizeClass)}",
            $"construction {query.Construction}",
            $"identity {query.IdentityRetention}",
            $"operational {query.OperationalCondition}",
            $"capabilities {(query.Capabilities.Count == 0 ? "none" : string.Join(',', query.Capabilities))}",
            $"transform {FormatNumber(query.Transform.OriginX)},{FormatNumber(query.Transform.OriginY)},{FormatNumber(query.Transform.OriginZ)} {FormatNumber(query.Transform.RightX)},{FormatNumber(query.Transform.RightY)},{FormatNumber(query.Transform.RightZ)} {FormatNumber(query.Transform.UpX)},{FormatNumber(query.Transform.UpY)},{FormatNumber(query.Transform.UpZ)} {query.Transform.Orientation}",
            envelope,
            $"parts {query.Parts.Count}"
        };
        foreach (var contact in query.Contacts)
        {
            lines.Add(
                $"contact {FormatNumber(contact.X)},{FormatNumber(contact.Y)},{FormatNumber(contact.Z)} cell ({contact.BoundCell.X},{contact.BoundCell.Y},{contact.BoundCell.Z}) part {contact.BoundPartId}");
        }

        AddPartPage(lines, query, 1);
        return string.Join('\n', lines);
    }

    private static string FormatPartsPage(CompositeQuery query, int page)
    {
        var lines = new List<string>
        {
            $"Composite {query.Id} {query.DefinitionId.Value}",
            $"parts {query.Parts.Count}"
        };
        AddPartPage(lines, query, page);
        return string.Join('\n', lines);
    }

    private static void AddPartPage(List<string> lines, CompositeQuery query, int page)
    {
        var totalPages = Math.Max(1, (int)Math.Ceiling(query.Parts.Count / (double)PartPageSize));
        lines.Add($"parts page {page}/{totalPages}");
        if (page < 1 || page > totalPages)
            return;
        var start = (page - 1) * PartPageSize;
        foreach (var part in query.Parts.Skip(start).Take(PartPageSize))
        {
            lines.Add(
                $"part {part.Id} cell ({part.Cell.X},{part.Cell.Y},{part.Cell.Z}) {part.DefinitionId.Value} {part.Orientation} durability {part.CurrentDurability}");
        }
    }

    private static bool TryListPage(IReadOnlyList<string> arguments, out int page) =>
        TryOptionalTrailingPage(arguments, 2, out page);

    private static bool TryPartPage(IReadOnlyList<string> arguments, out int page) =>
        TryOptionalTrailingPage(arguments, 3, out page);

    private static string FormatNumber(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryDamagedPage(IReadOnlyList<string> arguments, out int page) =>
        TryOptionalTrailingPage(arguments, 2, out page);

    private static bool TryOptionalTrailingPage(IReadOnlyList<string> arguments, int pageArgumentIndex, out int page)
    {
        page = 1;
        if (arguments.Count == pageArgumentIndex)
            return true;
        return arguments.Count == pageArgumentIndex + 1 &&
               int.TryParse(arguments[pageArgumentIndex], out page) &&
               page > 0;
    }

    private static bool TryParseReset(
        IReadOnlyList<string> arguments,
        out WorldChangeChoice? choice,
        out CompositeDefinitionId? fixture)
    {
        choice = null;
        fixture = null;
        if (arguments.Count > 3)
            return false;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (choice is null && WorldChangeConsole.TryParseChoice(arguments[index], out var parsed))
            {
                choice = parsed;
                continue;
            }

            if (fixture is null && TryFixture(arguments[index], out var definitionId))
            {
                fixture = definitionId;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryFixture(string text, out CompositeDefinitionId definitionId)
    {
        if (string.Equals(text, "hollow", StringComparison.OrdinalIgnoreCase))
        {
            definitionId = HollowShellFixture.DefinitionId;
            return true;
        }

        if (string.Equals(text, "bridge", StringComparison.OrdinalIgnoreCase))
        {
            definitionId = BridgeFixture.DefinitionId;
            return true;
        }

        if (string.Equals(text, "bridge-right", StringComparison.OrdinalIgnoreCase))
        {
            definitionId = BridgeFixture.RightHeavyDefinitionId;
            return true;
        }

        definitionId = default;
        return false;
    }

    private static bool TryDamageTargets(
        EntityId vehicleId,
        IReadOnlyList<string> arguments,
        out IReadOnlyList<CompositeDamageTarget> targets)
    {
        var parsed = new CompositeDamageTarget[(arguments.Count - 2) / 4];
        for (var index = 0; index < parsed.Length; index++)
        {
            var start = 2 + index * 4;
            if (!int.TryParse(arguments[start], out var x) ||
                !int.TryParse(arguments[start + 1], out var y) ||
                !int.TryParse(arguments[start + 2], out var z) ||
                !int.TryParse(arguments[start + 3], out var amount))
            {
                targets = Array.Empty<CompositeDamageTarget>();
                return false;
            }

            parsed[index] = new CompositeDamageTarget(vehicleId, new LocalVoxelCoord(x, y, z), amount);
        }

        targets = parsed;
        return true;
    }

    private static bool TryDefinition(string text, out CompositeDefinitionId definitionId)
    {
        try
        {
            definitionId = CompositeDefinitionId.Parse(text);
            return true;
        }
        catch (ArgumentException)
        {
            definitionId = default;
            return false;
        }
    }

    private static bool TryVoxelDefinition(string text, out VoxelDefinitionId definitionId)
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

    private static bool TryEntityId(string text, out EntityId id)
    {
        if (ulong.TryParse(text, out var raw) && raw > 0)
        {
            id = new EntityId(raw);
            return true;
        }

        id = default;
        return false;
    }

    private static bool TryCell(IReadOnlyList<string> arguments, int start, out LocalVoxelCoord cell)
    {
        if (int.TryParse(arguments[start], out var x) &&
            int.TryParse(arguments[start + 1], out var y) &&
            int.TryParse(arguments[start + 2], out var z))
        {
            cell = new LocalVoxelCoord(x, y, z);
            return true;
        }

        cell = default;
        return false;
    }

    private static bool TryBatchOperations(IReadOnlyList<string> arguments, out IReadOnlyList<CompositeRefitOperation> operations)
    {
        var parsed = new List<CompositeRefitOperation>();
        var index = 2;
        while (index < arguments.Count)
        {
            var kind = arguments[index];
            if (string.Equals(kind, "add", StringComparison.OrdinalIgnoreCase) &&
                index + 5 < arguments.Count &&
                TryCell(arguments, index + 1, out var addCell) &&
                TryVoxelDefinition(arguments[index + 4], out var addDefinition) &&
                VoxelOrientation.TryParse(arguments[index + 5], out var addOrientation))
            {
                parsed.Add(CompositeRefitOperation.Add(addCell, addDefinition, addOrientation));
                index += 6;
                continue;
            }

            if (string.Equals(kind, "remove", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Count &&
                TryEntityId(arguments[index + 1], out var removeId))
            {
                parsed.Add(CompositeRefitOperation.Remove(removeId));
                index += 2;
                continue;
            }

            if (string.Equals(kind, "move", StringComparison.OrdinalIgnoreCase) &&
                index + 4 < arguments.Count &&
                TryEntityId(arguments[index + 1], out var moveId) &&
                TryCell(arguments, index + 2, out var destination))
            {
                parsed.Add(CompositeRefitOperation.Move(moveId, destination));
                index += 5;
                continue;
            }

            if (string.Equals(kind, "rotate", StringComparison.OrdinalIgnoreCase) &&
                index + 2 < arguments.Count &&
                TryEntityId(arguments[index + 1], out var rotateId) &&
                VoxelOrientation.TryParse(arguments[index + 2], out var rotateOrientation))
            {
                parsed.Add(CompositeRefitOperation.Rotate(rotateId, rotateOrientation));
                index += 3;
                continue;
            }

            if (string.Equals(kind, "replace", StringComparison.OrdinalIgnoreCase) &&
                index + 5 < arguments.Count &&
                TryCell(arguments, index + 1, out var replaceCell) &&
                TryVoxelDefinition(arguments[index + 4], out var replaceDefinition) &&
                VoxelOrientation.TryParse(arguments[index + 5], out var replaceOrientation))
            {
                parsed.Add(CompositeRefitOperation.Replace(replaceCell, replaceDefinition, replaceOrientation));
                index += 6;
                continue;
            }

            operations = Array.Empty<CompositeRefitOperation>();
            return false;
        }

        operations = parsed;
        return parsed.Count > 0;
    }
}
