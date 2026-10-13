using System.Text.Json;

namespace Train.App.Measurement;

internal sealed record MemoryMeasurement(double ElapsedSeconds, long WorkingSetBytes, long PrivateBytes,
    double? GodotStaticBytes, double? VideoMemoryBytes);

internal sealed record MeasuredOperation(string Name, double ElapsedSeconds, double DurationSeconds,
    bool Succeeded, string? Problem = null);

internal static class MeasurementReport
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    internal static string Serialize(string scenario, IReadOnlyDictionary<string, object?> identity,
        MeasurementTiming timing, IEnumerable<MemoryMeasurement> memory, IEnumerable<MeasuredOperation> operations)
        => JsonSerializer.Serialize(new
        {
            scenario,
            scope = new { stage = 1, world = "small", machine = "development", content = "placeholder",
                final_performance = false, unmeasured = new[] { "full_demo_load", "target_hardware", "long_runs" } },
            identity,
            timing,
            memory = memory.Select(sample => new
            {
                sample.ElapsedSeconds, sample.WorkingSetBytes, sample.PrivateBytes,
                GodotStaticBytes = sample.GodotStaticBytes is { } engine ? (object)engine : "unavailable",
                VideoMemoryBytes = sample.VideoMemoryBytes is { } video ? (object)video : "unavailable"
            }),
            operations
        }, Json);
}
