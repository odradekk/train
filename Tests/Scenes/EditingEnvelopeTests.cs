using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class EditingEnvelopeTests
{
    private static readonly GridBounds Envelope = new(new(0, 0, 0), new(2, 2, 2));

    [TestMethod]
    public void AddedCellsOutsideEnvelopeRejectTheWholeRegionWithoutHistory()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(0, 0, 0)),
            SceneFixtures.Projection([]) with { AllowedEnvelope = Envelope });
        var before = session.Capture();
        var operation = new EditOperation.Cells(new EditFootprint.Box(new(new(0, 0, 0), new(3, 0, 0))),
            new CellEdit.Add(EditingFixtures.Cube));
        var preview = EditingFixtures.Preview(session, operation);
        Assert.IsFalse(preview.CanApply);
        Assert.IsTrue(preview.Conflicts.Any(conflict => conflict.Cell == new GridPosition(3, 0, 0)
            && conflict.Problem.Code == "outside_envelope"));
        Assert.AreEqual("outside_envelope", EditingFixtures.Code(session.Apply(operation)));
        Assert.AreEqual(before, session.Capture());
        Assert.IsFalse(session.Read().CanUndo);
    }

    [TestMethod]
    public void InitialEnvelopeStillAppliesAfterTheCurrentEvaluationIsCleared()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(),
            SceneFixtures.Projection([]) with { AllowedEnvelope = Envelope });
        EditingFixtures.Apply(session, EditingFixtures.Add(new(2, 2, 2)));
        Assert.IsNull(session.Read().CurrentEvaluation);
        Assert.IsFalse(EditingFixtures.Preview(session, EditingFixtures.Add(new(3, 2, 2))).CanApply);
    }

    [TestMethod]
    public void EnvelopeIsUnknownAfterSpecificationChangesUntilMatchingEvaluationIsAccepted()
    {
        var draft = EditingFixtures.Draft() with { Specification = new DesignSpecification.Vehicle(SizeClass.Small) };
        using var session = EditingFixtures.Open(draft,
            SceneFixtures.Projection([]) with { AllowedEnvelope = Envelope });
        EditingFixtures.Apply(session, new EditOperation.Properties(draft.Display, new DesignSpecification.Vehicle(SizeClass.Medium)));
        Assert.IsTrue(EditingFixtures.Preview(session, EditingFixtures.Add(new(3, 0, 0))).CanApply);
        var current = session.Capture();
        var evaluated = EditingFixtures.Evaluation(current.Draft) with
        { Scene = SceneFixtures.Projection([]) with { AllowedEnvelope = new(new(0, 0, 0), new(3, 3, 3)) } };
        Assert.IsTrue(session.AcceptEvaluation(current.Stamp, evaluated));
        Assert.IsTrue(EditingFixtures.Preview(session, EditingFixtures.Add(new(3, 0, 0))).CanApply);
        Assert.IsFalse(EditingFixtures.Preview(session, EditingFixtures.Add(new(4, 0, 0))).CanApply);
    }

    [TestMethod]
    public void ExistingOutsideCellsCanStillBeRemovedAndNullEnvelopeDoesNotRestrictAddition()
    {
        using var session = EditingFixtures.Open(EditingFixtures.Draft(new GridPosition(3, 0, 0)),
            SceneFixtures.Projection([]) with { AllowedEnvelope = Envelope });
        EditingFixtures.Apply(session, new EditOperation.Cells(new EditFootprint.Cell(new(3, 0, 0)), new CellEdit.Remove()));
        Assert.IsEmpty(session.Capture().Draft.Layout.Cells);
        using var unbounded = EditingFixtures.Open(EditingFixtures.Draft());
        EditingFixtures.Apply(unbounded, EditingFixtures.Add(new(-5, 20, 50)));
        Assert.HasCount(1, unbounded.Capture().Draft.Layout.Cells);
    }

    [TestMethod]
    public void NestedAdditionUsesRootEnvelopeAndReportsConflictInEnteredCoordinates()
    {
        var child = EditingFixtures.Draft() with { Specification = new DesignSpecification.Assembly(null) };
        var draft = EditingFixtures.Draft() with
        {
            Layout = new([], [], [new NestedDesignItem.AssemblyCopy(new(1, 1, 1), VoxelOrientation.PxPy, child)])
        };
        using var session = EditingFixtures.Open(draft,
            SceneFixtures.Projection([]) with { AllowedEnvelope = Envelope });
        EditingFixtures.Value(session.Enter(session.Read().Elements.Single().Id));
        EditingFixtures.Apply(session, EditingFixtures.Add(new(0, 0, 0)));
        var operation = EditingFixtures.Add(new(2, 0, 0));
        var preview = EditingFixtures.Preview(session, operation);
        Assert.IsFalse(preview.CanApply);
        Assert.AreEqual(new GridPosition(2, 0, 0), preview.Conflicts.Single().Cell);
        Assert.AreEqual("outside_envelope", EditingFixtures.Code(session.Apply(operation)));
    }
}
