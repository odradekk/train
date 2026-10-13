using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;

namespace Train.ContractSupport.Tests.Content;

[TestClass]
public sealed class HostContentScenarioTests
{
    [TestMethod]
    public void CapturePassesExactViewAndReferences()
    {
        var view = new FixedContentView(HostContentScenario.CreateProjection(new CoreInputId("fixture")), new(1), new(9));
        var references = new CoreReferenceManifest([], [], [], []);
        var capture = new FixedContentCapture("opaque", view.PersistentRevision);
        var factory = new ScriptedContentFactory
        {
            CaptureCall = (actualView, actualReferences) =>
            {
                Assert.AreSame(view, actualView);
                Assert.AreSame(references, actualReferences);
                return new ContentResult<IContentCapture>.Success(capture);
            }
        };
        Assert.AreSame(capture, ((ContentResult<IContentCapture>.Success)factory.Capture(view, references)).Value);
        Assert.AreEqual(1, factory.CaptureCount);
    }

    [TestMethod]
    public void CandidateActivatesOnceAndDisposeKeepsRetainedViewUsable()
    {
        var core = HostContentScenario.CreateProjection(new CoreInputId("fixture-a"));
        var generation = new ContentGeneration(7);
        var revision = new ContentRevision(3);
        var view = new FixedContentView(core, generation, revision);
        var basis = new FixedContentView(HostContentScenario.CreateProjection(new CoreInputId("fixture-b")));
        var report = new ContentPreparationReport([], [], [], []);
        var candidate = new ScriptedContentCandidate(view, basis, ContentChange.Persistent, report);

        Assert.AreSame(basis, candidate.Basis);
        Assert.AreSame(report, candidate.Report);
        Assert.AreEqual(ContentChange.Persistent, candidate.Changes);
        var retained = candidate.Activate();
        Assert.AreSame(view, retained);
        Assert.ThrowsExactly<InvalidOperationException>(() => candidate.Activate());
        candidate.Dispose();
        candidate.Dispose();
        Assert.AreEqual(1, candidate.Activations);
        Assert.AreEqual(1, candidate.Disposals);
        Assert.ThrowsExactly<ObjectDisposedException>(() => candidate.Activate());
        HostContentScenario.AssertRetainedView(retained, core, generation, revision);
    }

    [TestMethod]
    public void DisposedUnactivatedCandidateCannotActivate()
    {
        var candidate = new ScriptedContentCandidate(new FixedContentView(
            HostContentScenario.CreateProjection(new CoreInputId("fixture"))));
        candidate.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => candidate.Activate());
        Assert.AreEqual(0, candidate.Activations);
    }

    [TestMethod]
    public void FactoryPassesConfiguredInputsAndKeepsOpaqueCapture()
    {
        var view = new FixedContentView(HostContentScenario.CreateProjection(new CoreInputId("fixture")));
        var capture = new FixedContentCapture("opaque payload", new ContentRevision(9),
            [new DesignHash("fixture-design")]);
        var request = new ContentLoadRequest(new ContentInstallation(new ContentRoot("built-in"),
            new ContentRoot("mods"), null, "fixture", []), new ContentLoadPurpose.Initial());
        var files = new ScriptedContentFiles();
        var inspector = new UnconfiguredAssetInspector();
        var candidate = new ScriptedContentCandidate(view);
        var inputResult = new ContentResult<WorldGenerationInput>.Faulted("fixture", "configured", null);
        var profile = new GenerationProfileId("fixture-profile");
        var factory = new ScriptedContentFactory
        {
            PrepareLoadCall = (actualRequest, actualFiles, actualInspector) =>
            {
                Assert.AreSame(request, actualRequest);
                Assert.AreSame(files, actualFiles);
                Assert.AreSame(inspector, actualInspector);
                return new ContentResult<IContentCandidate>.Success(candidate);
            },
            DecodeCaptureCall = payload =>
            {
                Assert.AreEqual(capture.Encode(), payload);
                return new ContentResult<IContentCapture>.Success(capture);
            },
            CreateWorldInputCall = (actualView, actualProfile, seed, version) =>
            {
                Assert.AreSame(view, actualView);
                Assert.AreEqual(profile, actualProfile);
                Assert.AreEqual(11UL, seed);
                Assert.AreEqual("fixture-generator", version);
                return inputResult;
            },
            WithSettlementCopiesCall = (actualView, copies) =>
            {
                Assert.AreSame(view, actualView);
                Assert.IsTrue(copies.IsEmpty);
                return view;
            }
        };

        Assert.AreSame(candidate, ((ContentResult<IContentCandidate>.Success)
            factory.PrepareLoad(request, files, inspector)).Value);
        Assert.AreSame(capture, ((ContentResult<IContentCapture>.Success)
            factory.DecodeCapture(capture.Encode())).Value);
        Assert.AreSame(inputResult, factory.CreateWorldInput(view, profile, 11, "fixture-generator"));
        Assert.AreSame(view, factory.WithSettlementCopies(view, []));
        candidate.Dispose();
        Assert.AreEqual("opaque payload", capture.Encode());
        Assert.AreEqual(new ContentRevision(9), capture.Revision);
        CollectionAssert.AreEqual(new[] { new DesignHash("fixture-design") }, capture.Designs.ToArray());
    }

    [TestMethod]
    public void UnconfiguredFactoryEntriesAndViewQueriesFailExplicitly()
    {
        var factory = new ScriptedContentFactory();
        var view = new FixedContentView(HostContentScenario.CreateProjection(new CoreInputId("fixture")));
        AssertUnconfigured(() => factory.DecodeCapture("unexpected"));
        AssertUnconfigured(() => factory.Capture(view, new([], [], [], [])));
        AssertUnconfigured(() => factory.CreateWorldInput(view, new GenerationProfileId("unexpected"), 1, "v1"));
        AssertUnconfigured(() => factory.WithSettlementCopies(view, []));
        AssertUnconfigured(() => factory.PrepareLoad(new ContentLoadRequest(
            new ContentInstallation(new ContentRoot("built-in"), new ContentRoot("mods"), null, "fixture", []),
            new ContentLoadPurpose.Initial()), new ScriptedContentFiles(), new UnconfiguredAssetInspector()));
        AssertUnconfigured(() => factory.DecodeReplay("unexpected"));
        AssertUnconfigured(() => factory.CaptureReplay([view], new([], [], [], [])));
        AssertUnconfigured(() => view.FindDesign(new DesignHash("unexpected")));
        AssertUnconfigured(() => _ = view.Blueprints);
    }

    [TestMethod]
    public void UnconfiguredAssetInspectorFailsExplicitly()
    {
        AssertUnconfigured(() => new UnconfiguredAssetInspector().Inspect(default, default, []));
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }

}
