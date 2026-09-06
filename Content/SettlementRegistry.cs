using System.Collections.ObjectModel;
using Train.Domain.Voxels;
using Train.Domain.Settlements;

namespace Train.Content;

public sealed class SettlementCandidate
{
    public SettlementId Id { get; }
    public SettlementTypeId Type { get; }
    public string Name { get; }
    public MapPosition Position { get; }
    public int Population { get; }
    public IReadOnlySet<VoxelDefinitionId> NaturalResourceIds { get; }
    public ContentPackageKind Kind { get; }

    public SettlementCandidate(
        SettlementId id,
        SettlementTypeId type,
        string name,
        MapPosition position,
        int population,
        IReadOnlySet<VoxelDefinitionId> naturalResourceIds,
        ContentPackageKind kind = ContentPackageKind.BuiltIn)
    {
        IdentityText.EnsureDefined(id.Value, nameof(id));
        IdentityText.EnsureDefined(type.Value, nameof(type));
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(naturalResourceIds);
        Id = id;
        Type = type;
        Name = name;
        Position = position;
        Population = population;
        NaturalResourceIds = naturalResourceIds;
        Kind = kind;
    }

    public bool Matches(Settlement settlement) =>
        settlement.Id.Equals(Id) &&
        settlement.Type.Equals(Type) &&
        settlement.Name == Name &&
        settlement.Position.Equals(Position) &&
        settlement.Population == Population &&
        settlement.NaturalResourceIds.SetEquals(NaturalResourceIds);
}

public sealed class SettlementRegistry
{
    private readonly Dictionary<SettlementId, Settlement> _items = new();
    private readonly List<Settlement> _order = new();

    public int Count => _items.Count;
    public bool HasCommitted { get; private set; }
    public IReadOnlyList<Settlement> Items => new ReadOnlyCollection<Settlement>(_order.ToArray());

    public bool TryGet(SettlementId id, out Settlement settlement) =>
        _items.TryGetValue(id, out settlement!);

    public void Apply(IReadOnlyList<SettlementCandidate> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var incoming = new Dictionary<SettlementId, SettlementCandidate>();
        foreach (var candidate in next)
            incoming[candidate.Id] = candidate;

        var removed = _order.Where(item => !incoming.ContainsKey(item.Id)).ToArray();
        foreach (var item in removed)
        {
            _items.Remove(item.Id);
            _order.Remove(item);
        }

        foreach (var candidate in next)
        {
            if (_items.TryGetValue(candidate.Id, out var existing))
            {
                if (candidate.Matches(existing))
                    continue;
                existing.Rename(candidate.Name);
                existing.ChangeType(candidate.Type);
                existing.SetPopulation(candidate.Population);
                existing.SetPosition(candidate.Position);
                existing.SetNaturalResourceIds(candidate.NaturalResourceIds);
                continue;
            }

            var created = new Settlement(
                candidate.Id,
                candidate.Name,
                candidate.Type,
                candidate.Position,
                candidate.Population,
                new Dictionary<BuildingTypeId, int>(),
                candidate.NaturalResourceIds);
            _items[candidate.Id] = created;
            _order.Add(created);
        }

        HasCommitted = true;
    }
}
