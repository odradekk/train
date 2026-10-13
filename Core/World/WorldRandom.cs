using System.Text;
using Train.Core.Simulation;

namespace Train.Core.World;

internal static class WorldRandom
{
    internal static ulong Sample(ulong seed, string purpose, int stage, ulong worldRetry,
        ulong stageRetry, params ulong[] candidate)
        => KeyedRandom.Sample(new(seed, "world.stage1.v1." + purpose,
            [(ulong)stage, worldRetry, stageRetry, .. candidate]));

    internal static double Unit(ulong sample) => (sample >> 11) * (1.0 / (1UL << 53));

    internal static long Integer(ulong sample, long minimum, long maximum)
        => checked(minimum + (long)(((UInt128)sample * ((ulong)(maximum - minimum) + 1)) >> 64));

    internal static ulong Signed(long value) => unchecked((ulong)((value << 1) ^ (value >> 63)));

    internal static ulong[] Identity(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return [(ulong)bytes.Length, .. bytes.Select(b => (ulong)b)];
    }
}
