using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Train.Domain.Goods;

namespace Train.Domain.Settlements;

public sealed class Settlement : IEquatable<Settlement>
{
    private readonly Dictionary<BuildingTypeId, int> _buildings = new();

    public SettlementId Id { get; }
    public string Name { get; private set; }
    public SettlementTypeId Type { get; private set; }
    public MapPosition Position { get; private set; }
    public int Population { get; private set; }
    public IReadOnlyDictionary<BuildingTypeId, int> Buildings { get; }
    public IReadOnlySet<GoodId> NaturalResourceIds { get; private set; }
    public Station? Station { get; private set; }

    public Settlement(
        SettlementId id,
        string name,
        SettlementTypeId type,
        MapPosition position,
        int population,
        IReadOnlyDictionary<BuildingTypeId, int> buildings,
        IReadOnlySet<GoodId> naturalResourceIds,
        Station? station = null)
    {
        IdentityText.EnsureDefined(id.Value, nameof(id));
        IdentityText.EnsureDefined(type.Value, nameof(type));
        ArgumentNullException.ThrowIfNull(buildings);
        ArgumentNullException.ThrowIfNull(naturalResourceIds);

        Id = id;
        Name = NormalizeName(name);
        Type = type;
        Position = position;
        Population = EnsurePositivePopulation(population);
        CopyBuildings(buildings);
        Buildings = new ReadOnlyDictionary<BuildingTypeId, int>(_buildings);
        NaturalResourceIds = CopyNaturalResourceIds(naturalResourceIds);
        if (station is not null)
            IdentityText.EnsureDefined(station.Id.Value, nameof(station));
        Station = station;
    }

    public void Rename(string name)
    {
        var normalized = NormalizeName(name);
        if (Name == normalized)
            return;
        Name = normalized;
    }

    public void ChangeType(SettlementTypeId type)
    {
        IdentityText.EnsureDefined(type.Value, nameof(type));
        if (Type.Equals(type))
            return;
        Type = type;
    }

    public void SetPopulation(int population)
    {
        var normalized = EnsurePositivePopulation(population);
        if (Population == normalized)
            return;
        Population = normalized;
    }

    public void SetPosition(MapPosition position)
    {
        if (Position.Equals(position))
            return;
        Position = position;
    }

    public void SetNaturalResourceIds(IReadOnlySet<GoodId> naturalResourceIds)
    {
        ArgumentNullException.ThrowIfNull(naturalResourceIds);
        var copy = CopyNaturalResourceIds(naturalResourceIds);
        if (NaturalResourceIds.SetEquals(copy))
            return;
        NaturalResourceIds = copy;
    }

    public void SetBuildingCount(BuildingTypeId buildingType, int count)
    {
        IdentityText.EnsureDefined(buildingType.Value, nameof(buildingType));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Building count cannot be negative.");

        if (count == 0)
        {
            _buildings.Remove(buildingType);
            return;
        }

        if (_buildings.TryGetValue(buildingType, out var current) && current == count)
            return;

        _buildings[buildingType] = count;
    }

    public void AttachStation(Station station)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (Station is not null)
            throw new InvalidOperationException("Settlement already has a station.");
        Station = station;
    }

    public void DetachStation()
    {
        if (Station is null)
            throw new InvalidOperationException("Settlement has no station.");
        Station = null;
    }

    public bool Equals(Settlement? other) => other is not null && Id.Equals(other.Id);

    public override bool Equals(object? obj) => obj is Settlement other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode();

    private static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Name cannot be blank.", nameof(name));
        return trimmed;
    }

    private static int EnsurePositivePopulation(int population)
    {
        if (population <= 0)
            throw new ArgumentOutOfRangeException(nameof(population), population, "Population must be greater than zero.");
        return population;
    }

    private void CopyBuildings(IReadOnlyDictionary<BuildingTypeId, int> buildings)
    {
        foreach (var (buildingType, count) in buildings)
        {
            IdentityText.EnsureDefined(buildingType.Value, nameof(buildings));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(buildings), count, "Building count cannot be negative.");
            if (count == 0)
                continue;
            _buildings[buildingType] = count;
        }
    }

    private static FrozenSet<GoodId> CopyNaturalResourceIds(IReadOnlySet<GoodId> naturalResourceIds)
    {
        var copy = new HashSet<GoodId>();
        foreach (var resourceId in naturalResourceIds)
        {
            if (string.IsNullOrEmpty(resourceId.Namespace) || string.IsNullOrEmpty(resourceId.LocalId))
                throw new ArgumentException("GoodId cannot be blank.", nameof(naturalResourceIds));
            copy.Add(resourceId);
        }

        return copy.ToFrozenSet();
    }
}
