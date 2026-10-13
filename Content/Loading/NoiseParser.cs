using System.Collections.Immutable;
using System.Text.Json;
using K = Train.Core.Contracts;
using static Train.Content.Loading.DefinitionJson;

namespace Train.Content.Loading;

internal static class NoiseParser
{
    internal static ImmutableArray<K.NoiseOctave> Parse(JsonElement value, string field) => Items(value, field).Select(item =>
    {
        Object(item.Value, item.Field, "scale_meters", "amplitude");
        return new K.NoiseOctave(Number(Required(item.Value, "scale_meters", item.Field), item.Field + "/scale_meters", positive: true),
            Number(Required(item.Value, "amplitude", item.Field), item.Field + "/amplitude", 0));
    }).OrderBy(x => x.ScaleMeters).ThenBy(x => x.Amplitude).ToImmutableArray();
}
