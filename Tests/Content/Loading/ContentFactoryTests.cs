using Microsoft.VisualStudio.TestTools.UnitTesting;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class ContentFactoryTests
{
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
    public void Deferred_load_purposes_and_user_library_throw_before_fault_conversion()
    {
        var fixture = new ContentFixture();
        var factory = ContentFactory.Create();
        using var candidate = fixture.Load();
        Assert.ThrowsExactly<NotImplementedException>(() => factory.PrepareLoad(
            new(fixture.Installation with { UserLibrary = new C.ContentRoot("library") }, new C.ContentLoadPurpose.Initial()), fixture, fixture));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.PrepareLoad(
            new(fixture.Installation with { UserLibrary = new C.ContentRoot("library") }, new C.ContentLoadPurpose.Restore(null!, null!)), fixture, fixture));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.PrepareLoad(
            new(fixture.Installation with { UserLibrary = new C.ContentRoot("library") }, new C.ContentLoadPurpose.Reload(candidate.View, null!)), fixture, fixture));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.CaptureReplay([candidate.View], null!));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.OpenAuthoringPackage(ContentFixture.BuiltIn, new("core"), new("unused"), fixture));
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
}
