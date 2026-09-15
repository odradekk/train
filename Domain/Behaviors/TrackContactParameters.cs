namespace Train.Domain.Behaviors;

public sealed record TrackContactParameters(double CoastDeceleration)
{
    public const double DefaultCoastDeceleration = 1;

    public static TrackContactParameters Default { get; } = new(DefaultCoastDeceleration);

    public static bool TryCreate(double coastDeceleration, out TrackContactParameters? parameters)
    {
        parameters = null;
        if (!double.IsFinite(coastDeceleration) || coastDeceleration <= 0)
            return false;
        parameters = new TrackContactParameters(coastDeceleration);
        return true;
    }
}
