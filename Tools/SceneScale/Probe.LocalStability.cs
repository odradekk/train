namespace Train.SceneScale;

internal static partial class Probe
{
    internal sealed class LocalStabilityWindow(double positionStartedAt)
    {
        private LocalObservation? previous;
        private double? unchangedSince;
        internal double? UnchangedSinceSeconds => unchangedSince;

        internal void Track(LocalObservation observation)
        {
            if (previous is null || previous.objectSetSha256 != observation.objectSetSha256 ||
                previous.objectCount != observation.objectCount || previous.triangles != observation.triangles ||
                previous.triangleWorldCoordinatesMaterialSha256 != observation.triangleWorldCoordinatesMaterialSha256)
                unchangedSince = observation.seconds;
            previous = observation;
        }

        internal void Reset()
        {
            previous = null;
            unchangedSince = null;
        }

        internal bool IsStable(double seconds) => unchangedSince is { } start && seconds - start >= 30;
        internal bool TimedOut(double seconds) => seconds - positionStartedAt >= 300;
    }
}
