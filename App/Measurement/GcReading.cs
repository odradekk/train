namespace Train.App.Measurement;

internal readonly record struct GcReading(TimeSpan Pause, int[] CollectionsByGeneration)
{
    internal static GcReading Current => new(GC.GetTotalPauseDuration(),
        Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray());

    internal GcReading Since(GcReading start) => new(Pause - start.Pause,
        [CollectionsByGeneration[0] - start.CollectionsByGeneration[0],
         CollectionsByGeneration[1] - start.CollectionsByGeneration[1],
         CollectionsByGeneration[2] - start.CollectionsByGeneration[2]]);
}
