using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;

namespace Train.ContractSupport.Tests.Content;

[TestClass]
public sealed class HostContentScenarioTests
{
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
        var inspector = new UnusedInspector();
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
        AssertUnconfigured(() => factory.CreateWorldInput(view, new GenerationProfileId("unexpected"), 1, "v1"));
        AssertUnconfigured(() => factory.WithSettlementCopies(view, []));
        AssertUnconfigured(() => factory.PrepareLoad(new ContentLoadRequest(
            new ContentInstallation(new ContentRoot("built-in"), new ContentRoot("mods"), null, "fixture", []),
            new ContentLoadPurpose.Initial()), new ScriptedContentFiles(), new UnusedInspector()));
        AssertUnconfigured(() => factory.DecodeReplay("unexpected"));
        AssertUnconfigured(() => view.FindDesign(new DesignHash("unexpected")));
        AssertUnconfigured(() => _ = view.Blueprints);
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }

    private sealed class UnusedInspector : IAssetInspector
    {
        public AssetInspection Inspect(AssetKind kind, AssetRevision revision, ImmutableArray<byte> bytes) =>
            throw new InvalidOperationException("UNCONFIGURED: 资产检查未配置。");
    }
}
