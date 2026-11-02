using System.Collections.Immutable;
using System.Text.Json;
using Train.Core.Contracts;

namespace Train.Core.Sessions;

internal static partial class CoreCommandEncoding
{
    private static void WriteFormation(Utf8JsonWriter writer, ImmutableArray<VehicleId> vehicles)
    {
        if (vehicles.IsDefault) throw new ArgumentException("数组必须初始化。", "command");
        writer.WriteStartArray("ordered_vehicles");
        foreach (var vehicle in vehicles)
        {
            if (vehicle.Value == 0) throw new ArgumentException("世界身份必须非零。", "command");
            writer.WriteNumberValue(vehicle.Value);
        }
        writer.WriteEndArray();
    }

    private static ImmutableArray<VehicleId> ReadFormation(JsonElement root)
    {
        var vehicles = Property(root, "ordered_vehicles", "", JsonValueKind.Array);
        var builder = ImmutableArray.CreateBuilder<VehicleId>();
        foreach (var vehicle in vehicles.EnumerateArray())
            builder.Add(new(Identity(vehicle, $"/ordered_vehicles/{builder.Count}")));
        return builder.ToImmutable();
    }
}
