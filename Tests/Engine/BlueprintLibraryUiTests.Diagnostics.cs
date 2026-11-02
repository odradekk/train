using System.Text.Json;
using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class BlueprintLibraryUiTests
{
    [Test]
    public async Task Failed_draft_evaluation_keeps_the_exception_in_a_warning_and_the_ui_message_readable()
    {
        const string failure = "fixture library evaluation failure";
        var draft = new SavedContentDraft(new(Guid.NewGuid()), new(1), new DraftTarget.LocalBlueprint(null),
            new DraftPayload.Design(EditorPreviewSession.Draft));
        var fixture = await Ready(drafts: [draft], configure: factory => factory.EvaluateDesignCall = (_, _) =>
            throw new InvalidOperationException(failure));
        try
        {
            var library = Library(fixture); var input = new UiTestInput(TestScene);
            await input.Activate(Button(library, "草稿", exact: true));
            await FramesUntil(TestScene, () => Shown(library, "设计属性与制造材料暂不可用"));
            using var logFile = new FileStream(fixture.App.Logger.JsonlPath!, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(logFile);
            var warnings = reader.ReadToEnd().Split('\n').Where(line => line.Contains(failure)).ToArray();
            Require(warnings.Length == 1, "草稿评估抛异常时必须留一条含原因的日志。");
            using var record = JsonDocument.Parse(warnings[0]);
            Require(record.RootElement.GetProperty("level").GetString() == "warning" &&
                record.RootElement.GetProperty("exception").GetProperty("message").GetString() == failure,
                "评估失败应记录警告及原异常。");
            Require(!Shown(library, failure), "技术异常文本不能直接出现在蓝图库中。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    [Test]
    public async Task Deleting_a_building_blueprint_explains_that_existing_buildings_are_unaffected()
    {
        var fixture = await Ready();
        try
        {
            var library = Library(fixture); var input = new UiTestInput(TestScene);
            await input.Activate(Button(library, "建筑", exact: true));
            await input.Activate(Button(library, "删除…", exact: true));
            var dialog = library.GetChildren().OfType<ConfirmationDialog>().Single();
            Require(dialog.DialogText.Contains("建筑") && dialog.DialogText.Contains("不受影响"),
                "删除建筑蓝图时应说明现有建筑不受影响。");
            await input.Activate(dialog.GetCancelButton(), dialog);
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
}
