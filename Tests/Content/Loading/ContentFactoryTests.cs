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
            new(fixture.Installation, new C.ContentLoadPurpose.Reload(candidate.View, null!)), fixture, fixture));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.PrepareLoad(
            new(fixture.Installation, new C.ContentLoadPurpose.Restore(null!, null!)), fixture, fixture));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.PrepareLoad(
            new(fixture.Installation with { UserLibrary = new C.ContentRoot("library") }, new C.ContentLoadPurpose.Initial()), fixture, fixture));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.EvaluateDesign(candidate.View, null!));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.WithSettlementCopies(candidate.View, []));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.CreateWorldInput(candidate.View, new("unused"), 1, "unused"));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.Capture(candidate.View, null!));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.CaptureReplay([candidate.View], null!));
        Assert.ThrowsExactly<NotImplementedException>(() => factory.OpenAuthoringPackage(ContentFixture.BuiltIn, new("core"), new("unused"), fixture));
    }
}
