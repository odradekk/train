using System.Globalization;
namespace Train.Domain.Composites;

public readonly record struct EntityId
{
    public ulong Value { get; }

    public EntityId(ulong value)
    {
        if (value == 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Runtime entity ids start at 1.");
        Value = value;
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
