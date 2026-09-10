using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train;

public sealed class CompositeConsoleCommands
{
    public const int PartPageSize = 32;
    public const int VehiclePageSize = 8;

    private readonly VoxelDevelopmentSession _session;

    public CompositeConsoleCommands(VoxelDevelopmentSession session)
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
            return FormatReset();
        }

        if (arguments.Count is 1 or 2 &&
            string.Equals(arguments[0], "manufacture", StringComparison.OrdinalIgnoreCase) &&
            TryDefinition(arguments.Count == 1 ? HollowShellFixture.DefinitionId.Value : arguments[1], out var definitionId))
            return FormatManufacture(_session.Manufacture(definitionId));

        if (arguments.Count >= 1 && string.Equals(arguments[0], "query", StringComparison.OrdinalIgnoreCase))
        {
            if (arguments.Count == 1)
                return FormatVehiclePage(_session.QueryComposites(), 1);
            if (arguments.Count is 2 or 3 &&
                string.Equals(arguments[1], "page", StringComparison.OrdinalIgnoreCase) &&
                TryListPage(arguments, out var listPage))
                return FormatVehiclePage(_session.QueryComposites(), listPage);
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

        return string.Join('\n',
            "Usage: composite reset | composite manufacture [<namespace:id>] | composite query [page [<page>] | <id> [parts [<page>]]]",
            "Default manufacture id is core:dev-hollow-shell. Query without an id lists one page of compact vehicle summaries.",
            $"Vehicle pages are {VehiclePageSize} entries, sorted by runtime id. Part pages are {PartPageSize} entries, sorted by local cell.",
            "Page numbers start at 1.");
    }

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
            $"transform {FormatNumber(query.Transform.OriginX)},{FormatNumber(query.Transform.OriginY)},{FormatNumber(query.Transform.OriginZ)} {query.Transform.Orientation}",
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

    private static bool TryListPage(IReadOnlyList<string> arguments, out int page)
    {
        page = 1;
        if (arguments.Count == 2)
            return true;
        return arguments.Count == 3 && int.TryParse(arguments[2], out page) && page > 0;
    }

    private static bool TryPartPage(IReadOnlyList<string> arguments, out int page)
    {
        page = 1;
        if (arguments.Count == 3)
            return true;
        return arguments.Count == 4 && int.TryParse(arguments[3], out page) && page > 0;
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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
}
