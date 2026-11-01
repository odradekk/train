using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Editing;

public sealed partial class EditorServiceTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void Preview_rejections_leave_operation_feedback_and_logs_unchanged(int outcome)
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var problem = new SceneProblem("region_limit", SceneStage.Edit, null, null, null, null, "hover rejection");
        SceneResult<IEditPreview> rejected = outcome == 0
            ? new SceneResult<IEditPreview>.Failure([problem]) : new SceneResult<IEditPreview>.Faulted(problem);
        fixture.Session.PreviewCall = _ => outcome == 2 ? throw new InvalidOperationException("hover fault") : rejected;
        var logs = fixture.Host.Logs.Count;
        var message = fixture.Editor.Message;
        var changes = 0;
        fixture.Editor.Changed += () => changes++;

        var result = fixture.Editor.Preview(new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Remove()));

        if (outcome == 2) Assert.IsNull(result);
        else Assert.AreSame(rejected, result);
        Assert.IsFalse(fixture.Editor.HasError, "悬停拒绝不能把编辑器设为操作失败。");
        Assert.AreEqual(message, fixture.Editor.Message);
        Assert.AreEqual(logs, fixture.Host.Logs.Count, "悬停查询不得写操作失败日志。");
        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    public void Reading_cached_state_does_not_poll_session_or_publish_changes()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var cached = fixture.Editor.State;
        var reads = 0;
        var changes = 0;
        fixture.Editor.Changed += () => changes++;
        fixture.Session.Change(Draft("external change"));
        var updated = fixture.Session.Read();
        fixture.Session.ReadCall = () => { reads++; return updated; };
        for (var index = 0; index < 3; index++)
        {
            Assert.AreSame(cached, fixture.Editor.State);
            Assert.IsFalse(fixture.Editor.IsDirty);
        }
        Assert.AreEqual(0, reads);
        Assert.AreEqual(0, changes);
        fixture.Editor.Process();
        Assert.AreEqual(1, reads);
        Assert.AreSame(updated, fixture.Editor.State);
        Assert.IsTrue(fixture.Editor.IsDirty);
    }
}
