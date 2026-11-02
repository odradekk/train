using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.ContractSupport.Tests.Scenes;

[TestClass]
public sealed class EditorScenarioTests
{
    [TestMethod]
    public void Old_snapshot_cannot_mark_new_revision_saved_or_accept_old_evaluation()
    {
        var draft = new DesignDraft(new("设计", ""), new DesignSpecification.SceneModel(null), new([], [], []));
        var evaluation = new DesignEvaluation.Complete(new("design"), new("composition"), draft, null,
            new(null, null, null, true, null, null, [], [], [], [], []), true, []);
        var editors = new EditorScenes();
        var factory = new FixedDisplayFactory { OpenEditorCall = editors.OpenEditor };
        var result = factory.OpenEditor(new FixedContentView { Generation = new(1) }, evaluation);
        var session = (EditorSession)((SceneResult<IDesignEditSession>.Success)result).Value;
        Assert.AreSame(editors.Session, session);
        var old = session.Capture();
        Assert.IsInstanceOfType<SceneResult<EditState>.Success>(session.Apply(new EditOperation.Properties(new("修改", ""), draft.Specification)));
        Assert.IsFalse(session.MarkSaved(old));
        Assert.IsFalse(session.AcceptEvaluation(old.Stamp, evaluation));
        Assert.IsTrue(session.Read().IsDirty);
        Assert.IsNull(session.Read().CurrentEvaluation);
        var current = session.Capture();
        Assert.IsTrue(session.AcceptEvaluation(current.Stamp, evaluation));
        Assert.IsTrue(session.MarkSaved(current));
        Assert.IsFalse(session.Read().IsDirty);
        Assert.AreSame(evaluation, session.Read().CurrentEvaluation);
        Assert.ThrowsExactly<InvalidOperationException>(() => session.Undo());
    }
}
