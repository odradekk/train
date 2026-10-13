using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Core.World;

internal sealed record TerrainSample(TerrainView View, TerrainGenerationBand Band);

internal sealed class WorldTerrain(WorldGenerationInput input, GenerationBudgetCounter budget, ulong worldRetry)
{
    internal double SampleSpacing => input.Configuration.TerrainSampleSpacingMeters;
    internal int MaximumTracks => input.Configuration.InitialRailway.MaximumTracks;
    internal double Noise(string family, ImmutableArray<NoiseOctave> octaves, double x, double y)
    {
        var value = 0.0;
        budget.Sort(octaves.Length);
        foreach (var octave in octaves.OrderBy(o => o.ScaleMeters).ThenBy(o => o.Amplitude))
        {
            if (octave.Amplitude == 0) continue;
            budget.Work();
            var gx = Math.Floor(x / octave.ScaleMeters);
            var gy = Math.Floor(y / octave.ScaleMeters);
            if (!double.IsFinite(gx) || !double.IsFinite(gy) || gx < long.MinValue || gx >= (double)long.MaxValue
                || gy < long.MinValue || gy >= (double)long.MaxValue)
                throw new GenerationFailure("config_invalid", "Configuration." + family + "Noise");
            var ix = (long)gx;
            var iy = (long)gy;
            var tx = Smooth(x / octave.ScaleMeters - gx);
            var ty = Smooth(y / octave.ScaleMeters - gy);
            var a = Grid(ix, iy);
            var b = Grid(ix + 1, iy);
            var c = Grid(ix, iy + 1);
            var d = Grid(ix + 1, iy + 1);
            var lower = a + (b - a) * tx;
            var upper = c + (d - c) * tx;
            value += (lower + (upper - lower) * ty) * octave.Amplitude;

            double Grid(long px, long py) => WorldRandom.Unit(WorldRandom.Sample(input.WorldSeed,
                "noise." + family, 1, worldRetry, 0, BitConverter.DoubleToUInt64Bits(octave.ScaleMeters),
                WorldRandom.Signed(px), WorldRandom.Signed(py)));
        }
        if (!double.IsFinite(value))
            throw new GenerationFailure("config_invalid", "Configuration." + family + "Noise");
        return value;
    }

    internal TerrainSample Sample(MapPoint point)
    {
        budget.Work();
        var config = input.Configuration;
        var elevation = Noise("elevation", config.ElevationNoise, point.X, point.Y);
        var moisture = Noise("moisture", config.MoistureNoise, point.X, point.Y);
        var radius = config.TerrainSampleSpacingMeters;
        var samples = new[] { elevation,
            Elevation(Math.Max(0, point.X - radius), point.Y),
            Elevation(Math.Min(config.WidthMeters, point.X + radius), point.Y),
            Elevation(point.X, Math.Max(0, point.Y - radius)),
            Elevation(point.X, Math.Min(config.HeightMeters, point.Y + radius)) };
        var relief = samples.Max() - samples.Min();
        var matches = config.TerrainBands.Distinct().Where(b =>
        {
            budget.Work();
            return elevation >= b.MinimumElevation && elevation <= b.MaximumElevation
                && moisture >= b.MinimumMoisture && moisture <= b.MaximumMoisture && relief <= b.MaximumRelief;
        }).ToArray();
        if (matches.Length == 0)
            throw new GenerationFailure("config_invalid", "Configuration.TerrainBands");
        var smallest = matches.Min(b => b.MaximumRelief);
        var chosen = matches.Where(b => b.MaximumRelief == smallest).ToArray();
        if (chosen.Length != 1)
            throw new GenerationFailure("config_invalid", "Configuration.TerrainBands");
        var features = ImmutableArray.CreateBuilder<string>();
        if (elevation < config.LakeElevationThreshold) features.Add("lake");
        var amplitude = config.RiverNoise.Sum(o => o.Amplitude);
        if (!double.IsFinite(amplitude))
            throw new GenerationFailure("config_invalid", "Configuration.RiverNoise");
        if (amplitude > 0 && Math.Abs(Noise("river", config.RiverNoise, point.X, point.Y) - amplitude / 2)
            <= config.RiverBandHalfWidth) features.Add("river");
        return new(new(chosen[0].Terrain, relief, features.ToImmutable()), chosen[0]);

        double Elevation(double x, double y) => Noise("elevation", config.ElevationNoise, x, y);
    }

    private static double Smooth(double value) => value * value * (3 - 2 * value);
}
