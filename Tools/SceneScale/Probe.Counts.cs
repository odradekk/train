namespace Train.SceneScale;
internal static partial class Probe
{
    private static readonly (int X, int Y, int Z)[] FaceNeighbors = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    internal sealed record FarCounts(long N, int C, long F)
    {
        internal HashSet<(int, int, int)> Chunks { get; init; } = [];
    }
    internal static FarCounts CountFar(IEnumerable<HashSet<(int X, int Y, int Z)>> buildings)
    {
        var chunks = new HashSet<(int, int, int)>();
        long n = 0, f = 0;
        foreach (var cells in buildings)
        foreach (var (x, y, z) in cells)
        {
            if ((double)x * x + (double)z * z <= 192 * 192) continue;
            n++;
            chunks.Add(((int)Math.Floor(x / 32.0), (int)Math.Floor(y / 32.0), (int)Math.Floor(z / 32.0)));
            foreach (var (dx, dy, dz) in FaceNeighbors)
                if (!cells.Contains((x + dx, y + dy, z + dz))) f++;
        }
        return new(n, chunks.Count, f) { Chunks = chunks };
    }
}
