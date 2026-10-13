using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Sessions;

namespace Train.Core.Tests.Sessions;

[TestClass]
public sealed class CoreLifecycleTests
{
    [TestMethod]
    public void Candidate_view_is_readable_before_activation()
    {
        using var candidate = new CoreSessionCandidate(new(new(0, 0), 1, new(0), Content('A')), 1);

        Assert.AreEqual(new SimulationPosition(0, 0), candidate.View.Position);
        Assert.AreEqual(new CoreRevision(0), candidate.View.Revision);
        Assert.AreEqual(new CoreInputId(new string('A', 64)), candidate.View.InputIdentity);
        Assert.IsNull(candidate.View.Fault);
    }

    [TestMethod]
    public void Candidate_can_only_be_activated_once()
    {
        using var candidate = new CoreSessionCandidate(new(new(0, 0), 1, new(0), Content('A')), 1);
        using var session = candidate.Activate();

        Assert.ThrowsExactly<InvalidOperationException>(() => candidate.Activate());
        Assert.AreEqual(new SimulationPosition(0, 0), session.Read().Position);
        Assert.AreEqual(new CoreRevision(0), session.Read().Revision);
        Assert.AreEqual(1UL, ((CoreSession)session).State.NextCommandNumber);
    }

    [TestMethod]
    public void Disposing_an_unactivated_candidate_abandons_it()
    {
        var candidate = new CoreSessionCandidate(new(new(0, 0), 1, new(0), Content('A')), 1);
        var retained = candidate.View;

        candidate.Dispose();
        candidate.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => candidate.Activate());
        Assert.AreEqual(new SimulationPosition(0, 0), retained.Position);
        Assert.AreEqual(new CoreRevision(0), retained.Revision);
        Assert.AreEqual(new CoreInputId(new string('A', 64)), retained.InputIdentity);
        Assert.IsNull(retained.Fault);
    }

    [TestMethod]
    public void Disposing_an_activated_candidate_leaves_the_session_owned_by_its_caller()
    {
        var candidate = new CoreSessionCandidate(new(new(0, 0), 1, new(0), Content('A')), 1);
        using var session = candidate.Activate();
        candidate.Dispose();
        candidate.Dispose();

        var advanced = session.Advance(1);

        Assert.AreEqual(1u, advanced.CompletedSteps);
        Assert.AreEqual(new SimulationPosition(1, 0), session.Read().Position);
        Assert.AreEqual(new CoreRevision(1), session.Read().Revision);
        Assert.AreEqual(1UL, ((CoreSession)session).State.NextCommandNumber);
        Assert.IsNull(session.Read().Fault);
    }

    [TestMethod]
    public void Disposed_sessions_reject_writes_and_leave_retained_views_readable()
    {
        using var candidate = new CoreSessionCandidate(new(new(0, 0), 1, new(0), Content('A')), 1);
        var session = candidate.Activate();
        var retained = session.Read();

        session.Dispose();
        session.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() =>
            session.Submit(new CoreCommand.PublishContent(Content('B')), CommandSource.AutomationTest));
        Assert.ThrowsExactly<ObjectDisposedException>(() => session.Advance(1));
        Assert.AreEqual(new SimulationPosition(0, 0), retained.Position);
        Assert.AreEqual(new CoreRevision(0), retained.Revision);
        Assert.AreEqual(new CoreInputId(new string('A', 64)), retained.InputIdentity);
        Assert.IsNull(retained.Fault);
    }

    private static CoreContentProjection Content(char identity)
        => new(new(new string(identity, 64)), [], [], [], [], [], [], [], [], [], [], [], [], [], []);
}
