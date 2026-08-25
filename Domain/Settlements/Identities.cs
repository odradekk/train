namespace Train.Domain.Settlements;

internal static class IdentityText
{
    public static string Normalize(string value, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Identity cannot be blank.", paramName);
        return trimmed;
    }

    public static void EnsureDefined(string? value, string paramName)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("Identity cannot be blank.", paramName);
    }
}

public readonly record struct SettlementId
{
    public string Value { get; }

    public SettlementId(string value) => Value = IdentityText.Normalize(value, nameof(value));

    public override string ToString() => Value;
}

public readonly record struct StationId
{
    public string Value { get; }

    public StationId(string value) => Value = IdentityText.Normalize(value, nameof(value));

    public override string ToString() => Value;
}

public readonly record struct SettlementTypeId
{
    public string Value { get; }

    public SettlementTypeId(string value) => Value = IdentityText.Normalize(value, nameof(value));

    public override string ToString() => Value;
}

public readonly record struct BuildingTypeId
{
    public string Value { get; }

    public BuildingTypeId(string value) => Value = IdentityText.Normalize(value, nameof(value));

    public override string ToString() => Value;
}

public readonly record struct NaturalResourceTypeId
{
    public string Value { get; }

    public NaturalResourceTypeId(string value) => Value = IdentityText.Normalize(value, nameof(value));

    public override string ToString() => Value;
}

public readonly record struct MapPosition(int X, int Y);
