using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class WorldNoiseTests
{
    private static WorldTerrain Terrain(ResolvedGenerationConfiguration? configuration = null)
    {
        var input = Input;
        if (configuration is not null) input = input with { Configuration = configuration };
        input = WorldInputs.NormalizeInput(input);
        return new(input, new GenerationBudgetCounter(input.Configuration.Budget), 0);
    }

    private static double Noise(string family, ImmutableArray<NoiseOctave> octaves, double x, double y)
    {
        var input = WorldInputs.NormalizeInput(Input with
            { Configuration = Input.Configuration with { ElevationNoise = octaves } });
        var terrain = new WorldTerrain(input, new(input.Configuration.Budget), 0);
        return terrain.Noise(family, input.Configuration.ElevationNoise, x, y);
    }

    // Python 独立参考实现：little-endian struct 键、高 53 位、smoothstep、先 x 后 y。
    [TestMethod]
    [DataRow("elevation", 0.05290786737049469, 0.32620026175089134, 0.6419822735805266)]
    [DataRow("moisture", 0.15602001655973208, 0.453392130414328, 0.6174130284043526)]
    [DataRow("river", 0.4850476642459296, 0.5198609576369077, 0.7138905783214955)]
    public void Noise_matches_independent_lattice_fraction_and_multiple_octave_goldens(
        string family, double lattice, double fraction, double layered)
    {
        Assert.AreEqual(lattice, Noise(family, [new(8, 1)], 0, 0));
        Assert.AreEqual(fraction, Noise(family, [new(8, 1)], 2, 6));
        Assert.AreEqual(layered, Noise(family, [new(4, .25), new(16, .75)], -3, 9));
        Assert.AreEqual(layered, Noise(family, [new(16, .75), new(4, .25)], -3, 9));
    }

    [TestMethod]
    [DataRow("elevation")]
    [DataRow("moisture")]
    [DataRow("river")]
    public void Empty_and_zero_amplitude_noise_is_zero(string family)
    {
        Assert.AreEqual(0d, Noise(family, [], 3, -7));
        Assert.AreEqual(0d, Noise(family, [new(8, 0)], 3, -7));
    }

    [TestMethod]
    public void Band_selection_uses_closed_bounds_smallest_relief_and_ignores_permutation_and_exact_duplicates()
    {
        var flat = new TerrainGenerationBand(new("fixture:flat"), 0, 0, 0, 0, 0, 1, 1);
        var hills = flat with { Terrain = new("fixture:hills"), MaximumRelief = 1 };
        foreach (ImmutableArray<TerrainGenerationBand> bands in new ImmutableArray<TerrainGenerationBand>[]
                 { [flat, hills], [hills, flat], [flat, hills, flat] })
        {
            var sample = Terrain(Input.Configuration with { TerrainBands = bands }).Sample(new(5, 5));
            Assert.AreEqual(flat, sample.Band);
            Assert.AreEqual(flat.Terrain, sample.View.TypeId);
            Assert.AreEqual(0d, sample.View.Relief);
        }
    }

    [TestMethod]
    public void Missing_and_ambiguous_bands_return_configuration_failure()
    {
        var flat = new TerrainGenerationBand(new("fixture:flat"), 0, 1, 0, 1, 1, 1, 1);
        foreach (ImmutableArray<TerrainGenerationBand> bands in new ImmutableArray<TerrainGenerationBand>[]
                 { [flat with { MinimumElevation = .1 }], [flat, flat with { Terrain = new("fixture:other") }] })
        {
            var terrain = Terrain(Input.Configuration with { TerrainBands = bands });
            var failure = Assert.ThrowsExactly<GenerationFailure>(() => terrain.Sample(new(5, 5)));
            Assert.AreEqual("config_invalid", failure.Category);
            Assert.AreEqual("Configuration.TerrainBands", failure.Field);
        }
    }

    [TestMethod]
    public void Terrain_features_use_the_fixed_lake_and_river_identities()
    {
        var sample = Terrain(Input.Configuration with
        {
            LakeElevationThreshold = .1, RiverNoise = [new(8, 1)], RiverBandHalfWidth = .02
        }).Sample(new(0, 0));
        CollectionAssert.AreEqual(new[] { "lake", "river" }, sample.View.FeatureIds.ToArray());
    }
}
