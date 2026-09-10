using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train;

public sealed class CompositeConsoleCommands
{
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

        if (arguments.Count == 1 && string.Equals(arguments[0], "query", StringComparison.OrdinalIgnoreCase))
            return FormatQueries(_session.QueryComposites());

        if (arguments.Count == 2 &&
            string.Equals(arguments[0], "query", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(arguments[1], out var rawId) &&
            rawId > 0)
        {
            var query = _session.QueryComposite(new EntityId(rawId));
            return query is null
                ? $"Composite UnknownVehicle {rawId}."
                : FormatQuery(query);
        }

        return string.Join('\n',
            "Usage: composite reset | composite manufacture [<namespace:id>] | composite query [<id>]",
            "Default manufacture id is core:dev-hollow-shell. Query without an id lists every manufactured vehicle.");
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

    private static string FormatQueries(IReadOnlyList<CompositeQuery> queries)
    {
        if (queries.Count == 0)
            return "Composite none.";
        return string.Join('\n', queries.SelectMany(query => FormatQuery(query).Split('\n')));
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

        return string.Join('\n', lines);
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
