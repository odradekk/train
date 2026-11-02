using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Measurement;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Measurement;

[TestClass]
public sealed class SoakExpectationsTests
{
    [TestMethod]
    public void LatestBatchBeforeSaveKeepsScopeAndBounds()
    {
        using var recipe = JsonDocument.Parse("""
            {"expectations":[
              {"at":10,"key":"nodes","minimum":50},
              {"at":20,"key":"platforms","node":2,"minimum":4,"maximum":4},
              {"at":20,"key":"trains_queued","node":2,"minimum":6},
              {"at":40,"key":"nodes","minimum":100}]}
            """);
        var selected = SoakExpectations.Read(recipe.RootElement, 30);
        Assert.AreEqual(30UL, selected.SavedAt);
        Assert.AreEqual(20UL, selected.ExpectedAt);
        CollectionAssert.AreEqual(new[] { new SoakExpectation("platforms", new(2), 4, 4),
            new SoakExpectation("trains_queued", new(2), 6, null) }, selected.Items.ToArray());
        Assert.AreEqual(40UL, SoakExpectations.Read(recipe.RootElement, 40).ExpectedAt);
        Assert.Throws<InvalidDataException>(() => SoakExpectations.Read(recipe.RootElement, 9));
    }

    [TestMethod]
    public void ScopedCountsDoNotUseTheWorldTotal()
    {
        var terrain = new TerrainView(new("plain"), 0, []);
        var view = new FixedCoreReadView { World = new(1, new("rail"), 1, 1,
            [new(new(1), new(0, 0), terrain, new NodeOccupant.Empty(), [], 1, 1, null),
             new(new(2), new(1, 0), terrain, new NodeOccupant.Empty(), [], 4, 4, null)], [], [], [], []) };
        var rule = new SoakExpectations(30, 20, [new("platforms", new(2), 4, 4), new("platforms", null, 5, 5)]);
        var counts = rule.Count(view, new FixedContentView());
        Assert.AreEqual(4d, counts["node:2/platforms"]);
        Assert.AreEqual(5d, counts["platforms"]);
        Assert.AreEqual(0, rule.Failed(counts).Length);
    }

    [TestMethod]
    public void MissingCountsAndBothSidesOfInclusiveBoundsFail()
    {
        var item = new SoakExpectation("creatures", new(2), 4, 6);
        var rule = new SoakExpectations(30, 20, [item]);
        foreach (var value in new[] { 4d, 5d, 6d })
            Assert.AreEqual(0, rule.Failed(new Dictionary<string, double> { [item.Counter] = value }).Length);
        foreach (var value in new[] { 3d, 7d })
            CollectionAssert.AreEqual(new[] { item }, rule.Failed(new Dictionary<string, double> { [item.Counter] = value }));
        CollectionAssert.AreEqual(new[] { item }, rule.Failed(new Dictionary<string, double>()));
    }

    [TestMethod]
    public void ACountFromThePreviousSessionOrBeforeTheSampleIsRejected()
    {
        var binding = new HostBinding(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var count = new SoakCount(ImmutableSortedDictionary<string, double>.Empty.Add("creatures", 4), binding, 11, .1);
        var rule = new SoakExpectations(30, 20, [new("creatures", null, 4, 4)]);
        var window = new SoakLoadWindow();
        window.Begin(binding, 10);
        Assert.IsTrue(window.Observe(count, rule));
        Assert.AreEqual("full", window.LoadClass);
        window.Begin(binding, 20);
        Assert.IsNull(window.Reading);
        Assert.AreEqual("unknown", window.LoadClass);
        Assert.IsFalse(window.Observe(count, rule));
        Assert.IsFalse(window.Observe(count with { At = 20 }, rule));
        Assert.IsFalse(window.Observe(count with { At = 21, Binding = new(Guid.Parse("00000000-0000-0000-0000-000000000002")) }, rule));
        Assert.AreEqual("unknown", window.LoadClass);
        Assert.IsTrue(window.Observe(count with { At = 21 }, rule));
        Assert.AreEqual("full", window.LoadClass);
        Assert.IsTrue(window.Observe(count with { At = 22, Counts = count.Counts.SetItem("creatures", 3) }, rule));
        Assert.AreEqual("below-recipe", window.LoadClass);
        Assert.AreEqual("creatures", window.Failed.Single().Key);
    }
}
