using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Godot.Tests.Editing;

public sealed partial class EditorServiceTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Only_package_targets_supply_identity_when_opening_and_reevaluating(int kind)
    {
        using var fixture = new EditingFixture();
        DraftTarget target = kind switch
        {
            0 => new DraftTarget.LocalBlueprint(new("fixture:equipment")),
            1 => new DraftTarget.UserBlueprint(new("fixture:equipment")),
            2 => new DraftTarget.Instance(new InstanceId.Vehicle(new(31))),
            _ => new DraftTarget.PackageDefinition(new("fixture"), DefinitionKind.Blueprint, "equipment", new("source"))
        };
        CompositeDefinitionId? expected = kind == 3 ? new("fixture:equipment") : null;
        var identities = new ConcurrentQueue<CompositeDefinitionId?>();
        fixture.Host.Factory.EvaluateDesignCall = (_, draft, identity) =>
        {
            identities.Enqueue(identity);
            return new ContentResult<DesignEvaluation>.Success(Evaluation(draft));
        };
        var draft = Draft("equipment") with { Specification = new DesignSpecification.FunctionalComposite([], null) };
        fixture.Editor.Open(draft, target);
        fixture.PumpUntil(() => fixture.Editor.Session is not null && !fixture.Editor.IsBusy);
        Assert.AreEqual(expected, identities.Single());

        fixture.Session.Change(draft with { Display = new("changed equipment", "") });
        fixture.Editor.Process();
        fixture.PumpUntil(() => fixture.Editor.State?.CurrentEvaluation is DesignEvaluation.Complete
            { Definition.Display.Name: "changed equipment" });
        Assert.AreEqual(2, identities.Count);
        foreach (var identity in identities) Assert.AreEqual(expected, identity);
    }
}
