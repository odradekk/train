namespace Train.Domain.Settlements;

public sealed class Station : IEquatable<Station>
{
    public StationId Id { get; }

    public Station(StationId id)
    {
        IdentityText.EnsureDefined(id.Value, nameof(id));
        Id = id;
    }

    public bool Equals(Station? other) => other is not null && Id.Equals(other.Id);

    public override bool Equals(object? obj) => obj is Station other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode();
}
