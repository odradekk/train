using System.Collections.Immutable;
using static Train.Content.Tests.Loading.ContentCaptureTests;
using K = Train.Core.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentFactoryTests
{
    [TestMethod]
    public void Only_the_factory_is_exported_from_the_content_implementation() =>
        CollectionAssert.AreEqual(new[] { typeof(ContentFactory) }, typeof(ContentFactory).Assembly.GetExportedTypes());

    [TestMethod]
    public void Initial_candidate_can_be_activated_once_and_retained_after_disposal()
    {
        var fixture = new ContentFixture();
        var candidate = fixture.Load();
        var retained = candidate.View;
        Assert.AreEqual(1UL, retained.Generation.Value);
        Assert.IsNull(candidate.Basis);
        Assert.AreSame(retained, candidate.Activate());
        Assert.ThrowsExactly<InvalidOperationException>(() => candidate.Activate());
        candidate.Dispose();
        candidate.Dispose();
        Assert.AreEqual(1UL, retained.Generation.Value);
        Assert.ThrowsExactly<InvalidOperationException>(() => candidate.Activate());
    }

    [TestMethod]
    public void Abandoned_candidate_keeps_its_values_but_cannot_be_activated()
    {
        var fixture = new ContentFixture();
        var candidate = fixture.Load();
        var retained = candidate.View;
        candidate.Dispose();
        Assert.ThrowsExactly<InvalidOperationException>(() => candidate.Activate());
        Assert.AreEqual(1, retained.Packages.Length);
        Assert.IsInstanceOfType<C.ContentLookup<C.BlueprintRecord>.NotFound>(
            retained.FindBlueprint(new(C.BlueprintLibrary.Packages, new("core:absent"))));
    }

    [TestMethod]
    public void User_library_loads_and_invalid_restore_reload_arguments_throw_before_fault_conversion()
    {
        var fixture = new ContentFixture();
        var factory = ContentFactory.Create();
        using var candidate = fixture.Load();
        fixture.AddDirectory(new("library"), "");
        using var library = Success(factory.PrepareLoad(
            new(fixture.Installation with { UserLibrary = new C.ContentRoot("library") }, new C.ContentLoadPurpose.Initial()), fixture, fixture));
        Assert.AreEqual(0, library.View.Blueprints.Length);
        Assert.ThrowsExactly<ArgumentNullException>(() => factory.PrepareLoad(
            new(fixture.Installation with { UserLibrary = new C.ContentRoot("library") }, new C.ContentLoadPurpose.Restore(null!, null!)), fixture, fixture));
        Assert.ThrowsExactly<ArgumentNullException>(() => factory.PrepareLoad(
            new(fixture.Installation with { UserLibrary = new C.ContentRoot("library") }, new C.ContentLoadPurpose.Reload(candidate.View, null!)), fixture, fixture));
    }

    [TestMethod]
    [DataRow("resources/voxel")]
    [DataRow("resources/voxels/grain.voxel.JSON")]
    public void Unknown_definition_directories_and_misnamed_json_files_are_rejected(string path)
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            fixture.AddFile(ContentFixture.BuiltIn, path, "{}");
        else fixture.AddDirectory(ContentFixture.BuiltIn, path);
        var result = fixture.Prepare();
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Failure>(result);
        Assert.IsTrue(((C.ContentResult<C.IContentCandidate>.Failure)result).Diagnostics.Any(diagnostic =>
            diagnostic.Code == "unsupported_format" && diagnostic.Severity == C.ContentDiagnosticSeverity.Error && diagnostic.Source?.Path == path));
    }
    [TestMethod]
    public void Caller_errors_throw_before_loading_or_fault_conversion()
    {
        var fixture = WorldContentFixtures.Content();
        using var initial = fixture.Load();
        var factory = ContentFactory.Create();
        var references = References(initial.View, "engine");
        var saved = Success(factory.Capture(initial.View, references));
        Assert.ThrowsExactly<ArgumentNullException>(() => Prepare(new C.ContentLoadPurpose.Restore(null!, references)));
        Assert.ThrowsExactly<ArgumentNullException>(() => Prepare(new C.ContentLoadPurpose.Restore(saved, null!)));
        Assert.ThrowsExactly<ArgumentNullException>(() => Prepare(new C.ContentLoadPurpose.Reload(null!, references)));
        Assert.ThrowsExactly<ArgumentNullException>(() => Prepare(new C.ContentLoadPurpose.Reload(initial.View, null!)));
        Assert.ThrowsExactly<ArgumentException>(() => Prepare(new C.ContentLoadPurpose.Restore(new ForeignCapture(), references)));
        var foreign = new ForeignView();
        Assert.ThrowsExactly<ArgumentException>(() => Prepare(new C.ContentLoadPurpose.Reload(foreign, references)));
        Assert.ThrowsExactly<ArgumentException>(() => factory.CaptureReplay([foreign], references));
        Assert.ThrowsExactly<ArgumentNullException>(() => factory.CaptureReplay([null!], references));
        Assert.ThrowsExactly<ArgumentException>(() => factory.CaptureReplay(default, references));
        foreach (var invalid in new[] { references with { Designs = default }, references with { Templates = default },
            references with { Definitions = default }, references with { Inputs = default },
            references with { Templates = [Template(41) with { Plots = default }] } })
        {
            Assert.ThrowsExactly<ArgumentException>(() => factory.Capture(initial.View, invalid));
            Assert.ThrowsExactly<ArgumentException>(() => factory.CaptureReplay([initial.View], invalid));
            Assert.ThrowsExactly<ArgumentException>(() => Prepare(new C.ContentLoadPurpose.Restore(saved, invalid)));
            Assert.ThrowsExactly<ArgumentException>(() => Prepare(new C.ContentLoadPurpose.Reload(initial.View, invalid)));
        }
        C.ContentResult<C.IContentCandidate> Prepare(C.ContentLoadPurpose purpose) =>
            factory.PrepareLoad(new(fixture.Installation, purpose), fixture, fixture);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Default_arrays_returned_by_host_files_remain_faulted(bool read)
    {
        var fixture = new ContentFixture();
        fixture.AddCube();
        var result = ContentFactory.Create().PrepareLoad(new(fixture.Installation, new C.ContentLoadPurpose.Initial()),
            new DefaultFiles(fixture, read), fixture);
        Assert.IsInstanceOfType<C.ContentResult<C.IContentCandidate>.Faulted>(result);
    }

    private sealed class DefaultFiles(ContentFixture fixture, bool read) : C.IContentFiles
    {
        public C.ContentIoResult<ImmutableArray<C.ContentFileEntry>> Enumerate(C.ContentPath directory) =>
            read ? fixture.Enumerate(directory) : new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success(default);
        public C.ContentIoResult<ImmutableArray<byte>> Read(C.ContentPath file, long maximumBytes) =>
            new C.ContentIoResult<ImmutableArray<byte>>.Success(default);
    }

    private sealed class ForeignCapture : C.IContentCapture
    {
        public C.ContentRevision Revision => throw new InvalidOperationException();
        public ImmutableArray<K.DesignHash> Designs => throw new InvalidOperationException();
        public ImmutableArray<K.OwnedSettlementTemplateReference> Templates => throw new InvalidOperationException();
        public ImmutableArray<C.PackageMetadata> ReferencedPackages => throw new InvalidOperationException();
        public string Encode() => throw new InvalidOperationException("Foreign capture must not be encoded.");
    }

    private sealed class ForeignView : C.IContentView
    {
        public C.ContentGeneration Generation => throw new InvalidOperationException();
        public C.ContentRevision PersistentRevision => throw new InvalidOperationException();
        public K.CoreContentProjection Core => throw new InvalidOperationException();
        public ImmutableArray<C.PackageStatus> Packages => throw new InvalidOperationException();
        public ImmutableArray<C.VoxelCatalogEntry> Voxels => throw new InvalidOperationException();
        public ImmutableArray<C.BlueprintSummary> Blueprints => throw new InvalidOperationException();
        public ImmutableArray<C.SavedContentDraft> LocalDrafts => throw new InvalidOperationException();
        public ImmutableArray<C.GenerationProfileSummary> GenerationProfiles => throw new InvalidOperationException();
        public C.ContentLookup<C.BlueprintRecord> FindBlueprint(C.BlueprintAddress address) => throw new InvalidOperationException();
        public C.ContentLookup<C.DesignEvaluation> FindDesign(K.DesignHash hash) => throw new InvalidOperationException();
        public C.ContentLookup<C.VoxelAppearanceProjection> FindVoxelAppearance(K.VoxelDefinitionId definition) => throw new InvalidOperationException();
        public C.ContentLookup<C.VoxelDisplayProjection> FindVoxelDisplay(K.VoxelDefinitionId definition) => throw new InvalidOperationException();
        public C.ContentLookup<C.ContentDisplay> FindDefinitionDisplay(K.CoreDefinitionReference definition) => throw new InvalidOperationException();
        public C.ContentLookup<C.GoodPresentation> FindGood(K.GoodId good) => throw new InvalidOperationException();
        public C.ContentLookup<C.ContentAsset> FindAsset(C.AssetRevision revision) => throw new InvalidOperationException();
        public C.ContentLookup<C.SettlementLayoutProjection> FindSettlementLayout(K.SettlementId settlement) => throw new InvalidOperationException();
        public C.ContentLookup<C.CreatureAppearanceProjection> FindCreature(K.CreatureDefinitionId creature) => throw new InvalidOperationException();
        public C.ContentLookup<C.LairAppearanceProjection> FindLair(K.LairTypeId lair) => throw new InvalidOperationException();
        public C.ContentLookup<C.TerrainAppearanceProjection> FindTerrain(K.TerrainTypeId terrain) => throw new InvalidOperationException();
        public C.ContentLookup<C.RailwayAppearanceProjection> FindRailwayAppearance(K.RuleDefinitionId rule) => throw new InvalidOperationException();
    }

}
