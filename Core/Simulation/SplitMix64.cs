
namespace Train.Core.Simulation;

public static class SplitMix64
{
    private const ulong Increment = 0x9E3779B97F4A7C15UL;

    private const ulong FirstMultiplier = 0xBF58476D1CE4E5B9UL;

    private const ulong SecondMultiplier = 0x94D049BB133111EBUL;

    private const int FirstShift = 30;

    private const int SecondShift = 27;

    private const int FinalShift = 31;

    public static ulong Mix(ulong value)
    {
        unchecked
        {
            value += Increment;
            value = (value ^ (value >> FirstShift)) * FirstMultiplier;
            value = (value ^ (value >> SecondShift)) * SecondMultiplier;
            return value ^ (value >> FinalShift);
        }
    }
}
