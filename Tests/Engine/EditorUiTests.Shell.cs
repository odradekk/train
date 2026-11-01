using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class EditorUiTests
{
    [Test]
    public async Task Blueprint_library_is_visible_during_editing_and_after_successful_publication()
    {
        var fixture = await Ready();
        try
        {
            var ui = fixture.App.GetNode<GameUi>("GameUi");
            var shell = Find<GameShell>(ui, "*");
            var library = Find<BlueprintLibraryPanel>(ui, "*");
            Require(fixture.Editor.IsOpen && !ui.World.IsVisibleInTree(), "编辑器打开时世界外壳必须隐藏。");
            library.Open();
            await new UiTestInput(TestScene).Frames(3);
            var originalWidth = Find<PanelContainer>(library, "BlueprintLibrary").Size.X;
            Require(fixture.Editor.IsOpen && library.IsVisibleInTree() && library.GetParent() == ui.GetNode("Root") &&
                shell.WorkspaceContent is null, "编辑期间蓝图库必须显示在 Root，不能留在隐藏的世界外壳。");
            await new UiTestInput(TestScene).PressKey(Key.Escape);
            Require(!library.Visible && fixture.Editor.IsOpen,
                "根视口 Esc 应关闭独立蓝图库，不能顺带退出编辑器。");
            var basis = fixture.App.Host!.Content!;
            var published = new FixedContentView(basis.Core, basis.Generation, new(1)) { Blueprints = [], LocalDrafts = [], Voxels = [] };
            var candidate = new ScriptedContentCandidate(published, basis, ContentChange.Persistent,
                edit: new PreparedContentEdit.BlueprintSaved(new(BlueprintLibrary.Local, new("saved")), new("saved-hash")));
            ((ScriptedContentFactory)fixture.App.Host.ContentFactory).PrepareEditCall = (_, _) =>
                new ContentResult<IContentCandidate>.Success(candidate);
            var panel = EditorPanel(fixture);
            await ActivateEditorButton(panel, "SaveBlueprint");
            var dialog = panel.GetChildren().OfType<AcceptDialog>().Single(item => item.Title == "保存有效蓝图");
            await new UiTestInput(TestScene).Activate(dialog.GetOkButton(), dialog);
            await FramesUntil(TestScene, () => !fixture.Editor.IsOpen && !fixture.Editor.IsBusy);
            await new UiTestInput(TestScene).Frames(2);
            Require(candidate.Activations == 1 && ReferenceEquals(fixture.App.Host.Content, published),
                "保存按钮必须实际发布准备好的蓝图内容。");
            Require(ui.World.IsVisibleInTree() && library.IsVisibleInTree() && shell.WorkspaceContent == library,
                "发布成功退出编辑器后，蓝图库必须实际显示在世界外壳的工作区。");
            fixture.Editor.Open(EditorPreviewSession.Draft, new DraftTarget.LocalBlueprint(null));
            await FramesUntil(TestScene, () => fixture.Editor.Session is not null && !fixture.Editor.IsBusy);
            library.Open();
            await new UiTestInput(TestScene).Frames(4);
            Require(library.IsVisibleInTree() && library.GetParent() == ui.GetNode("Root") &&
                Find<PanelContainer>(library, "BlueprintLibrary").Size.X >= originalWidth,
                "蓝图库从停靠工作区回到编辑器独立显示时，必须恢复原有宽度。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }
}
