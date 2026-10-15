using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.World;

[TestClass]
public sealed class ConstructionCapabilityTests
{
    [TestMethod]
    [DataRow(PlotSize.SmallStation)]
    [DataRow(PlotSize.LargeStation)]
    [DataRow((PlotSize)(-1))]
    public void Input_rejects_construction_limits_outside_the_four_building_ranks(PlotSize plot)
        => Assert.ThrowsExactly<ArgumentException>(() => Prepare(content: WithLimits([new(4, plot, null)])));

    [TestMethod]
    public void Input_rejects_unknown_vehicle_limits_but_accepts_no_vehicle_capability()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Prepare(content: WithLimits([new(4, PlotSize.Medium, (SizeClass)(-1))])));
        using var candidate = Prepare(content: WithLimits([new(4, PlotSize.Medium, null)]));
        Assert.IsTrue(candidate.MissingReferences.IsEmpty);
    }

    [TestMethod]
    public void Construction_limits_are_canonical_and_preserved_by_restore_and_strict_replay()
    {
        PartCapability.Construction[] limits = [new(4, PlotSize.Medium, null),
            new(4, PlotSize.Medium, SizeClass.Small), new(4, PlotSize.Giant, SizeClass.Titan)];
        var content = WithLimits(limits);
        using var candidate = Prepare(content: content);
        using var session = candidate.Activate();
        var initial = Success(session.Capture(CapturePurpose.Save));
        using var reordered = Prepare(content: WithLimits(limits.Reverse().ToArray()));
        using var reorderedSession = reordered.Activate();
        Assert.AreEqual(initial.Encode(), Success(reorderedSession.Capture(CapturePurpose.Save)).Encode());

        var factory = CoreFactory.Create();
        var decoded = Success(factory.DecodeState(initial.Encode()));
        CollectionAssert.AreEqual(limits, ((CoreCapture)decoded).World.Designs[0].Parts[0].Capabilities
            .OfType<PartCapability.Construction>().ToArray());
        using var restoreCandidate = Success(factory.PrepareRestore(decoded, content, Options));
        using var restored = restoreCandidate.Activate();
        Assert.AreEqual(initial.Checkpoint, Success(restored.Capture(CapturePurpose.Save)).Checkpoint);

        var changed = WithLimits([new(4, PlotSize.Large, SizeClass.Large)]) with { Identity = new(new string('D', 64)) };
        var publication = (SubmissionResult.Completed)session.Submit(new CoreCommand.PublishContent(changed), CommandSource.AutomationTest);
        Assert.IsInstanceOfType<CommandDecision.Accepted>(publication.Receipt.Decision);
        session.Advance(2);
        var expected = Success(session.Capture(CapturePurpose.Save));
        var recording = session.ReadRecording();
        using var replay = Success(factory.PrepareReplay(new(2, Options.Runtime, initial.Encode(), recording.Commands,
            [expected.Checkpoint], recording.EndPosition, null), Options.Runtime, [content, changed]));
        Assert.AreEqual(ReplayStop.EndOfRecording, replay.Advance(2).Stop);
        Assert.AreEqual(expected.Checkpoint, replay.CaptureBoundary().Checkpoint);
    }

    private static CoreContentProjection WithLimits(params PartCapability.Construction[] limits) => Content with
    {
        Identity = new(new string('C', 64)),
        Designs = Content.Designs.Select(design => design with
        {
            Parts = design.Parts.Select(part => part with
                { Capabilities = part.Capabilities.AddRange(limits) }).ToImmutableArray()
        }).ToImmutableArray()
    };
}
