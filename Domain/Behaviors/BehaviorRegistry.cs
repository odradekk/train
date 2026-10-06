// 属于旧车辆能力层。
// 将内容 Behavior 映射为旧列车系统使用的 capability。
using System.Collections.ObjectModel;
using Train.Core.Content.Behaviors;

namespace Train.Domain.Behaviors;

public static class CapabilityTypes
{
    public const string Propulsion = "propulsion";
    public const string Control = "control";
    public const string TrackContact = "track_contact";
}

public static class LegacyBehaviorCapabilities
{
    private static readonly IReadOnlyDictionary<string, string> CapabilitiesByBehavior =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BehaviorTypes.Propulsion] = CapabilityTypes.Propulsion,
            [BehaviorTypes.Control] = CapabilityTypes.Control,
            [BehaviorTypes.TrackContact] = CapabilityTypes.TrackContact
        });

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
}
