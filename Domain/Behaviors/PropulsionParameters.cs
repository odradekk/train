namespace Train.Domain.Behaviors;

public sealed record PropulsionParameters(double TargetSpeed, double Acceleration)
{
    public const double DefaultTargetSpeed = 4;
    public const double DefaultAcceleration = 2;

    public static PropulsionParameters Default { get; } = new(DefaultTargetSpeed, DefaultAcceleration);

    public static bool TryCreate(double targetSpeed, double acceleration, out PropulsionParameters? parameters)
    {
        parameters = null;
        if (!double.IsFinite(targetSpeed) || targetSpeed <= 0)
            return false;
        if (!double.IsFinite(acceleration) || acceleration <= 0)
            return false;
        parameters = new PropulsionParameters(targetSpeed, acceleration);
        return true;
    }
}
