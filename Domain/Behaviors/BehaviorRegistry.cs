using System.Collections.ObjectModel;

namespace Train.Domain.Behaviors;

public static class BehaviorTypes
{
    public const string Propulsion = "propulsion";
    public const string Control = "control";
    public const string TrackContact = "track_contact";
}

public static class CapabilityTypes
{
    public const string Propulsion = "propulsion";
    public const string Control = "control";
    public const string TrackContact = "track_contact";
}

public static class BehaviorRegistry
{
    private static readonly IReadOnlyDictionary<string, string> CapabilitiesByBehavior =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BehaviorTypes.Propulsion] = CapabilityTypes.Propulsion,
            [BehaviorTypes.Control] = CapabilityTypes.Control,
            [BehaviorTypes.TrackContact] = CapabilityTypes.TrackContact
        });

    public static bool IsRegistered(string type) =>
        type is not null && CapabilitiesByBehavior.ContainsKey(type);

    public static bool TryGetCapability(string type, out string capability)
    {
        capability = "";
        return type is not null && CapabilitiesByBehavior.TryGetValue(type, out capability!);
    }

    public static IReadOnlyList<string> CapabilitiesFrom(IEnumerable<string> behaviors)
    {
        ArgumentNullException.ThrowIfNull(behaviors);
        var capabilities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var behavior in behaviors)
        {
            if (TryGetCapability(behavior, out var capability))
                capabilities.Add(capability);
        }

        return capabilities.ToArray();
    }

    public static bool TryNormalizePropulsion(
        double? targetSpeed,
        double? acceleration,
        out PropulsionParameters parameters)
    {
        var target = targetSpeed ?? PropulsionParameters.DefaultTargetSpeed;
        var accel = acceleration ?? PropulsionParameters.DefaultAcceleration;
        if (!PropulsionParameters.TryCreate(target, accel, out var created) || created is null)
        {
            parameters = PropulsionParameters.Default;
            return false;
        }

        parameters = created;
        return true;
    }

    public static bool TryNormalizeTrackContact(
        double? coastDeceleration,
        out TrackContactParameters parameters)
    {
        var deceleration = coastDeceleration ?? TrackContactParameters.DefaultCoastDeceleration;
        if (!TrackContactParameters.TryCreate(deceleration, out var created) || created is null)
        {
            parameters = TrackContactParameters.Default;
            return false;
        }

        parameters = created;
        return true;
    }
}
