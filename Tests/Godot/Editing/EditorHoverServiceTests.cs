using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Logging;
using Train.Scenes.Contracts;

namespace Train.Godot.Tests.Editing;

public sealed partial class EditorServiceTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void Preview_rejections_leave_operation_feedback_and_logs_unchanged(int outcome)
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var problem = new SceneProblem("region_limit", SceneStage.Edit, null, null, null, null, "hover rejection");
        SceneResult<IEditPreview> rejected = outcome == 0
            ? new SceneResult<IEditPreview>.Failure([problem]) : new SceneResult<IEditPreview>.Faulted(problem);
        fixture.Session.PreviewCall = _ => rejected;
        var logs = fixture.Host.Logs.Count;
        var message = fixture.Editor.Message;
        var changes = 0;
        fixture.Editor.Changed += () => changes++;

        var result = fixture.Editor.Preview(new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Remove()));

        Assert.AreSame(rejected, result);
        Assert.IsFalse(fixture.Editor.HasError, "悬停拒绝不能把编辑器设为操作失败。");
        Assert.AreEqual(message, fixture.Editor.Message);
        Assert.AreEqual(logs, fixture.Host.Logs.Count, "悬停查询不得写操作失败日志。");
        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    public void Preview_exception_is_logged_without_changing_operation_feedback()
    {
        using var fixture = new EditingFixture();
        fixture.Open();
        var error = new InvalidOperationException("hover fault");
        fixture.Session.PreviewCall = _ => throw error;
        var logs = fixture.Host.Logs.Count;
        var message = fixture.Editor.Message;
        var changes = 0;
        fixture.Editor.Changed += () => changes++;

        var result = fixture.Editor.Preview(new EditOperation.Cells(new EditFootprint.Cell(new(0, 0, 0)), new CellEdit.Remove()));

        var reported = fixture.Host.Logs.Skip(logs).ToArray();
        Assert.AreEqual(1, reported.Length, "一次抛异常的查询必须只记录一条日志。");
        Assert.AreEqual(LogLevel.Warning, reported[0].Level);
        Assert.AreEqual("editor_preview_failed", reported[0].EventId);
        Assert.AreEqual(error.Message, reported[0].Message);
        Assert.IsNotNull(reported[0].Exception);
        Assert.IsInstanceOfType<SceneResult<IEditPreview>.Faulted>(result);
        Assert.IsFalse(fixture.Editor.HasError);
        Assert.AreEqual(message, fixture.Editor.Message);
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
