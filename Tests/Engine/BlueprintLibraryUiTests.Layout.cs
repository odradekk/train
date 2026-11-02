using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class BlueprintLibraryUiTests
{
    [Test]
    public async Task Long_draft_names_stay_readable_inside_cards_and_scaled_confirmation_dialogs()
    {
        var name = new string('名', 100);
        var draft = new SavedContentDraft(new(Guid.NewGuid()), new(1), new DraftTarget.Instance(new InstanceId.Vehicle(new(1))),
            new DraftPayload.Design(EditorPreviewSession.Draft with { Display = new(name, ""), Specification = new DesignSpecification.Vehicle(SizeClass.Small) }));
        var fixture = await Ready(Vehicles(), [draft]);
        var window = fixture.GetWindow(); var scale = window.ContentScaleFactor;
        try
        {
            var library = Library(fixture); var input = new UiTestInput(TestScene);
            await input.Activate(Button(library, "草稿")); await input.Frames(5);
            var card = library.FindChildren("DesignCard", "Button", true, false).OfType<Button>().Single();
            Require(card.FindChildren("*", "Label", true, false).OfType<Label>().All(label => card.GetGlobalRect().Encloses(label.GetGlobalRect())),
                "长名称及草稿来源不能越出卡片。");
            Require(card.TooltipText == name && Shown(library, name), "卡片缩略名称必须能在提示和详情中读到全文。");
            window.ContentScaleFactor = 2; await input.Frames(3);
            await input.Activate(Button(library, "丢弃…", exact: true));
            var dialog = library.GetChildren().OfType<ConfirmationDialog>().Single();
            Require(fixture.GetViewport().GetVisibleRect().Encloses(new Rect2(dialog.Position, dialog.Size)),
                "200% 缩放时，长名称的确认文本和两个按钮必须留在窗口内。");
            await input.Activate(dialog.GetCancelButton(), dialog);
        }
        finally { window.ContentScaleFactor = scale; fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
}
