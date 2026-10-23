using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class NodeCandidateAdmissionTests
{
    [TestMethod]
    public async Task Candidate_pumps_admit_bounded_batches_without_candidate_receipts()
    {
        var snapshot = NodeFixtures.Snapshot(platforms: 0, trains: [],
            content: NodeFixtures.Content(layout: NodeFixtures.Layout with { Plots = [] }));
        var entering = RailwayFixtures.Train(1, direction: Train.Core.Contracts.TravelDirection.Reverse, speed: 1);
        var visuals = Enumerable.Range(0, NodeStreaming.AdmissionVisualLimit)
            .Select(_ => (SceneVisual)new SceneVisual.Wheelset(1, 1, 1, 0)).ToImmutableArray();
        var geometry = WorldSceneSession.BuildGeometry(id => new(
            [new(id(""), null, SceneFixtures.Identity, null, visuals, [], [])], [], []));
        NodeScene.Part Part() => new("", geometry, ImmutableDictionary<GridColumn, double>.Empty);
        await using var vehicles = new SceneVehicles();
        var ids = new Dictionary<string, SceneObjectId>();
        await using var scene = new NodeScene(snapshot, NodeFixtures.NodeId, new(), key =>
            ids.TryGetValue(key, out var id) ? id : ids[key] = new((ulong)ids.Count + 1),
            vehicles, new(), _ => null,
            buildChunk: (_, _, _, token) => { token.ThrowIfCancellationRequested(); return Part(); },
            buildRail: (_, _, _, token) => { token.ThrowIfCancellationRequested(); return Part(); });
        await scene.BuildWork!;
        scene.Pump(enteringTrain: entering);
        // Let static builders drain without admitting their completed work or sending any receipt.
        while (scene.HasPendingBuilds)
        {
            await SceneFixtures.AwaitPendingWork(scene.PendingWork);
            await Task.Yield();
        }
        var previous = VisualCount();
        for (var batch = 0; batch < 3; batch++)
        {
            scene.Pump(enteringTrain: entering);
            var current = VisualCount();
            Assert.AreEqual(NodeStreaming.AdmissionVisualLimit, current - previous,
                "Each candidate Pump must admit one bounded batch even when no previous batch has a receipt.");
            previous = current;
        }

        int VisualCount() => scene.CandidateObjects(snapshot).Sum(item => item.Visuals.OfType<SceneVisual.Wheelset>().Count());
    }
}
